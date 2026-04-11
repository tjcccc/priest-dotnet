using System.Text.Json.Nodes;

namespace Priest.Schema;

public enum FinishedReason { Stop, Length, Error, Unknown }

/// <summary>Execution metadata for a completed run.</summary>
public record ExecutionInfo(
    string Provider,
    string Model,
    long? LatencyMs,
    string Profile,
    FinishedReason? FinishedReason);

/// <summary>Token usage reported by the provider.</summary>
public record UsageInfo(
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    double? EstimatedCostUsd);

/// <summary>Session state after a run.</summary>
public record SessionInfo(
    string Id,
    bool IsNew,
    int TurnCount);

/// <summary>
/// Structured error placed into PriestResponse when a provider call fails.
/// Distinct from thrown PriestException (which is for PROVIDER_NOT_REGISTERED
/// and SESSION_NOT_FOUND — errors where no response can be constructed).
/// </summary>
public record PriestErrorModel(
    string Code,
    string Message,
    Dictionary<string, string> Details);

/// <summary>Result of a single engine run.</summary>
public class PriestResponse
{
    /// <summary>Raw text returned by the provider. Null on error or no content.</summary>
    public string? Text { get; init; }

    public ExecutionInfo Execution { get; init; }
    public UsageInfo? Usage { get; init; }
    public SessionInfo? Session { get; init; }
    public PriestErrorModel? Error { get; init; }
    public Dictionary<string, JsonNode?> Metadata { get; init; }

    /// <summary>True when Error is null.</summary>
    public bool Ok => Error is null;

    public PriestResponse(ExecutionInfo execution, Dictionary<string, JsonNode?> metadata)
    {
        Execution = execution;
        Metadata = metadata;
    }
}
