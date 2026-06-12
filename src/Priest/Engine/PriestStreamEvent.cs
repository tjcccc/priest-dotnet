using Priest.Schema;

namespace Priest.Engine;

/// <summary>
/// Engine-level structured streaming event (spec 2.4.0). Type is one of:
/// text_delta, tool_call_start, tool_call_delta, tool_call_end, usage, done.
/// The terminal event is always "done" carrying the full PriestResponse.
/// </summary>
public record PriestStreamEvent(string Type)
{
    public string? Text { get; init; }
    public int? Index { get; init; }
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? ArgumentsDelta { get; init; }
    public ToolCall? ToolCall { get; init; }
    public UsageInfo? Usage { get; init; }
    public PriestResponse? Response { get; init; }
}
