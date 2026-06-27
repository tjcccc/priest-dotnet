using System.Runtime.CompilerServices;
using Priest.Engine;
using Priest.Errors;
using Priest.Profiles;
using Priest.Providers;
using Priest.Schema;
using Priest.Sessions;

namespace Priest.Tests;

public class CompactionTests
{
    private const string SummaryMarker = "compress prior conversation";

    private static bool IsSummary(IList<ChatMessage> messages)
        => messages.Count > 0 && messages[0].Content.Contains(SummaryMarker);

    /// <summary>Reports a fixed input size on chat turns (to drive the trigger) and a
    /// short summary on the summarization call. Records every messages list.</summary>
    private sealed class ProgrammableAdapter : IProviderAdapter
    {
        private readonly int _inputTokens;
        public List<IList<ChatMessage>> Calls { get; } = new();

        public ProgrammableAdapter(int inputTokens) => _inputTokens = inputTokens;

        public Task<AdapterResult> CompleteAsync(IList<ChatMessage> messages, PriestConfig config,
            OutputSpec? outputSpec = null, AdapterCallOptions? options = null, CancellationToken ct = default)
        {
            Calls.Add(messages);
            var summary = IsSummary(messages);
            return Task.FromResult(new AdapterResult(
                summary ? "SUMMARY" : "assistant reply", "stop",
                InputTokens: summary ? 5 : _inputTokens, OutputTokens: 5));
        }

        public async IAsyncEnumerable<string> StreamAsync(IList<ChatMessage> messages, PriestConfig config,
            OutputSpec? outputSpec = null, AdapterCallOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Calls.Add(messages);
            yield return "assistant reply";
            await Task.Yield();
        }

        public async IAsyncEnumerable<AdapterStreamEvent> StreamEventsAsync(IList<ChatMessage> messages, PriestConfig config,
            OutputSpec? outputSpec = null, AdapterCallOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Calls.Add(messages);
            yield return new AdapterStreamEvent("text_delta") { Text = "assistant reply" };
            yield return new AdapterStreamEvent("usage") { InputTokens = _inputTokens, OutputTokens = 5 };
            yield return new AdapterStreamEvent("finish") { FinishReason = "stop" };
            await Task.Yield();
        }
    }

    private static PriestConfig BudgetConfig() =>
        new("mock", "test-model") { MaxContextTokens = 100, CompactionKeepTurns = 2 };

    private static (PriestEngine, ProgrammableAdapter) Engine(ISessionStore store, int inputTokens)
    {
        var adapter = new ProgrammableAdapter(inputTokens);
        var engine = new PriestEngine(new StaticProfileLoader(), store,
            new Dictionary<string, IProviderAdapter> { ["mock"] = adapter });
        return (engine, adapter);
    }

    private static PriestRequest Req(PriestConfig config, string prompt)
        => new(config, prompt) { Session = new SessionRef("s") };

    // ── Compactor (pure) ──────────────────────────────────────────────────────

    [Fact]
    public void ShouldCompactOffWithoutBudgetOrMeasuredTurn()
    {
        Assert.False(Compactor.ShouldCompact(10_000, null));
        Assert.False(Compactor.ShouldCompact(10_000, 0));
        Assert.False(Compactor.ShouldCompact(null, 1000));
    }

    [Fact]
    public void ShouldCompactFiresOnlyAbove80Percent()
    {
        Assert.False(Compactor.ShouldCompact(799, 1000));
        Assert.True(Compactor.ShouldCompact(801, 1000));
    }

    [Fact]
    public void PlanCompactionNoneWhileHistoryFits()
    {
        var turns = new List<Turn> { Turn("user", "a"), Turn("assistant", "b") };
        Assert.Null(Compactor.PlanCompaction(turns, 0, 2));
    }

    [Fact]
    public void PlanCompactionFoldsBeforeTailAndAdvances()
    {
        var turns = new List<Turn> { Turn("user", "u1"), Turn("assistant", "a1"), Turn("user", "u2"), Turn("assistant", "a2") };
        var plan = Compactor.PlanCompaction(turns, 0, 2);
        Assert.NotNull(plan);
        Assert.Equal(2, plan!.SummarizedThrough);
        Assert.Equal(new[] { "u1", "a1" }, plan.ToSummarize.Select(t => t.Content));
    }

