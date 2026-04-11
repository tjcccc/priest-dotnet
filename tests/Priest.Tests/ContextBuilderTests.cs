using Priest.Engine;
using Priest.Profile;
using Priest.Schema;
using ProfileModel = Priest.Profile.Profile;

namespace Priest.Tests;

public class ContextBuilderTests
{
    private static readonly ProfileModel BaseProfile = new("test", "You are a test assistant.", "Be helpful.");

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
        var empty = new ProfileModel("e", "", "", new List<string>());
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
    public void SystemContextInjectedBeforeRules()
    {
        var msgs = ContextBuilder.BuildMessages(BaseProfile, null, "Hi",
            systemContext: new[] { "Today is Monday." });
        var system = msgs[0].Content;
        Assert.True(system.IndexOf("Today is Monday.") < system.IndexOf("Be helpful."));
    }

    [Fact]
    public void ExtraContextAppendedToUserTurn()
    {
        var msgs = ContextBuilder.BuildMessages(BaseProfile, null, "Summarize this",
            extraContext: new[] { "Context: some document" });
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
        var profile = new ProfileModel("p", "id", "rules", new[] { "mem1", "mem2" });
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
}
