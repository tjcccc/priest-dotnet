using Priest.Engine;
using Priest.Errors;
using Priest.Profiles;
using Priest.Providers;
using Priest.Schema;
using Priest.Sessions;

namespace Priest.Tests;

public class EngineTests
{
    private static readonly PriestConfig Config = new("mock", "test-model");

    private static PriestEngine MakeEngine(ISessionStore? store = null, string responseText = "mock response")
        => new(
            new StaticProfileLoader(),
            store,
            new Dictionary<string, IProviderAdapter> { ["mock"] = new MockAdapter(responseText) });

    [Fact]
    public void SpecVersionIs200()
    {
        Assert.Equal("2.0.0", PriestEngine.SpecVersion);
    }

    [Fact]
    public async Task ReturnsOkResponseForRegisteredProvider()
    {
        var engine = MakeEngine();
        var response = await engine.RunAsync(new PriestRequest(Config, "Hello"));
        Assert.True(response.Ok);
        Assert.Equal("mock response", response.Text);
    }

    [Fact]
    public async Task ThrowsForUnregisteredProvider()
    {
        var engine = new PriestEngine(new StaticProfileLoader());
        await Assert.ThrowsAsync<PriestException>(() =>
            engine.RunAsync(new PriestRequest(new PriestConfig("unknown", "x"), "Hi")));
    }

    [Fact]
    public async Task PopulatesExecutionInfo()
    {
        var engine = MakeEngine();
        var response = await engine.RunAsync(new PriestRequest(Config, "Hi"));
        Assert.Equal("mock", response.Execution.Provider);
        Assert.Equal("test-model", response.Execution.Model);
        Assert.True(response.Execution.LatencyMs >= 0);
    }

    [Fact]
    public async Task EchoesMetadataUnchanged()
    {
        var engine = MakeEngine();
        var request = new PriestRequest(Config, "Hi")
        {
            Metadata = new() { ["tag"] = System.Text.Json.Nodes.JsonValue.Create("test") },
        };
        var response = await engine.RunAsync(request);
        Assert.True(response.Metadata.ContainsKey("tag"));
    }

    [Fact]
    public async Task CreatesSessionOnFirstCallContinuesOnSecond()
    {
        var store = new InMemorySessionStore();
        var engine = MakeEngine(store);

        var r1 = await engine.RunAsync(new PriestRequest(Config, "Hello")
        {
            Session = new SessionRef("sess1"),
        });
        Assert.True(r1.Session!.IsNew);
        Assert.Equal(2, r1.Session.TurnCount);

        var r2 = await engine.RunAsync(new PriestRequest(Config, "How are you?")
        {
            Session = new SessionRef("sess1"),
        });
        Assert.False(r2.Session!.IsNew);
        Assert.Equal(4, r2.Session.TurnCount);
    }

    [Fact]
    public async Task ThrowsSessionNotFoundWhenCreateIfMissingFalse()
    {
        var store = new InMemorySessionStore();
        var engine = MakeEngine(store);
        await Assert.ThrowsAsync<PriestException>(() =>
            engine.RunAsync(new PriestRequest(Config, "Hi")
            {
                Session = new SessionRef("missing") { CreateIfMissing = false },
            }));
    }

    [Fact]
    public async Task PlacesProviderErrorIntoResponseError()
    {
        var failAdapter = new FailingAdapter();
        var engine = new PriestEngine(
            new StaticProfileLoader(), null,
            new Dictionary<string, IProviderAdapter> { ["mock"] = failAdapter });

        var response = await engine.RunAsync(new PriestRequest(Config, "Hi"));
        Assert.False(response.Ok);
        Assert.Equal(PriestErrorCode.ProviderError, response.Error!.Code);
    }

    private class FailingAdapter : IProviderAdapter
    {
        public Task<AdapterResult> CompleteAsync(IList<ChatMessage> messages, PriestConfig config,
            OutputSpec? outputSpec = null, CancellationToken ct = default)
            => throw PriestException.ProviderError("mock", "network fail");

        public async IAsyncEnumerable<string> StreamAsync(IList<ChatMessage> messages, PriestConfig config,
            OutputSpec? outputSpec = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}

public class StaticProfileLoader : IProfileLoader
{
    public Profile Load(string name) => DefaultProfile.Instance;
}
