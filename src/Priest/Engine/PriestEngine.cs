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
/// Spec version this implementation targets: 1.0.0
/// </summary>
public class PriestEngine
{
    /// <summary>Spec version this implementation targets.</summary>
    public const string SpecVersion = "2.4.0";

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

        var messages = ContextBuilder.BuildMessages(
            profile, session, request.Prompt,
            request.Context, request.Memory, request.UserContext, request.Output, request.Config.MaxSystemChars,
            request.ToolExchange);

        string? text = null;
        IList<ToolCall>? toolCalls = null;
        string? finishReason = null;
        int? inputTokens = null, outputTokens = null;
        PriestErrorModel? errorModel = null;

        try
        {
            var result = await adapter.CompleteAsync(messages, request.Config, request.Output, CallOptions(request), ct);
            text         = result.Text;
            toolCalls    = result.ToolCalls is { Count: > 0 } ? result.ToolCalls : null;
            finishReason = result.FinishReason;
            inputTokens  = result.InputTokens;
            outputTokens = result.OutputTokens;
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
                await _sessionStore.SaveAsync(session, ct);
            }
            sessionInfo = new(session.Id, isNew, session.Turns.Count);
        }

        var latencyMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startMs;

        var usage = BuildUsage(inputTokens, outputTokens);

        var finishedReason = MapFinishedReason(finishReason);

        return new PriestResponse(
            new ExecutionInfo(request.Config.Provider, request.Config.Model,
                latencyMs, request.Profile, finishedReason),
            request.Metadata)
        {
            Text      = text,
            ToolCalls = toolCalls,
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

        var messages = ContextBuilder.BuildMessages(
            profile, session, request.Prompt,
            request.Context, request.Memory, request.UserContext, request.Output, request.Config.MaxSystemChars,
            request.ToolExchange);

        var textParts = new List<string>();
        var toolCalls = new List<ToolCall>();
        string? finishReason = null;
        int? inputTokens = null, outputTokens = null;
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
                    yield return new PriestStreamEvent("usage") { Usage = BuildUsage(inputTokens, outputTokens) };
                    break;
                case "finish":
                    finishReason = ev.FinishReason ?? finishReason;
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
            Usage     = BuildUsage(inputTokens, outputTokens),
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
        "tool_calls" => FinishedReason.ToolCalls,
        "error"      => FinishedReason.Error,
        not null     => FinishedReason.Unknown,
        _            => null,
    };

    private static UsageInfo? BuildUsage(int? inputTokens, int? outputTokens)
    {
        if (!inputTokens.HasValue && !outputTokens.HasValue) return null;
        var total = (inputTokens ?? 0) + (outputTokens ?? 0);
        return new(inputTokens, outputTokens, total > 0 ? total : null, null);
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
