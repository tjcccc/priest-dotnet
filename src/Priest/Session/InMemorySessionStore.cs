using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Priest.Sessions;

/// <summary>In-memory session store. Data is lost when the process exits.</summary>
public class InMemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    public Task<Session> CreateAsync(string profileName, string? sessionId = null,
        Dictionary<string, JsonNode?>? metadata = null, CancellationToken ct = default)
    {
        var id = sessionId ?? Guid.NewGuid().ToString();
        var now = DateTime.UtcNow;
        var session = new Session(id, profileName, now, now, metadata: metadata);
        _sessions[id] = session;
        return Task.FromResult(session);
    }

    public Task<Session?> GetAsync(string sessionId, CancellationToken ct = default)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return Task.FromResult<Session?>(session);
    }

    public Task SaveAsync(Session session, CancellationToken ct = default)
    {
        _sessions[session.Id] = session;
        return Task.CompletedTask;
    }
}