    [Fact]
    public void PlanCompactionRecursiveOnlyFoldsAfterSummarized()
    {
        var turns = new List<Turn>
        {
            Turn("user", "u1"), Turn("assistant", "a1"),
            Turn("user", "u2"), Turn("assistant", "a2"),
            Turn("user", "u3"), Turn("assistant", "a3"),
        };
        var plan = Compactor.PlanCompaction(turns, 2, 2);
        Assert.Equal(4, plan!.SummarizedThrough);
        Assert.Equal(new[] { "u2", "a2" }, plan.ToSummarize.Select(t => t.Content));
    }

    [Fact]
    public void BuildSummaryMessagesMergesExistingAndIncludesNewTurns()
    {
        var messages = Compactor.BuildSummaryMessages("prior synopsis", new[] { Turn("user", "hello"), Turn("assistant", "hi there") });
        Assert.Contains(SummaryMarker, messages[0].Content);
        Assert.Contains("prior synopsis", messages[1].Content);
        Assert.Contains("hello", messages[1].Content);
        Assert.Contains("hi there", messages[1].Content);
    }

    // ── Engine compaction ─────────────────────────────────────────────────────

    [Fact]
    public async Task CompactsOverBudgetChatAndReplaysSummaryPlusTail()
    {
        var store = new InMemorySessionStore();
        var (engine, adapter) = Engine(store, 200);

        foreach (var prompt in new[] { "msg1", "msg2", "msg3" })
            await engine.RunAsync(Req(BudgetConfig(), prompt));

        var session = await store.GetAsync("s");
        Assert.Equal("SUMMARY", session!.GetCompaction().Summary);
        Assert.Contains(adapter.Calls, IsSummary);

        var lastChat = adapter.Calls.Last(m => !IsSummary(m));
        Assert.Contains("## Conversation so far (summary)", lastChat[0].Content);
        Assert.Contains("SUMMARY", lastChat[0].Content);
        Assert.DoesNotContain(lastChat, m => m.Content == "msg1");
    }

