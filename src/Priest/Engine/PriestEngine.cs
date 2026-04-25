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
    public const string SpecVersion = "2.2.0";

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
            request.Context, request.Memory, request.UserContext, request.Output, request.Config.MaxSystemChars);

        string? text = null;
        string? finishReason = null;
        int? inputTokens = null, outputTokens = null;
        PriestErrorModel? errorModel = null;

        try
        {
            var result = await adapter.CompleteAsync(messages, request.Config, request.Output, ct);
            text         = result.Text;
            finishReason = result.FinishReason;
            inputTokens  = result.InputTokens;
            outputTokens = result.OutputTokens;
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
            session.AppendTurn(TurnRole.User, request.Prompt);
            if (text is not null) session.AppendTurn(TurnRole.Assistant, text);
            await _sessionStore.SaveAsync(session, ct);
            sessionInfo = new(session.Id, isNew, session.Turns.Count);
        }

        var latencyMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startMs;

        UsageInfo? usage = null;
        if (inputTokens.HasValue || outputTokens.HasValue)
        {
            var total = (inputTokens ?? 0) + (outputTokens ?? 0);
            usage = new(inputTokens, outputTokens, total > 0 ? total : null, null);
        }

        var finishedReason = finishReason switch
        {
            "stop"   => FinishedReason.Stop,
            "length" => FinishedReason.Length,
            "error"  => FinishedReason.Error,
            not null => FinishedReason.Unknown,
            _        => (FinishedReason?)null,
        };

        return new PriestResponse(
            new ExecutionInfo(request.Config.Provider, request.Config.Model,
                latencyMs, request.Profile, finishedReason),
            request.Metadata)
        {
            Text    = text,
            Usage   = usage,
            Session = sessionInfo,
            Error   = errorModel,
        };
    }

    /// <summary>
    /// Yield text chunks as they arrive from the provider.
    ///
    /// Session is saved automatically after the stream completes.
    /// Unlike RunAsync(), StreamAsync() yields only raw text chunks — no final
    /// PriestResponse, no usage stats, no latency info.
    /// </summary>
    public async IAsyncEnumerable<string> StreamAsync(
        PriestRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_adapters.TryGetValue(request.Config.Provider, out var adapter))
            throw PriestException.ProviderNotRegistered(request.Config.Provider);

        var profile = _profileLoader.Load(request.Profile);
        var (session, _) = await ResolveSessionAsync(request, ct);

        var messages = ContextBuilder.BuildMessages(
            profile, session, request.Prompt,
            request.Context, request.Memory, request.UserContext, request.Output, request.Config.MaxSystemChars);

        var parts = new List<string>();
        await foreach (var chunk in adapter.StreamAsync(messages, request.Config, request.Output, ct))
        {
            parts.Add(chunk);
            yield return chunk;
        }

        if (session is not null && _sessionStore is not null && parts.Count > 0)
        {
            session.AppendTurn(TurnRole.User, request.Prompt);
            session.AppendTurn(TurnRole.Assistant, string.Concat(parts));
            await _sessionStore.SaveAsync(session, ct);
        }
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
