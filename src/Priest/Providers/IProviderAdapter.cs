using Priest.Schema;

namespace Priest.Providers;

/// <summary>
/// One message in the provider conversation. ToolCalls is set on assistant
/// turns that requested tools; ToolCallId/Name are set on tool-result turns.
/// </summary>
public record ChatMessage(
    string Role,
    string Content,
    IList<ToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    string? Name = null);

/// <summary>Per-call options threaded from the engine into adapters (spec 2.4.0).</summary>
public record AdapterCallOptions(
    IList<ToolDefinition>? Tools = null,
    ToolChoice? ToolChoice = null);

/// <summary>
/// One structured streaming event from an adapter (spec 2.4.0). Type is one
/// of: text_delta, tool_call_start, tool_call_delta, tool_call_end, usage,
/// finish. Only the fields relevant to the type are populated.
/// </summary>
public record AdapterStreamEvent(string Type)
{
    public string? Text { get; init; }
    public int? Index { get; init; }
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? ArgumentsDelta { get; init; }
    public ToolCall? ToolCall { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public string? FinishReason { get; init; }
}

/// <summary>Interface that all provider adapters must implement.</summary>
public interface IProviderAdapter
{
    /// <summary>Execute a request and return the full response.</summary>
    Task<AdapterResult> CompleteAsync(IList<ChatMessage> messages, PriestConfig config,
        OutputSpec? outputSpec = null, AdapterCallOptions? options = null, CancellationToken ct = default);

    /// <summary>Yield text chunks as they arrive.</summary>
    IAsyncEnumerable<string> StreamAsync(IList<ChatMessage> messages, PriestConfig config,
        OutputSpec? outputSpec = null, AdapterCallOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Yield structured streaming events (spec 2.4.0). The default
    /// implementation wraps StreamAsync(): each text chunk becomes a
    /// text_delta and a final finish event is synthesized.
    /// </summary>
    IAsyncEnumerable<AdapterStreamEvent> StreamEventsAsync(IList<ChatMessage> messages, PriestConfig config,
        OutputSpec? outputSpec = null, AdapterCallOptions? options = null, CancellationToken ct = default)
        => WrapTextStream(StreamAsync(messages, config, outputSpec, options, ct));

    private static async IAsyncEnumerable<AdapterStreamEvent> WrapTextStream(IAsyncEnumerable<string> source)
    {
        await foreach (var chunk in source)
            yield return new AdapterStreamEvent("text_delta") { Text = chunk };
        yield return new AdapterStreamEvent("finish") { FinishReason = "stop" };
    }
}
