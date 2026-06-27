using System.Text.Json.Nodes;

namespace Priest.Sessions;

public enum TurnRole { User, Assistant }

/// <summary>
/// Conversation-compaction state (spec 2.5.0), persisted inside session
/// <see cref="Session.Metadata"/> under <see cref="Session.CompactionMetadataKey"/>.
/// Stored with EXACT camelCase field names — a cross-SDK contract; see
/// spec/behavior/session-lifecycle.md.
/// </summary>
public sealed class CompactionState
{
    /// <summary>Running synopsis covering Turns[0 .. SummarizedThrough).</summary>
    public string? Summary { get; set; }
    /// <summary>Number of leading turns folded into Summary (index into Turns).</summary>
    public int SummarizedThrough { get; set; }
    /// <summary>Provider-reported input tokens of the most recent measured (chat) turn — the trigger signal.</summary>
    public int? LastInputTokens { get; set; }
    /// <summary>ISO-8601 timestamp of the last compaction-state update.</summary>
    public string? UpdatedAt { get; set; }
}

public class Turn
{
    public TurnRole Role { get; }
    public string Content { get; }
    public DateTime Timestamp { get; }

    public Turn(TurnRole role, string content, DateTime timestamp)
    {
        Role = role;
        Content = content;
        Timestamp = timestamp;
    }
}

/// <summary>A conversation session. Mutated in place during a run.</summary>
public class Session
{
    public string Id { get; }
    public string ProfileName { get; }
    public DateTime CreatedAt { get; }
    public DateTime UpdatedAt { get; private set; }
    public List<Turn> Turns { get; } = new();
    public Dictionary<string, JsonNode?> Metadata { get; set; } = new();

    public Session(string id, string profileName, DateTime createdAt, DateTime updatedAt,
        IEnumerable<Turn>? turns = null, Dictionary<string, JsonNode?>? metadata = null)
    {
        Id = id;
        ProfileName = profileName;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        if (turns is not null) Turns.AddRange(turns);
        if (metadata is not null) Metadata = metadata;
    }

    public void AppendTurn(TurnRole role, string content)
    {
        Turns.Add(new Turn(role, content, DateTime.UtcNow));
        UpdatedAt = DateTime.UtcNow;
    }

    // ---- Conversation compaction (spec 2.5.0) ----

    public const string CompactionMetadataKey = "__compaction";

    /// <summary>Read compaction state from metadata. Empty state when unset.</summary>
    public CompactionState GetCompaction()
    {
        if (Metadata.TryGetValue(CompactionMetadataKey, out var raw) && raw is JsonObject obj)
        {
            return new CompactionState
            {
                Summary = obj["summary"]?.GetValue<string>(),
                SummarizedThrough = obj["summarizedThrough"]?.GetValue<int>() ?? 0,
                LastInputTokens = obj["lastInputTokens"]?.GetValue<int>(),
                UpdatedAt = obj["updatedAt"]?.GetValue<string>(),
            };
        }
        return new CompactionState();
    }

    /// <summary>Serialize compaction state into metadata using the camelCase wire keys.</summary>
    private void SetCompaction(CompactionState state)
    {
        var obj = new JsonObject { ["summarizedThrough"] = state.SummarizedThrough };
        if (state.Summary is not null) obj["summary"] = state.Summary;
        if (state.LastInputTokens is int t) obj["lastInputTokens"] = t;
        if (state.UpdatedAt is not null) obj["updatedAt"] = state.UpdatedAt;
        Metadata[CompactionMetadataKey] = obj;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Record the most recent turn's input size (the compaction trigger signal).</summary>
    public void RecordInputTokens(int? tokens)
    {
        if (tokens is not int t) return;
        var state = GetCompaction();
        state.LastInputTokens = t;
        SetCompaction(state);
    }

    /// <summary>Fold Turns[0 .. summarizedThrough) into <paramref name="summary"/>; raw turns stay intact.</summary>
    public void ApplyCompaction(string summary, int summarizedThrough)
    {
        var state = GetCompaction();
        state.Summary = summary;
        state.SummarizedThrough = summarizedThrough;
        state.UpdatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffff+00:00");
        SetCompaction(state);
    }
}