    [Fact]
    public async Task CompactionStateSurvivesSqliteRoundTrip()
    {
        // Cross-SDK interop: state written as camelCase JSON must read back from a
        // fresh store, and the persisted bytes must use camelCase keys.
        var dbPath = Path.Combine(Path.GetTempPath(), $"priest-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteSessionStore(dbPath);
            store.Open();
            var (engine, _) = Engine(store, 200);
            foreach (var prompt in new[] { "msg1", "msg2", "msg3" })
                await engine.RunAsync(Req(BudgetConfig(), prompt));

            // Reopen a fresh store on the same DB — forces a JSON deserialize.
            var fresh = new SqliteSessionStore(dbPath);
            fresh.Open();
            var session = await fresh.GetAsync("s");
            var comp = session!.GetCompaction();
            Assert.Equal("SUMMARY", comp.Summary);
            Assert.Equal(2, comp.SummarizedThrough);

            var serialized = session.Metadata["__compaction"]!.ToJsonString();
            Assert.Contains("summarizedThrough", serialized);
            Assert.DoesNotContain("summarized_through", serialized);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task DoesNotRecordTriggerWhenToolExchangeReplayed()
    {
        var store = new InMemorySessionStore();
        var (engine, _) = Engine(store, 200);

        var request = Req(BudgetConfig(), "msg1");
        request.ToolExchange = new List<ToolExchangeTurn> { new ToolResultTurn("c1", "web_search", "big results") };
        await engine.RunAsync(request);

        var session = await store.GetAsync("s");
        Assert.Null(session!.GetCompaction().LastInputTokens);
    }

    [Fact]
    public async Task CompactsOverStreamingPath()
    {
        var store = new InMemorySessionStore();
        var (engine, _) = Engine(store, 200);

        foreach (var prompt in new[] { "msg1", "msg2", "msg3" })
            await foreach (var _ in engine.StreamEventsAsync(Req(BudgetConfig(), prompt))) { }

        var session = await store.GetAsync("s");
        Assert.Equal("SUMMARY", session!.GetCompaction().Summary);
    }

    [Fact]
    public async Task NeverCompactsWithoutBudget()
    {
        var store = new InMemorySessionStore();
        var (engine, adapter) = Engine(store, 200);
        var noBudget = new PriestConfig("mock", "test-model");

        foreach (var prompt in new[] { "msg1", "msg2", "msg3", "msg4" })
            await engine.RunAsync(Req(noBudget, prompt));

        var session = await store.GetAsync("s");
        Assert.Null(session!.GetCompaction().Summary);
        Assert.DoesNotContain(adapter.Calls, IsSummary);
    }

    [Fact]
    public async Task CompactSessionFoldsOnDemandAndReportsCoverage()
    {
        var store = new InMemorySessionStore();
        var (engine, _) = Engine(store, 10); // small input — no auto-compaction
        var noBudget = new PriestConfig("mock", "test-model");

        foreach (var prompt in new[] { "msg1", "msg2", "msg3" })
            await engine.RunAsync(Req(noBudget, prompt));
        Assert.Null((await store.GetAsync("s"))!.GetCompaction().Summary);

        var (compacted, summarizedThrough) = await engine.CompactSessionAsync("s",
            new PriestConfig("mock", "test-model") { CompactionKeepTurns = 2 });
        Assert.True(compacted);
        Assert.Equal(4, summarizedThrough); // 6 turns − keep 2
        Assert.Equal("SUMMARY", (await store.GetAsync("s"))!.GetCompaction().Summary);
    }

    [Fact]
    public async Task CompactSessionThrowsForUnknownSession()
    {
        var store = new InMemorySessionStore();
        var (engine, _) = Engine(store, 10);
        await Assert.ThrowsAsync<PriestException>(() =>
            engine.CompactSessionAsync("nope", new PriestConfig("mock", "m")));
    }

    // ── Session turn window (spec 2.6.0) ──────────────────────────────────────

    private static readonly Profile WindowProfile = new("default", "", "");

    private static Session SessionWith(int n)
    {
        var s = new Session("s", "default", DateTime.UtcNow, DateTime.UtcNow);
        for (var i = 0; i < n; i++)
            s.AppendTurn(i % 2 == 0 ? TurnRole.User : TurnRole.Assistant, $"turn-{i}");
        return s;
    }

    private static IList<string> Replayed(IList<ChatMessage> msgs)
    {
        var body = msgs.Where(m => m.Role != "system").ToList();
        return body.Take(body.Count - 1).Select(m => m.Content).ToList();
    }

    [Fact]
    public void ReplaysAllTurnsWhenWindowUnset()
    {
        var msgs = ContextBuilder.BuildMessages(WindowProfile, SessionWith(6), "Hi");
        Assert.Equal(new[] { "turn-0", "turn-1", "turn-2", "turn-3", "turn-4", "turn-5" }, Replayed(msgs));
    }

    [Fact]
    public void ReplaysOnlyLastNTurns()
    {
        var msgs = ContextBuilder.BuildMessages(WindowProfile, SessionWith(6), "Hi", sessionContextTurns: 2);
        Assert.Equal(new[] { "turn-4", "turn-5" }, Replayed(msgs));
    }

    [Fact]
    public void ReplaysNoTurnsWhenWindowIsZero()
    {
        var msgs = ContextBuilder.BuildMessages(WindowProfile, SessionWith(6), "Hi", sessionContextTurns: 0);
        Assert.Empty(Replayed(msgs));
    }

    [Fact]
    public void SnapsOddWindowDownToUserTurn()
    {
        var msgs = ContextBuilder.BuildMessages(WindowProfile, SessionWith(8), "Hi", sessionContextTurns: 5);
        Assert.Equal("user", msgs.First(m => m.Role != "system").Role);
        Assert.Equal(new[] { "turn-2", "turn-3", "turn-4", "turn-5", "turn-6", "turn-7" }, Replayed(msgs));
    }

    [Fact]
    public void WindowNeverUnhidesSummarizedTurns()
    {
        var session = SessionWith(6);
        session.ApplyCompaction("earlier conversation summary", 4);
        var msgs = ContextBuilder.BuildMessages(WindowProfile, session, "Hi", sessionContextTurns: 5);
        Assert.Equal(new[] { "turn-4", "turn-5" }, Replayed(msgs));
        Assert.Contains("earlier conversation summary", msgs[0].Content);
    }

    private static Turn Turn(string role, string content)
        => new(role == "user" ? TurnRole.User : TurnRole.Assistant, content, DateTime.UtcNow);
}
