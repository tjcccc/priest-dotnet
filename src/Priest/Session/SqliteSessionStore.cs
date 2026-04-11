using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Priest.Errors;

namespace Priest.Session;

/// <summary>
/// SQLite-backed session store.
///
/// Uses the canonical DDL and timestamp format from spec/behavior/session-lifecycle.md
/// for cross-implementation interoperability with other priest SDKs.
/// </summary>
public class SqliteSessionStore : ISessionStore, IDisposable
{
    // Write format: YYYY-MM-DDTHH:MM:SS.ffffff+00:00 (UTC, microseconds)
    private const string WriteFormat = "yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'";

    private readonly string _path;
    private SqliteConnection? _connection;

    public SqliteSessionStore(string path)
    {
        _path = path;
    }

    public void Open()
    {
        _connection = new SqliteConnection($"Data Source={_path}");
        _connection.Open();
        Exec("PRAGMA journal_mode=WAL");
        Exec("""
            CREATE TABLE IF NOT EXISTS sessions (
                id           TEXT PRIMARY KEY,
                profile_name TEXT NOT NULL,
                created_at   TEXT NOT NULL,
                updated_at   TEXT NOT NULL,
                metadata     TEXT NOT NULL DEFAULT '{}'
            )
            """);
        Exec("""
            CREATE TABLE IF NOT EXISTS turns (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL REFERENCES sessions(id),
                role       TEXT NOT NULL,
                content    TEXT NOT NULL,
                timestamp  TEXT NOT NULL
            )
            """);
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }

    public Task<Session> CreateAsync(string profileName, string? sessionId = null,
        Dictionary<string, JsonNode?>? metadata = null, CancellationToken ct = default)
    {
        var db = RequireConnection();
        var id = sessionId ?? Guid.NewGuid().ToString();
        var now = DateTime.UtcNow;
        var metaJson = JsonSerializer.Serialize(metadata ?? new());
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO sessions (id, profile_name, created_at, updated_at, metadata) VALUES (@id, @pn, @ca, @ua, @m)";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@pn", profileName);
        cmd.Parameters.AddWithValue("@ca", DtToStr(now));
        cmd.Parameters.AddWithValue("@ua", DtToStr(now));
        cmd.Parameters.AddWithValue("@m",  metaJson);
        cmd.ExecuteNonQuery();
        return Task.FromResult(new Session(id, profileName, now, now, metadata: metadata));
    }

    public Task<Session?> GetAsync(string sessionId, CancellationToken ct = default)
    {
        var db = RequireConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id, profile_name, created_at, updated_at, metadata FROM sessions WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", sessionId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return Task.FromResult<Session?>(null);

        var id          = reader.GetString(0);
        var profileName = reader.GetString(1);
        var createdAt   = StrToDt(reader.GetString(2));
        var updatedAt   = StrToDt(reader.GetString(3));
        var meta        = JsonSerializer.Deserialize<Dictionary<string, JsonNode?>>(reader.GetString(4)) ?? new();
        var turns       = LoadTurns(db, id);

        return Task.FromResult<Session?>(new Session(id, profileName, createdAt, updatedAt, turns, meta));
    }

    public Task SaveAsync(Session session, CancellationToken ct = default)
    {
        var db = RequireConnection();
        using var tx = db.BeginTransaction();

        using var upd = db.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = "UPDATE sessions SET updated_at = @ua, metadata = @m WHERE id = @id";
        upd.Parameters.AddWithValue("@ua", DtToStr(session.UpdatedAt));
        upd.Parameters.AddWithValue("@m",  JsonSerializer.Serialize(session.Metadata));
        upd.Parameters.AddWithValue("@id", session.Id);
        upd.ExecuteNonQuery();

        using var del = db.CreateCommand();
        del.Transaction = tx;
        del.CommandText = "DELETE FROM turns WHERE session_id = @sid";
        del.Parameters.AddWithValue("@sid", session.Id);
        del.ExecuteNonQuery();

        foreach (var turn in session.Turns)
        {
            using var ins = db.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO turns (session_id, role, content, timestamp) VALUES (@sid, @r, @c, @ts)";
            ins.Parameters.AddWithValue("@sid", session.Id);
            ins.Parameters.AddWithValue("@r",   turn.Role == TurnRole.User ? "user" : "assistant");
            ins.Parameters.AddWithValue("@c",   turn.Content);
            ins.Parameters.AddWithValue("@ts",  DtToStr(turn.Timestamp));
            ins.ExecuteNonQuery();
        }

        tx.Commit();
        return Task.CompletedTask;
    }

    private static List<Turn> LoadTurns(SqliteConnection db, string sessionId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT role, content, timestamp FROM turns WHERE session_id = @sid ORDER BY id ASC";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        using var reader = cmd.ExecuteReader();
        var turns = new List<Turn>();
        while (reader.Read())
        {
            var role = reader.GetString(0) == "user" ? TurnRole.User : TurnRole.Assistant;
            turns.Add(new Turn(role, reader.GetString(1), StrToDt(reader.GetString(2))));
        }
        return turns;
    }

    private static string DtToStr(DateTime dt) =>
        dt.ToUniversalTime().ToString(WriteFormat, System.Globalization.CultureInfo.InvariantCulture);

    private static DateTime StrToDt(string s)
    {
        // Lenient read per spec — try multiple formats
        var formats = new[]
        {
            "yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'",
            "yyyy-MM-dd'T'HH:mm:ss'+00:00'",
            "yyyy-MM-dd'T'HH:mm:ss.ffffffZ",
            "yyyy-MM-dd'T'HH:mm:ssZ",
        };
        foreach (var fmt in formats)
            if (DateTime.TryParseExact(s, fmt, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
                return dt.ToUniversalTime();
        return DateTime.UtcNow;
    }

    private void Exec(string sql)
    {
        using var cmd = RequireConnection().CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection RequireConnection() =>
        _connection ?? throw new InvalidOperationException("SqliteSessionStore is not open. Call Open() first.");
}
