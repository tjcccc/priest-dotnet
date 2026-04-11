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

    public static readonly OutputSpec None = new();
}
