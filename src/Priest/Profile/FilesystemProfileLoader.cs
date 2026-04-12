using System.Text.Json;
using Priest.Errors;

namespace Priest.Profiles;

/// <summary>
/// Loads profiles from JSON files in a directory.
///
/// File layout: {baseDir}/{name}.json
/// Falls back to the built-in default profile when the file is not found.
/// </summary>
public class FilesystemProfileLoader : IProfileLoader
{
    private readonly string _baseDir;

    public FilesystemProfileLoader(string baseDir)
    {
        _baseDir = baseDir;
    }

    public Profile Load(string name)
    {
        var path = Path.Combine(_baseDir, $"{name}.json");
        if (!File.Exists(path))
        {
            if (name == "default") return DefaultProfile.Instance;
            throw PriestException.ProfileNotFound(name);
        }

        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var identity = root.TryGetProperty("identity", out var id) ? id.GetString() ?? "" : "";
        var rules    = root.TryGetProperty("rules",    out var r)  ? r.GetString()  ?? "" : "";
        var custom   = root.TryGetProperty("custom",   out var c)  ? c.GetString()       : null;

        var memories = new List<string>();
        if (root.TryGetProperty("memories", out var mems) && mems.ValueKind == JsonValueKind.Array)
            foreach (var m in mems.EnumerateArray())
                if (m.GetString() is { } s) memories.Add(s);

        return new Profile(name, identity, rules, memories, custom);
    }
}
