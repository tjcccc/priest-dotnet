using System.Text.Json.Nodes;
using Priest.Providers;
using Priest.Schema;

namespace Priest.Tests;

public class OpenAICompatWireTests
{
    private static readonly ChatMessage[] Messages = [new("user", "go")];

    [Fact]
    public void StreamingRequestsUsageAndNonStreamingOmitsIt()
    {
        var config = new PriestConfig("test", "test-model");

        var streaming = OpenAICompatProvider.BuildBody(Messages, config, null, null, stream: true);
        Assert.True(streaming["stream"]!.GetValue<bool>());
        Assert.True(streaming["stream_options"]!["include_usage"]!.GetValue<bool>());

        var nonStreaming = OpenAICompatProvider.BuildBody(Messages, config, null, null, stream: false);
        Assert.Null(nonStreaming["stream_options"]);
    }

    [Fact]
    public void ProviderOptionsOverrideStreamOptions()
    {
        var config = new PriestConfig("test", "test-model");
        config.ProviderOptions["stream_options"] = new JsonObject { ["include_usage"] = false };

        var body = OpenAICompatProvider.BuildBody(Messages, config, null, null, stream: true);
        Assert.False(body["stream_options"]!["include_usage"]!.GetValue<bool>());
    }
}
