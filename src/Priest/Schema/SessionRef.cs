namespace Priest.Schema;

/// <summary>Reference to a session to create or continue.</summary>
public class SessionRef
{
    /// <summary>
    /// Session identifier. When CreateIfMissing is true, this exact ID is used —
    /// session creation is idempotent on the same ID.
    /// </summary>
    public string Id { get; set; }

    /// <summary>
    /// If true, look up the session by ID before creating.
    /// If false, always create a new session. Defaults to true.
    /// </summary>
    public bool ContinueExisting { get; set; } = true;

    /// <summary>
    /// Only relevant when ContinueExisting is true.
    /// If the session does not exist: true creates it; false throws SessionNotFoundException.
    /// Defaults to true.
    /// </summary>
    public bool CreateIfMissing { get; set; } = true;

    public SessionRef(string id)
    {
        Id = id;
    }
}
