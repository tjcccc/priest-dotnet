using Priest.Sessions;

namespace Priest.Tests;

public class InMemorySessionStoreTests
{
    [Fact]
    public async Task CreatesAndRetrievesSession()
    {
        var store = new InMemorySessionStore();
        var session = await store.CreateAsync("default", "s1");
        Assert.Equal("s1", session.Id);
        var loaded = await store.GetAsync("s1");
        Assert.Equal("s1", loaded!.Id);
        Assert.Equal("default", loaded.ProfileName);
    }

    [Fact]
    public async Task ReturnsNullForMissingSession()
    {
        var store = new InMemorySessionStore();
        Assert.Null(await store.GetAsync("nope"));
    }

    [Fact]
    public async Task PersistsTurnsAfterSave()
    {
        var store = new InMemorySessionStore();
        var session = await store.CreateAsync("default", "s1");
        session.AppendTurn(TurnRole.User, "Hello");
        session.AppendTurn(TurnRole.Assistant, "Hi");
        await store.SaveAsync(session);
        var loaded = await store.GetAsync("s1");
        Assert.Equal(2, loaded!.Turns.Count);
        Assert.Equal("Hello", loaded.Turns[0].Content);
        Assert.Equal("Hi", loaded.Turns[1].Content);
    }

    [Fact]
    public async Task GeneratesUuidWhenSessionIdOmitted()
    {
        var store = new InMemorySessionStore();
        var session = await store.CreateAsync("default");
        Assert.NotEmpty(session.Id);
        Assert.True(Guid.TryParse(session.Id, out _));
    }
}

public class SqliteSessionStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteSessionStore _store;

    public SqliteSessionStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"priest_test_{Guid.NewGuid()}.db");
        _store = new SqliteSessionStore(_dbPath);
        _store.Open();
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task CreatesAndRetrievesSession()
    {
        var session = await _store.CreateAsync("default", "s1");
        Assert.Equal("s1", session.Id);
        var loaded = await _store.GetAsync("s1");
        Assert.Equal("s1", loaded!.Id);
        Assert.Equal("default", loaded.ProfileName);
    }

    [Fact]
    public async Task ReturnsNullForMissingSession()
    {
        Assert.Null(await _store.GetAsync("missing"));
    }

    [Fact]
    public async Task PersistsTurnsAfterSave()
    {
        var session = await _store.CreateAsync("default", "s1");
        session.AppendTurn(TurnRole.User, "Hello");
        session.AppendTurn(TurnRole.Assistant, "Hi");
        await _store.SaveAsync(session);
        var loaded = await _store.GetAsync("s1");
        Assert.Equal(2, loaded!.Turns.Count);
        Assert.Equal(TurnRole.User, loaded.Turns[0].Role);
        Assert.Equal("Hello", loaded.Turns[0].Content);
        Assert.Equal(TurnRole.Assistant, loaded.Turns[1].Role);
    }

    [Fact]
    public async Task ReturnsTurnsInInsertionOrder()
    {
        var session = await _store.CreateAsync("default", "s1");
        for (var i = 0; i < 5; i++) session.AppendTurn(TurnRole.User, $"msg{i}");
        await _store.SaveAsync(session);
        var loaded = await _store.GetAsync("s1");
        Assert.Equal(new[] { "msg0", "msg1", "msg2", "msg3", "msg4" },
            loaded!.Turns.Select(t => t.Content));
    }
}
