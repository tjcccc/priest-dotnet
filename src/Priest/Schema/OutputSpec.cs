using System.Text.Json.Nodes;

namespace Priest.Schema;

/// <summary>Provider-native output format hint. Currently only Json has broad support.</summary>
public enum ProviderFormat { Json }

/// <summary>Natural-language format instruction injected into the system prompt.</summary>
public enum PromptFormat { Json, Xml, Code }

/// <summary>
/// Output format hints for a priest request.
/// Both fields are optional and independent. The engine never parses response text.
/// </summary>
public class OutputSpec
{
    /// <summary>Activates provider-native structured output (e.g. Ollama format field).</summary>
    public ProviderFormat? ProviderFormat { get; set; }

    /// <summary>Injects a natural-language format instruction into the system prompt.</summary>
    public PromptFormat? PromptFormat { get; set; }

    /// <summary>
    /// JSON Schema for structured output.
    /// OpenAI-compat: maps to response_format={type:"json_schema",...}.
    /// Ollama (v0.5+): maps to format:&lt;schema_dict&gt;.
    /// Anthropic: schema description injected into system message (no native support).
    /// When set, takes precedence over ProviderFormat for the schema-capable path.
    /// </summary>
    public JsonNode? JsonSchema { get; set; }

    /// <summary>Schema name passed to OpenAI's json_schema.name field. Defaults to "response".</summary>
    public string JsonSchemaName { get; set; } = "response";

    /// <summary>
    /// Maps to OpenAI's json_schema.strict. Requires every property in required and
    /// additionalProperties:false. Most user schemas won't satisfy this. Defaults to false.
    /// </summary>
    public bool JsonSchemaStrict { get; set; }

    public static readonly OutputSpec None = new();
}
