using Priest.Profiles;
using Priest.Providers;
using Priest.Schema;
using Priest.Sessions;

namespace Priest.Engine;

/// <summary>
/// Assembles the messages list for a provider call.
///
/// Mirrors context_builder.py exactly. The algorithm is documented in
/// spec/behavior/context-assembly.md.
/// </summary>
public static class ContextBuilder
{
    // Spec-critical constants — must match spec/behavior/context-assembly.md exactly
    private static readonly Dictionary<PromptFormat, string> FormatInstructions = new()
    {
        [PromptFormat.Json] = "Respond only with valid JSON. No prose, no markdown code fences.",
        [PromptFormat.Xml]  = "Respond only with valid XML. No prose, no markdown code fences.",
        [PromptFormat.Code] = "Respond only with code. No prose, no markdown code fences around it.",
    };

    private const string MemoriesHeader    = "## Loaded Memories\n\n";
    private const string SectionSeparator  = "\n\n";
    private const string MemorySeparator   = "\n";

    public static IList<ChatMessage> BuildMessages(
        Profile profile,
        Session? session,
        string prompt,
        IList<string>? systemContext = null,
        IList<string>? extraContext  = null,
        OutputSpec?    outputSpec    = null)
    {
        var systemParts = new List<string>();

        // System context (highest priority — injected by app layer)
        if (systemContext is not null)
            foreach (var ctx in systemContext)
                if (!string.IsNullOrWhiteSpace(ctx)) systemParts.Add(ctx);

        // Profile rules
        if (!string.IsNullOrWhiteSpace(profile.Rules)) systemParts.Add(profile.Rules.Trim());

        // Profile identity
        if (!string.IsNullOrWhiteSpace(profile.Identity)) systemParts.Add(profile.Identity.Trim());

        // Profile custom
        if (!string.IsNullOrWhiteSpace(profile.Custom)) systemParts.Add(profile.Custom.Trim());

        // Memories block
        if (profile.Memories.Count > 0)
            systemParts.Add(MemoriesHeader + string.Join(MemorySeparator, profile.Memories));

        // Format instruction
        if (outputSpec?.PromptFormat is { } fmt && FormatInstructions.TryGetValue(fmt, out var instr))
            systemParts.Add(instr);

        var messages = new List<ChatMessage>();

        // System message (only if non-empty)
        if (systemParts.Count > 0)
            messages.Add(new ChatMessage("system", string.Join(SectionSeparator, systemParts)));

        // Historical turns
        if (session is not null)
            foreach (var turn in session.Turns)
                messages.Add(new ChatMessage(
                    turn.Role == TurnRole.User ? "user" : "assistant",
                    turn.Content));

        // User turn
        var userParts = new List<string> { prompt };
        if (extraContext is not null)
            foreach (var ctx in extraContext)
                if (!string.IsNullOrWhiteSpace(ctx)) userParts.Add(ctx);

        messages.Add(new ChatMessage("user", string.Join(SectionSeparator, userParts)));
        return messages;
    }
}
