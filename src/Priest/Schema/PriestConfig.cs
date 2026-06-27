using System.Text.Json.Nodes;

namespace Priest.Schema;

/// <summary>Provider and model configuration for a single priest run.</summary>
public class PriestConfig
{
    /// <summary>Registered provider name. Must match a key in the engine's adapter registry.</summary>
    public string Provider { get; set; }

    /// <summary>Model identifier passed directly to the provider.</summary>
    public string Model { get; set; }

    /// <summary>Request timeout. Defaults to 60 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Maximum tokens to generate. Omitted from provider request if null.</summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>Advisory cost ceiling in USD. The engine does NOT enforce this.</summary>
    public double? CostLimit { get; set; }

    /// <summary>Budget for the assembled system prompt in characters. Triggers tail-trim of memory entries when exceeded.</summary>
    public int? MaxSystemChars { get; set; }

    /// <summary>
    /// Conversation compaction budget (spec 2.5.0). When set, a chat turn whose
    /// reported input usage crosses 80% of this budget triggers compaction.
    /// Null = compaction off (default). Independent of MaxSystemChars.
    /// </summary>
    public int? MaxContextTokens { get; set; }

    /// <summary>Most-recent turns kept verbatim when compacting (spec 2.5.0). Default 6.</summary>
    public int? CompactionKeepTurns { get; set; }

    /// <summary>
    /// Hard cap on how many recent session turns are replayed (spec 2.6.0).
    /// 0 replays none (summary only); null replays all (default).
    /// </summary>
    public int? SessionContextTurns { get; set; }

    /// <summary>
    /// Provider-specific options merged directly into the request payload.
    /// Examples: { "think": false } for Ollama/Qwen3, { "temperature": 0.7 }.
    /// </summary>
    public Dictionary<string, JsonNode?> ProviderOptions { get; set; } = new();

    public PriestConfig(string provider, string model)
    {
        Provider = provider;
        Model = model;
    }
}
