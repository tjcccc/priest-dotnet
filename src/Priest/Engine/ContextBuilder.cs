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

    private const string MemoriesHeader       = "## Loaded Memories\n\n";
    private const string DynamicMemoryHeader  = "## Memory\n\n";
    private const string SectionSeparator     = "\n\n";
    private const string MemorySeparator      = "\n";

    public static IList<ChatMessage> BuildMessages(
        Profile        profile,
        Session?       session,
        string         prompt,
        IList<string>? context      = null,
        IList<string>? memory       = null,
        IList<string>? userContext  = null,
        OutputSpec?    outputSpec   = null,
        int?           maxSystemChars = null)
    {
        // Step 1 — normalize profile memories
        var profileMemories = profile.Memories
            .Select(m => m.Trim())
            .Where(m => m.Length > 0)
            .ToList();

        // Step 2 — deduplicate dynamic memory
        var seen = new HashSet<string>(profileMemories);
        var dynamicMemory = new List<string>();
        if (memory is not null)
        {
            foreach (var entry in memory)
            {
                var stripped = entry.Trim();
                if (stripped.Length == 0) continue;
                if (!seen.Add(stripped)) continue;
                dynamicMemory.Add(stripped);
            }
        }

        // Step 3 — trim to budget (only when maxSystemChars is set)
        if (maxSystemChars.HasValue)
        {
            var fmt = outputSpec?.PromptFormat is { } pf && FormatInstructions.TryGetValue(pf, out var fi) ? fi : null;
            while (dynamicMemory.Count > 0 &&
                   AssembleSystemContent(context, profile, profileMemories, dynamicMemory, fmt).Length > maxSystemChars.Value)
                dynamicMemory.RemoveAt(dynamicMemory.Count - 1);

            while (profileMemories.Count > 0 &&
                   AssembleSystemContent(context, profile, profileMemories, dynamicMemory, fmt).Length > maxSystemChars.Value)
                profileMemories.RemoveAt(profileMemories.Count - 1);
        }

        // Step 4 — assemble system content
        string? formatInstruction = outputSpec?.PromptFormat is { } fmt2 && FormatInstructions.TryGetValue(fmt2, out var instr2) ? instr2 : null;
        var systemContent = AssembleSystemContent(context, profile, profileMemories, dynamicMemory, formatInstruction);

        var messages = new List<ChatMessage>();

        // System message (only if non-empty)
        if (systemContent.Length > 0)
            messages.Add(new ChatMessage("system", systemContent));

        // Historical turns
        if (session is not null)
            foreach (var turn in session.Turns)
                messages.Add(new ChatMessage(
                    turn.Role == TurnRole.User ? "user" : "assistant",
                    turn.Content));

        // User turn
        var userParts = new List<string> { prompt };
        if (userContext is not null)
            foreach (var ctx in userContext)
                if (!string.IsNullOrWhiteSpace(ctx)) userParts.Add(ctx);

        messages.Add(new ChatMessage("user", string.Join(SectionSeparator, userParts)));
        return messages;
    }

    private static string AssembleSystemContent(
        IList<string>? context,
        Profile        profile,
        List<string>   profileMemories,
        List<string>   dynamicMemory,
        string?        formatInstruction)
    {
        var parts = new List<string>();

        if (context is not null)
            foreach (var ctx in context)
                if (!string.IsNullOrWhiteSpace(ctx)) parts.Add(ctx);

        if (!string.IsNullOrWhiteSpace(profile.Rules))   parts.Add(profile.Rules.Trim());
        if (!string.IsNullOrWhiteSpace(profile.Identity)) parts.Add(profile.Identity.Trim());
        if (!string.IsNullOrWhiteSpace(profile.Custom))  parts.Add(profile.Custom.Trim());

        if (profileMemories.Count > 0)
            parts.Add(MemoriesHeader + string.Join(MemorySeparator, profileMemories));

        if (dynamicMemory.Count > 0)
            parts.Add(DynamicMemoryHeader + string.Join(MemorySeparator, dynamicMemory));

        if (formatInstruction is not null)
            parts.Add(formatInstruction);

        return string.Join(SectionSeparator, parts);
    }
}
