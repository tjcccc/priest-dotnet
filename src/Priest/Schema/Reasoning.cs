using System.Text.Json.Nodes;

namespace Priest.Schema;

/// <summary>Provider-neutral reasoning effort. Support is provider- and model-specific.</summary>
public enum ReasoningEffort { None, Minimal, Low, Medium, High, XHigh, Max }

/// <summary>Requested provider behavior for displayable reasoning summaries.</summary>
public enum ReasoningSummaryMode { None, Auto }

/// <summary>Optional provider-neutral reasoning request.</summary>
public class ReasoningConfig
{
    /// <summary>Request provider-native reasoning, or disable it where supported.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Advisory reasoning effort.</summary>
    public ReasoningEffort? Effort { get; set; }

    /// <summary>Request the provider's displayable summary, or explicitly request none.</summary>
    public ReasoningSummaryMode? Summary { get; set; }
}

/// <summary>
/// Provider-owned continuation state. Value must be replayed unchanged only
/// to an adapter that recognizes Format, and must never be displayed.
/// </summary>
public record OpaqueReasoningState(string Format, JsonNode? Value);

/// <summary>Safe provider-supplied reasoning information.</summary>
public record ReasoningInfo(
    string? Summary = null,
    IList<OpaqueReasoningState>? Continuation = null);
