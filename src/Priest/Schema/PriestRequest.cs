using System.Text.Json.Nodes;

namespace Priest.Schema;

/// <summary>A single engine run request.</summary>
public class PriestRequest
{
    /// <summary>Provider and model configuration.</summary>
    public PriestConfig Config { get; set; }

    /// <summary>The user's prompt. Becomes the content of the final user message.</summary>
    public string Prompt { get; set; }

    /// <summary>Profile name to load. Defaults to "default".</summary>
    public string Profile { get; set; } = "default";

    /// <summary>Session reference. If null, no session is created or continued.</summary>
    public SessionRef? Session { get; set; }

    /// <summary>App-layer strings injected at the top of the system prompt.</summary>
    public IList<string> SystemContext { get; set; } = Array.Empty<string>();

    /// <summary>Additional strings appended to the user turn after the prompt.</summary>
    public IList<string> ExtraContext { get; set; } = Array.Empty<string>();

    /// <summary>Output format hints.</summary>
    public OutputSpec Output { get; set; } = OutputSpec.None;

    /// <summary>Arbitrary caller metadata. Echoed unchanged into PriestResponse.Metadata.</summary>
    public Dictionary<string, JsonNode?> Metadata { get; set; } = new();

    public PriestRequest(PriestConfig config, string prompt)
    {
        Config = config;
        Prompt = prompt;
    }
}
