using System.Text.Json.Nodes;
using Priest.Engine;
using Priest.Providers;
using Priest.Schema;
using Priest.Sessions;

namespace Priest.Tests;

public class ToolCallingTests
{
    private static readonly PriestConfig Config = new("mock", "test-model");
    private static readonly ToolDefinition ReadFileTool = new("read_file", "Read a file");

    private static ToolCall ReadFileCall() =>
        new("call_0", "read_file", new JsonObject { ["path"] = "a.txt" });

    private static PriestEngine MakeEngine(IProviderAdapter adapter, ISessionStore? store = null) =>
        new(new StaticProfileLoader(), store, new Dictionary<string, IProviderAdapter> { ["mock"] = adapter });

    private static PriestRequest MakeRequest() =>
        new(Config, "Read a.txt") { Tools = new List<ToolDefinition> { ReadFileTool } };

    [Fact]
    public async Task ToolCallsSurfaceWithFinishedReason()
    {
        var adapter = new ScriptedAdapter(new[]
        {
            new AdapterResult("", "tool_calls", ToolCalls: new List<ToolCall> { ReadFileCall() }),
        });
        var response = await MakeEngine(adapter).RunAsync(MakeRequest());

        Assert.True(response.Ok);
        Assert.NotNull(response.ToolCalls);
        Assert.Equal("read_file", response.ToolCalls![0].Name);
        Assert.Equal(FinishedReason.ToolCalls, response.Execution.FinishedReason);
        Assert.Equal(new[] { ReadFileTool }, adapter.Calls[0].Options!.Tools);
    }

    [Fact]
    public async Task ToolExchangeReplayedAfterUserMessage()
    {
        var adapter = new ScriptedAdapter(new[] { new AdapterResult("done", "stop") });
        var request = MakeRequest();
        request.ToolExchange = new List<ToolExchangeTurn>
        {
            new AssistantToolTurn("", new List<ToolCall> { ReadFileCall() }),
            new ToolResultTurn("call_0", "read_file", "file body"),
        };
        await MakeEngine(adapter).RunAsync(request);

        var messages = adapter.Calls[0].Messages;
        Assert.Equal("user", messages[^3].Role);
        Assert.Equal("assistant", messages[^2].Role);
        Assert.NotNull(messages[^2].ToolCalls);
        Assert.Equal("tool", messages[^1].Role);
        Assert.Equal("call_0", messages[^1].ToolCallId);
        Assert.Equal("file body", messages[^1].Content);
    }

    [Fact]
    public async Task SessionNotPersistedWhileToolCallsPending()
    {
        var store = new InMemorySessionStore();
        var adapter = new ScriptedAdapter(new[]
        {
            new AdapterResult("", "tool_calls", ToolCalls: new List<ToolCall> { ReadFileCall() }),
            new AdapterResult("The file says hello.", "stop"),
        });
        var engine = MakeEngine(adapter, store);

        var request = MakeRequest();
        request.Session = new SessionRef("s1");
        var first = await engine.RunAsync(request);
        Assert.NotNull(first.ToolCalls);
        Assert.Empty((await store.GetAsync("s1"))!.Turns);

        request.ToolExchange = new List<ToolExchangeTurn>
        {
            new AssistantToolTurn(null, first.ToolCalls!),
            new ToolResultTurn("call_0", "read_file", "hello"),
        };
        var second = await engine.RunAsync(request);
        Assert.Equal("The file says hello.", second.Text);

        var session = await store.GetAsync("s1");
        Assert.Equal(2, session!.Turns.Count);
        Assert.Equal(TurnRole.User, session.Turns[0].Role);
        Assert.Equal("Read a.txt", session.Turns[0].Content);
    }

    [Fact]
    public async Task RunWithToolsExecutesAndReturnsFinalResponse()
    {
        var adapter = new ScriptedAdapter(new[]
        {
            new AdapterResult("", "tool_calls", ToolCalls: new List<ToolCall> { ReadFileCall() }),
            new AdapterResult("The file says hello.", "stop"),
        });
        var executed = new List<ToolCall>();

        var result = await ToolLoop.RunWithToolsAsync(MakeEngine(adapter), MakeRequest(), call =>
        {
            executed.Add(call);
            return Task.FromResult(new ToolExecutionResult("hello"));
        });

        Assert.Single(executed);
        Assert.Equal("The file says hello.", result.Response.Text);
        Assert.False(result.IterationLimitReached);
        Assert.IsType<AssistantToolTurn>(result.Exchange[0]);
        var toolResult = Assert.IsType<ToolResultTurn>(result.Exchange[1]);
        Assert.Equal("hello", toolResult.Content);
        Assert.Contains(adapter.Calls[1].Messages, m => m.Role == "tool");
    }

    [Fact]
    public async Task RunWithToolsDenialInjectsErrorResult()
    {
        var adapter = new ScriptedAdapter(new[]
        {
            new AdapterResult("", "tool_calls", ToolCalls: new List<ToolCall> { ReadFileCall() }),
            new AdapterResult("Understood.", "stop"),
        });
        var executions = 0;

        var result = await ToolLoop.RunWithToolsAsync(MakeEngine(adapter), MakeRequest(),
            call => { executions++; return Task.FromResult(new ToolExecutionResult("never")); },
            onToolCall: _ => Task.FromResult(new ApprovalDecision(false, "not allowed")));

        Assert.Equal(0, executions);
        var denial = Assert.IsType<ToolResultTurn>(result.Exchange[1]);
        Assert.True(denial.IsError);
        Assert.Contains("not allowed", denial.Content);
    }

    [Fact]
    public async Task RunWithToolsIterationCap()
    {
        var adapter = new ScriptedAdapter(new[]
        {
            new AdapterResult("", "tool_calls", ToolCalls: new List<ToolCall> { ReadFileCall() }),
        });

        var result = await ToolLoop.RunWithToolsAsync(MakeEngine(adapter), MakeRequest(),
            _ => Task.FromResult(new ToolExecutionResult("data")), maxIterations: 3);

        Assert.True(result.IterationLimitReached);
        Assert.Equal(3, adapter.Calls.Count);
    }

    [Fact]
    public async Task StreamEventsFallbackWrapsPlainStream()
    {
        var engine = MakeEngine(new MockAdapter("hello world"));
        var events = new List<PriestStreamEvent>();
        await foreach (var ev in engine.StreamEventsAsync(new PriestRequest(Config, "Hi")))
            events.Add(ev);

        var deltas = events.Where(e => e.Type == "text_delta").Select(e => e.Text).ToList();
        Assert.Equal(new[] { "hello", "world" }, deltas);
        var done = events[^1];
        Assert.Equal("done", done.Type);
        Assert.Equal("helloworld", done.Response!.Text);
        Assert.True(done.Response.Ok);
    }
}
