namespace Priest.Providers;

/// <summary>Result returned by a provider adapter after a complete (non-streaming) call.</summary>
public record AdapterResult(
    string Text,
    string? FinishReason = null,
    int? InputTokens = null,
    int? OutputTokens = null);
