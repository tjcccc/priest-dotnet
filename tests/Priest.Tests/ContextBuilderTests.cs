using Priest.Engine;
using Priest.Profiles;
using Priest.Schema;

namespace Priest.Tests;

public class ContextBuilderTests
{
    private static readonly Profile BaseProfile = new("test", "You are a test assistant.", "Be helpful.");

    [Fact]
    public void ProducesSystemAndUserMessages()
    {
        var msgs = ContextBuilder.BuildMessages(BaseProfile, null, "Hello");
        Assert.Equal(2, msgs.Count);
        Assert.Equal("system", msgs[0].Role);
        Assert.Equal("user", msgs[1].Role);
        Assert.Equal("Hello", msgs[1].Content);
    }

    [Fact]
    public void OmitsSystemMessageWhenProfileEmpty()
    {
        var empty = new Profile("e", "", "", new List<string>());
        var msgs = ContextBuilder.BuildMessages(empty, null, "Hi");
        Assert.Single(msgs);
        Assert.Equal("user", msgs[0].Role);
    }

    [Fact]
    public void RulesBeforeIdentityInSystemMessage()
    {
        var msgs = ContextBuilder.BuildMessages(BaseProfile, null, "Hi");
        var system = msgs[0].Content;
        Assert.True(system.IndexOf("Be helpful.") < system.IndexOf("You are a test assistant."));
    }

    [Fact]
    public void ContextInjectedBeforeRules()
    {
        var msgs = ContextBuilder.BuildMessages(BaseProfile, null, "Hi",
            context: new[] { "Today is Monday." });
        var system = msgs[0].Content;
        Assert.True(system.IndexOf("Today is Monday.") < system.IndexOf("Be helpful."));
    }

    [Fact]
    public void UserContextAppendedToUserTurn()
    {
        var msgs = ContextBuilder.BuildMessages(BaseProfile, null, "Summarize this",
            userContext: new[] { "Context: some document" });
        Assert.Equal("Summarize this\n\nContext: some document", msgs[^1].Content);
    }

    [Theory]
    [InlineData(PromptFormat.Json, "Respond only with valid JSON. No prose, no markdown code fences.")]
    [InlineData(PromptFormat.Xml,  "Respond only with valid XML. No prose, no markdown code fences.")]
    [InlineData(PromptFormat.Code, "Respond only with code. No prose, no markdown code fences around it.")]
    public void CorrectFormatInstructionStrings(PromptFormat fmt, string expected)
    {
        var msgs = ContextBuilder.BuildMessages(BaseProfile, null, "Hi",
            outputSpec: new OutputSpec { PromptFormat = fmt });
        Assert.Contains(expected, msgs[0].Content);
    }

    [Fact]
    public void MemoriesBlockUsesCorrectHeaderAndSeparator()
    {
        var profile = new Profile("p", "id", "rules", new[] { "mem1", "mem2" });
        var msgs = ContextBuilder.BuildMessages(profile, null, "Hi");
        Assert.Contains("## Loaded Memories\n\nmem1\nmem2", msgs[0].Content);
    }

    [Fact]
    public void DefaultProfileMatchesSpec()
    {
        Assert.Equal("You are a helpful, thoughtful assistant.\n", DefaultProfile.Instance.Identity);
        Assert.Equal("Be honest. Do not make things up.\nBe concise unless the user asks for depth.\n",
            DefaultProfile.Instance.Rules);
    }

    // v2.0.0 — dynamic memory block

    [Fact]
    public void DynamicMemoryRenderedUnderMemoryHeader()
    {
        var msgs = ContextBuilder.BuildMessages(BaseProfile, null, "Hi",
            memory: new[] { "User prefers dark mode." });
        Assert.Contains("## Memory\n\nUser prefers dark mode.", msgs[0].Content);
    }

    [Fact]
    public void ProfileMemoriesBeforeDynamicMemory()
    {
        var profile = new Profile("p", "id", "rules", new[] { "Static fact." });
        var msgs = ContextBuilder.BuildMessages(profile, null, "Hi",
            memory: new[] { "Dynamic fact." });
        var system = msgs[0].Content;
        Assert.True(system.IndexOf("## Loaded Memories") < system.IndexOf("## Memory"));
    }

    // v2.0.0 — deduplication

    [Fact]
    public void DynamicMemoryDuplicatingProfileMemoryIsDropped()
    {
        var profile = new Profile("p", "id", "rules", new[] { "Fact A." });
        var msgs = ContextBuilder.BuildMessages(profile, null, "Hi",
            memory: new[] { "Fact A.", "Fact B." });
        var system = msgs[0].Content;
        var firstIdx = system.IndexOf("Fact A.");
        var secondIdx = system.IndexOf("Fact A.", firstIdx + 1);
        Assert.Equal(-1, secondIdx);
        Assert.Contains("Fact B.", system);
    }

    [Fact]
    public void DuplicateDynamicMemoryEntriesAreDropped()
    {
        var msgs = ContextBuilder.BuildMessages(BaseProfile, null, "Hi",
            memory: new[] { "Note X.", "Note X." });
        var system = msgs[0].Content;
        var firstIdx = system.IndexOf("Note X.");
        var secondIdx = system.IndexOf("Note X.", firstIdx + 1);
        Assert.Equal(-1, secondIdx);
    }

    [Fact]
    public void DeduplicationStripsWhitespace()
    {
        var profile = new Profile("p", "id", "rules", new[] { "Fact A." });
        var msgs = ContextBuilder.BuildMessages(profile, null, "Hi",
            memory: new[] { "  Fact A.  " });
        var system = msgs[0].Content;
        Assert.DoesNotContain("## Memory", system);
    }

    // v2.0.0 — trim

    [Fact]
    public void TrimsDynamicMemoryTailFirstWhenBudgetExceeded()
    {
        var profile = new Profile("p", "", "", new List<string>());
        var msgs = ContextBuilder.BuildMessages(profile, null, "Hi",
            memory: new[] { "Short.", new string('X', 500) },
            maxSystemChars: 50);
        var system = msgs[0].Content;
        Assert.Contains("Short.", system);
        Assert.DoesNotContain(new string('X', 500), system);
    }

    [Fact]
    public void NoTrimWhenMaxSystemCharsNotSet()
    {
        var profile = new Profile("p", "", "", new List<string>());
        var msgs = ContextBuilder.BuildMessages(profile, null, "Hi",
            memory: new[] { "A.", "B.", "C." });
        var system = msgs[0].Content;
        Assert.Contains("A.", system);
        Assert.Contains("B.", system);
        Assert.Contains("C.", system);
    }
}
