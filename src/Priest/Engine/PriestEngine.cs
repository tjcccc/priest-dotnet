using System.Runtime.CompilerServices;
using Priest.Errors;
using Priest.Profiles;
using Priest.Providers;
using Priest.Schema;
using Priest.Sessions;

namespace Priest.Engine;

/// <summary>
/// Orchestrates a single AI run.
///
/// The engine is stateless per-run — it holds no mutable state between calls.
///
/// Spec version this implementation targets: 2.8.0
/// </summary>
public class PriestEngine
{
    /// <summary>Spec version this implementation targets.</summary>
    public const string SpecVersion = "2.8.0";

    private readonly IProfileLoader _profileLoader;
    private readonly ISessionStore? _sessionStore;
    private readonly IReadOnlyDictionary<string, IProviderAdapter> _adapters;

    public PriestEngine(
        IProfileLoader profileLoader,
        ISessionStore? sessionStore = null,
        IReadOnlyDictionary<string, IProviderAdapter>? adapters = null)
    {
        _profileLoader = profileLoader;
        _sessionStore  = sessionStore;
        _adapters      = adapters ?? new Dictionary<string, IProviderAdapter>();
    }

    /// <summary>
    /// Execute a single request and return a structured response.
    ///
    /// Throws PriestException for PROVIDER_NOT_REGISTERED and SESSION_NOT_FOUND.
    /// All other provider errors are caught and placed into response.Error.
    /// </summary>
    public async Task<PriestResponse> RunAsync(PriestRequest request, CancellationToken ct = default)
    {
        var startMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (!_adapters.TryGetValue(request.Config.Provider, out var adapter))
            throw PriestException.ProviderNotRegistered(request.Config.Provider);

        var profile = _profileLoader.Load(request.Profile);
        var (session, isNew) = await ResolveSessionAsync(request, ct);

        // Compaction (spec 2.5.0): fold older turns before building messages.
        if (session is not null) await MaybeCompactAsync(session, request.Config, ct);

        var messages = ContextBuilder.BuildMessages(
            profile, session, request.Prompt,
            request.Context, request.Memory, request.UserContext, request.Output, request.Config.MaxSystemChars,
            request.ToolExchange, request.Config.SessionContextTurns);

        string? text = null;
        IList<ToolCall>? toolCalls = null;
        ReasoningInfo? reasoning = null;
        string? finishReason = null;
        int? inputTokens = null, outputTokens = null, cachedInputTokens = null, reasoningTokens = null;
        PriestErrorModel? errorModel = null;

        try
        {
            var result = await adapter.CompleteAsync(messages, request.Config, request.Output, CallOptions(request), ct);
            text         = result.Text;
            toolCalls    = result.ToolCalls is { Count: > 0 } ? result.ToolCalls : null;
            finishReason = result.FinishReason;
            inputTokens  = result.InputTokens;
            outputTokens = result.OutputTokens;
            cachedInputTokens = result.CachedInputTokens;
            reasoningTokens = result.ReasoningTokens;
            reasoning = result.Reasoning;
            if (toolCalls is not null) finishReason = "tool_calls";
        }
        catch (PriestException ex)
        {
            finishReason = "error";
            errorModel = new(ex.Code, ex.Message, ex.Details);
        }
        catch (Exception ex)
        {
            finishReason = "error";
            errorModel = new(PriestErrorCode.InternalError, ex.Message, new());
        }

        SessionInfo? sessionInfo = null;
        if (session is not null && _sessionStore is not null && errorModel is null)
        {
            // Tool-call iterations are turn-local: persist only when the model
            // produced a final answer (spec behavior/tool-calling.md).
            if (toolCalls is null)
            {
                session.AppendTurn(TurnRole.User, request.Prompt);
                if (text is not null) session.AppendTurn(TurnRole.Assistant, text);
                RecordChatUsage(session, request, inputTokens);
                await _sessionStore.SaveAsync(session, ct);
            }
            sessionInfo = new(session.Id, isNew, session.Turns.Count);
        }

        var latencyMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startMs;

        var usage = BuildUsage(inputTokens, outputTokens, cachedInputTokens, reasoningTokens);

        var finishedReason = MapFinishedReason(finishReason);

        return new PriestResponse(
            new ExecutionInfo(request.Config.Provider, request.Config.Model,
                latencyMs, request.Profile, finishedReason),
            request.Metadata)
        {
            Text      = text,
            ToolCalls = toolCalls,
            Reasoning = reasoning,
            Usage     = usage,
            Session   = sessionInfo,
            Error     = errorModel,
        };
    }

