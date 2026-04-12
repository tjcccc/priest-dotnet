using System.Text.Json.Nodes;

namespace Priest.Sessions;

public enum TurnRole { User, Assistant }

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
}
