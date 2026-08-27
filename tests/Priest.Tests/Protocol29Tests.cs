using System.Text.Json.Nodes;
using Priest.Engine;
using Priest.Providers;
using Priest.Schema;

namespace Priest.Tests;

public class Protocol29Tests
{
    [Fact]
    public void ResponsesPlacesProviderToolsBeforeFunctionTools()
    {
        var body = OpenAIResponsesProvider.BuildBody(
            new List<ChatMessage> { new("user", "Search") },
            new PriestConfig("responses", "gpt-test"),
            null,
            new AdapterCallOptions(
                new List<ToolDefinition> { new("lookup") },
                ProviderTools: new List<ProviderToolDefinition>
                {
                    ProviderToolDefinition.WebSearch,
                }),
            false);

        var tools = body["tools"]!.AsArray();
        Assert.Equal("web_search", tools[0]!["type"]!.GetValue<string>());
        Assert.Equal("function", tools[1]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnsupportedProviderToolReturnsProviderError()
    {
        var engine = new PriestEngine(
            new StaticProfileLoader(),
            adapters: new Dictionary<string, IProviderAdapter>
            {
                ["mock"] = new MockAdapter(),
            });
        var response = await engine.RunAsync(new PriestRequest(
            new PriestConfig("mock", "test-model"),
            "Search")
        {
            ProviderTools = new List<ProviderToolDefinition>
            {
                ProviderToolDefinition.WebSearch,
            },
        });

        Assert.False(response.Ok);
        Assert.Equal(Errors.PriestErrorCode.ProviderError, response.Error!.Code);
        Assert.Contains("Provider tool 'web_search' is not supported", response.Error.Message);
    }
}
