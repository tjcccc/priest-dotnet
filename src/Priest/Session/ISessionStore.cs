using System.Text.Json.Nodes;

namespace Priest.Session;

/// <summary>Contract for session persistence backends.</summary>
public interface ISessionStore
{
    /// <summary>Create a new session. Uses sessionId if provided; generates a UUID otherwise.</summary>
    Task<Session> CreateAsync(string profileName, string? sessionId = null,
        Dictionary<string, JsonNode?>? metadata = null, CancellationToken ct = default);

    /// <summary>Retrieve a session by ID. Returns null if not found.</summary>
    Task<Session?> GetAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Persist a session (turns + metadata + UpdatedAt).</summary>
    Task SaveAsync(Session session, CancellationToken ct = default);
}
