using System.Text.Json.Nodes;

namespace Priest.Schema;

/// <summary>
/// A tool the model may call. The library transports tool definitions and
/// calls; it never executes tools — execution is the caller's responsibility.
/// See spec behavior/tool-calling.md.
/// </summary>
public record ToolDefinition(
    string Name,
    string? Description = null,
    /// <summary>JSON Schema object describing the tool's parameters.</summary>
    JsonObject? Parameters = null);

/// <summary>A tool executed entirely by the model provider (spec 2.9.0).</summary>
public sealed record ProviderToolDefinition(string Type)
{
    public static ProviderToolDefinition WebSearch { get; } = new("web_search");
}

/// <summary>
/// Tool selection behavior: Auto lets the model decide, None disables calls,
/// Required forces a call, and Tool(name) forces a specific tool.
/// </summary>
public sealed class ToolChoice
{
    /// <summary>"auto", "none", or "required" — null when a named tool is forced.</summary>
    public string? Mode { get; }
    /// <summary>Forced tool name — null for the mode variants.</summary>
    public string? Name { get; }

    private ToolChoice(string? mode, string? name) { Mode = mode; Name = name; }

    public static readonly ToolChoice Auto = new("auto", null);
    public static readonly ToolChoice None = new("none", null);
    public static readonly ToolChoice Required = new("required", null);
    public static ToolChoice Tool(string name) => new(null, name);
}

/// <summary>
/// A single tool call requested by the model. Providers that do not assign
/// call ids (Ollama) get synthesized ids "call_0", "call_1", ... in order.
/// Arguments are always a parsed JSON object; unparseable provider JSON
/// becomes an empty object.
/// </summary>
public record ToolCall(string Id, string Name, JsonObject Arguments);

/// <summary>
/// One entry in the turn-local tool loop history. Callers replay the full
/// exchange on each loop iteration via PriestRequest.ToolExchange. Exchange
/// turns are never persisted in sessions.
/// </summary>
public abstract record ToolExchangeTurn;

/// <summary>Assistant turn carrying tool calls and request-local reasoning state.</summary>
public record AssistantToolTurn(
    string? Text,
    IList<ToolCall> ToolCalls,
    ReasoningInfo? Reasoning = null) : ToolExchangeTurn;

/// <summary>Result of one executed tool call, replayed in the exchange.</summary>
public record ToolResultTurn(string ToolCallId, string Name, string Content, bool IsError = false) : ToolExchangeTurn;