    /// <summary>
    /// Yield text chunks as they arrive from the provider.
    ///
    /// Implemented as a filter over StreamEventsAsync(): text deltas pass
    /// through, and a provider error in the terminal done event is re-thrown
    /// as a PriestException (preserving the legacy contract). Use
    /// StreamEventsAsync() for tool calls or structured metadata.
    /// </summary>
    public async IAsyncEnumerable<string> StreamAsync(
        PriestRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var ev in StreamEventsAsync(request, ct))
        {
            if (ev.Type == "text_delta" && ev.Text is not null)
                yield return ev.Text;
            else if (ev.Type == "done" && ev.Response?.Error is { } error)
                throw new PriestException(error.Code, error.Message, error.Details);
        }
    }

    /// <summary>
    /// Yield structured streaming events (spec 2.4.0): text deltas, tool-call
    /// progress, usage, and a terminal done event carrying the full
    /// PriestResponse. Provider errors surface in done.Response.Error rather
    /// than being thrown, matching RunAsync() semantics.
    /// </summary>
    public async IAsyncEnumerable<PriestStreamEvent> StreamEventsAsync(
        PriestRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var startMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (!_adapters.TryGetValue(request.Config.Provider, out var adapter))
            throw PriestException.ProviderNotRegistered(request.Config.Provider);

        var profile = _profileLoader.Load(request.Profile);
        var (session, isNew) = await ResolveSessionAsync(request, ct);

        if (session is not null) await MaybeCompactAsync(session, request.Config, ct);

        var messages = ContextBuilder.BuildMessages(
            profile, session, request.Prompt,
            request.Context, request.Memory, request.UserContext, request.Output, request.Config.MaxSystemChars,
            request.ToolExchange, request.Config.SessionContextTurns);

        var textParts = new List<string>();
        var toolCalls = new List<ToolCall>();
        ReasoningInfo? reasoning = null;
        string? finishReason = null;
        int? inputTokens = null, outputTokens = null, cachedInputTokens = null, reasoningTokens = null;
        PriestErrorModel? errorModel = null;

        var source = adapter.StreamEventsAsync(messages, request.Config, request.Output, CallOptions(request), ct);
        await using var enumerator = source.GetAsyncEnumerator(ct);
        while (true)
        {
            AdapterStreamEvent? ev = null;
            try
            {
                if (!await enumerator.MoveNextAsync()) break;
                ev = enumerator.Current;
            }
            catch (PriestException ex)
            {
                finishReason = "error";
                errorModel = new(ex.Code, ex.Message, ex.Details);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                finishReason = "error";
                errorModel = new(PriestErrorCode.InternalError, ex.Message, new());
                break;
            }

            switch (ev.Type)
            {
                case "text_delta" when ev.Text is not null:
                    textParts.Add(ev.Text);
                    yield return new PriestStreamEvent("text_delta") { Text = ev.Text };
                    break;
                case "reasoning_summary_delta" when ev.Text is not null:
                    yield return new PriestStreamEvent("reasoning_summary_delta") { Text = ev.Text };
                    break;
                case "tool_call_start":
                    yield return new PriestStreamEvent("tool_call_start") { Index = ev.Index, Id = ev.Id, Name = ev.Name };
                    break;
                case "tool_call_delta":
                    yield return new PriestStreamEvent("tool_call_delta") { Index = ev.Index, ArgumentsDelta = ev.ArgumentsDelta };
                    break;
                case "tool_call_end" when ev.ToolCall is not null:
                    toolCalls.Add(ev.ToolCall);
                    yield return new PriestStreamEvent("tool_call_end") { Index = ev.Index, ToolCall = ev.ToolCall };
                    break;
                case "usage":
                    inputTokens = ev.InputTokens ?? inputTokens;
                    outputTokens = ev.OutputTokens ?? outputTokens;
                    cachedInputTokens = ev.CachedInputTokens ?? cachedInputTokens;
                    reasoningTokens = ev.ReasoningTokens ?? reasoningTokens;
                    yield return new PriestStreamEvent("usage")
                    {
                        Usage = BuildUsage(inputTokens, outputTokens, cachedInputTokens, reasoningTokens),
                    };
                    break;
                case "finish":
                    finishReason = ev.FinishReason ?? finishReason;
                    reasoning = ev.Reasoning ?? reasoning;
                    break;
            }
        }

        var text = textParts.Count > 0 ? string.Concat(textParts) : null;
        if (toolCalls.Count > 0 && finishReason != "error") finishReason = "tool_calls";

        SessionInfo? sessionInfo = null;
        if (session is not null && _sessionStore is not null && errorModel is null)
        {
            if (toolCalls.Count == 0 && text is not null)
            {
                session.AppendTurn(TurnRole.User, request.Prompt);
                session.AppendTurn(TurnRole.Assistant, text);
                RecordChatUsage(session, request, inputTokens);
                await _sessionStore.SaveAsync(session, ct);
            }
            sessionInfo = new(session.Id, isNew, session.Turns.Count);
        }

        var response = new PriestResponse(
            new ExecutionInfo(request.Config.Provider, request.Config.Model,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startMs, request.Profile,
                MapFinishedReason(finishReason)),
            request.Metadata)
        {
            Text      = text,
            ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
            Reasoning = reasoning,
            Usage     = BuildUsage(inputTokens, outputTokens, cachedInputTokens, reasoningTokens),
            Session   = sessionInfo,
            Error     = errorModel,
        };

        yield return new PriestStreamEvent("done") { Response = response };
    }

    private static AdapterCallOptions? CallOptions(PriestRequest request)
        => request.Tools.Count > 0 ? new AdapterCallOptions(request.Tools, request.ToolChoice) : null;

    private static FinishedReason? MapFinishedReason(string? finishReason) => finishReason switch
    {
        "stop"       => FinishedReason.Stop,
        "length"     => FinishedReason.Length,
        "content_filter" => FinishedReason.ContentFilter,
        "tool_calls" => FinishedReason.ToolCalls,
        "error"      => FinishedReason.Error,
        not null     => FinishedReason.Unknown,
        _            => null,
    };

    private static UsageInfo? BuildUsage(
        int? inputTokens,
        int? outputTokens,
        int? cachedInputTokens = null,
        int? reasoningTokens = null)
    {
        if (!inputTokens.HasValue && !outputTokens.HasValue
            && !cachedInputTokens.HasValue && !reasoningTokens.HasValue) return null;
        var total = (inputTokens ?? 0) + (outputTokens ?? 0);
        return new(
            inputTokens,
            outputTokens,
            total > 0 ? total : null,
            cachedInputTokens,
            null,
            reasoningTokens);
    }

    // ---- Conversation compaction (spec 2.5.0) ----

    /// <summary>
    /// Compact a session on demand: fold older turns into the running summary,
    /// keeping the most recent CompactionKeepTurns. Returns whether anything was
    /// folded and the new coverage point. Throws SESSION_NOT_FOUND for unknown ids.
    /// </summary>
    public async Task<(bool Compacted, int SummarizedThrough)> CompactSessionAsync(
        string sessionId, PriestConfig config, CancellationToken ct = default)
    {
        if (_sessionStore is null) return (false, 0);
        var session = await _sessionStore.GetAsync(sessionId, ct)
            ?? throw PriestException.SessionNotFound(sessionId);
        var compacted = await CompactAsync(session, config, ct);
        return (compacted, session.GetCompaction().SummarizedThrough);
    }

    /// <summary>
    /// Record a turn's input size as the compaction trigger signal. Skipped when
    /// the turn replays a tool exchange (its input is inflated by tool context).
    /// </summary>
    private static void RecordChatUsage(Session session, PriestRequest request, int? inputTokens)
    {
        if (request.ToolExchange.Count > 0) return;
        session.RecordInputTokens(inputTokens);
    }

    /// <summary>Compact before a turn when the previous turn's input usage crossed the budget.</summary>
    private async Task MaybeCompactAsync(Session session, PriestConfig config, CancellationToken ct)
    {
        if (_sessionStore is null) return;
        if (!Compactor.ShouldCompact(session.GetCompaction().LastInputTokens, config.MaxContextTokens)) return;
        await CompactAsync(session, config, ct);
    }

    /// <summary>Fold turns into the summary via a provider summarization call; persists the result.</summary>
    private async Task<bool> CompactAsync(Session session, PriestConfig config, CancellationToken ct)
    {
        if (_sessionStore is null) return false;
        var keepTurns = config.CompactionKeepTurns ?? Compactor.DefaultCompactionKeepTurns;
        var existing = session.GetCompaction();
        var plan = Compactor.PlanCompaction(session.Turns, existing.SummarizedThrough, keepTurns);
        if (plan is null) return false;

        if (!_adapters.TryGetValue(config.Provider, out var adapter))
            throw PriestException.ProviderNotRegistered(config.Provider);

        var messages = Compactor.BuildSummaryMessages(existing.Summary, plan.ToSummarize);
        var summaryConfig = new PriestConfig(config.Provider, config.Model)
        {
            Timeout = config.Timeout,
            MaxOutputTokens = config.MaxOutputTokens ?? Compactor.SummaryMaxOutputTokens,
            ProviderOptions = config.ProviderOptions,
        };
        var result = await adapter.CompleteAsync(messages, summaryConfig, new OutputSpec(), null, ct);
        var summary = (result.Text ?? "").Trim();
        if (summary.Length == 0) return false;

        session.ApplyCompaction(summary, plan.SummarizedThrough);
        await _sessionStore.SaveAsync(session, ct);
        return true;
    }

    private async Task<(Session? session, bool isNew)> ResolveSessionAsync(
        PriestRequest request, CancellationToken ct)
    {
        if (request.Session is null || _sessionStore is null)
            return (null, false);

        var @ref = request.Session;

        if (@ref.ContinueExisting)
        {
            var existing = await _sessionStore.GetAsync(@ref.Id, ct);
            if (existing is not null) return (existing, false);
            if (@ref.CreateIfMissing)
            {
                var s = await _sessionStore.CreateAsync(request.Profile, @ref.Id, ct: ct);
                return (s, true);
            }
            throw PriestException.SessionNotFound(@ref.Id);
        }
        else
        {
            var s = await _sessionStore.CreateAsync(request.Profile, ct: ct);
            return (s, true);
        }
    }
}
