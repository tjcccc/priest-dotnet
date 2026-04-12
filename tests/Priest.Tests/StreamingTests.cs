using Priest.Engine;
using Priest.Providers;
using Priest.Schema;
using Priest.Sessions;

namespace Priest.Tests;

public class StreamingTests
{
    private static readonly PriestConfig Config = new("mock", "test-model");

    private static PriestEngine MakeEngine(ISessionStore? store = null, string text = "hello world")
        => new(
            new StaticProfileLoader(),
            store,
            new Dictionary<string, IProviderAdapter> { ["mock"] = new MockAdapter(text) });

    private static async Task<List<string>> Collect(IAsyncEnumerable<string> stream)
    {
        var chunks = new List<string>();
        await foreach (var c in stream) chunks.Add(c);
        return chunks;
    }

    [Fact]
    public async Task YieldsChunksFromAdapter()
    {
        var engine = MakeEngine();
        var chunks = await Collect(engine.StreamAsync(new PriestRequest(Config, "Hi")));
        Assert.Equal(new[] { "hello", "world" }, chunks);
    }

    [Fact]
    public async Task ThrowsForUnregisteredProvider()
    {
        var engine = new PriestEngine(new StaticProfileLoader());
        await Assert.ThrowsAsync<Errors.PriestException>(async () =>
            await Collect(engine.StreamAsync(new PriestRequest(new PriestConfig("nope", "x"), "Hi"))));
    }

    [Fact]
    public async Task SavesSessionAfterStreamCompletes()
    {
        var store = new InMemorySessionStore();
        var engine = MakeEngine(store, "hello world");

        var chunks = await Collect(engine.StreamAsync(new PriestRequest(Config, "Hello")
        {
            Session = new SessionRef("sess1"),
        }));

        Assert.NotEmpty(chunks);
        var saved = await store.GetAsync("sess1");
        Assert.Equal(2, saved!.Turns.Count);
        Assert.Equal(TurnRole.User, saved.Turns[0].Role);
        Assert.Equal("Hello", saved.Turns[0].Content);
        Assert.Equal(TurnRole.Assistant, saved.Turns[1].Role);
        Assert.Equal(string.Concat(chunks), saved.Turns[1].Content);
    }

    [Fact]
    public async Task ContinuesSessionAcrossStreamCalls()
    {
        var store = new InMemorySessionStore();
        var engine = MakeEngine(store, "a b");

        await Collect(engine.StreamAsync(new PriestRequest(Config, "First") { Session = new SessionRef("sess1") }));
        await Collect(engine.StreamAsync(new PriestRequest(Config, "Second") { Session = new SessionRef("sess1") }));

        var saved = await store.GetAsync("sess1");
        Assert.Equal(4, saved!.Turns.Count);
    }
}
