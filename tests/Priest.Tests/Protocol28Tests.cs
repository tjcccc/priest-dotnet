using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Priest.Engine;
using Priest.Providers;
using Priest.Schema;

namespace Priest.Tests;

public class Protocol28Tests
{
    private static readonly PriestConfig Config = new("mock", "reasoning-model");

    [Fact]
    public void SpecVersionIs281()
    {
        Assert.Equal("2.8.1", PriestEngine.SpecVersion);
    }

    [Fact]
    public void ResponsesBodyMapsReasoningToolsSchemaAndProtectsInvariants()
    {
        var config = new PriestConfig("responses", "gpt-test")
        {
            MaxOutputTokens = 200,
            Reasoning = new ReasoningConfig
            {
                Enabled = true,
                Effort = ReasoningEffort.Medium,
                Summary = ReasoningSummaryMode.Auto,
            },
            ProviderOptions = new()
            {
                ["store"] = true,
                ["model"] = "must-not-win",
                ["stream"] = true,
            },
        };
        var schema = JsonNode.Parse("""
            {"type":"object","properties":{"label":{"type":"string"}},"required":["label"]}
            """);
        var body = OpenAIResponsesProvider.BuildBody(
            new List<ChatMessage> { new("user", "Classify this.") },
            config,
            new OutputSpec
            {
                JsonSchema = schema,
                JsonSchemaName = "classification",
                JsonSchemaStrict = true,
            },
            new AdapterCallOptions(
                new List<ToolDefinition>
                {
                    new("lookup", "Look up a label.", new JsonObject { ["type"] = "object" }),
                }),
            stream: false);

        Assert.Equal("gpt-test", body["model"]!.GetValue<string>());
        Assert.False(body["stream"]!.GetValue<bool>());
        Assert.True(body["store"]!.GetValue<bool>());
        Assert.Equal(200, body["max_output_tokens"]!.GetValue<int>());
        Assert.Equal("medium", body["reasoning"]!["effort"]!.GetValue<string>());
        Assert.Equal("auto", body["reasoning"]!["summary"]!.GetValue<string>());
        Assert.Equal("classification", body["text"]!["format"]!["name"]!.GetValue<string>());
        Assert.Equal("function", body["tools"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("input_text", body["input"]![0]!["content"]![0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void ResponsesContinuationReplaysOpaqueStateBeforeFunctionCall()
    {
        var messages = new List<ChatMessage>
        {
            new(
                "assistant",
                "",
                ToolCalls: new List<ToolCall>
                {
                    new("call_1", "lookup", new JsonObject { ["id"] = "42" }),
                },
                Reasoning: new ReasoningInfo(Continuation: new List<OpaqueReasoningState>
                {
                    new(
                        "openai.responses.reasoning.v1",
                        JsonNode.Parse("""{"type":"reasoning","id":"rs_1","encrypted_content":"opaque"}""")),
                })),
            new("tool", "found", ToolCallId: "call_1", Name: "lookup"),
        };

        var input = OpenAIResponsesProvider.BuildBody(
            messages,
            new PriestConfig("responses", "gpt-test"),
            null,
            null,
            false)["input"]!.AsArray();

        Assert.Equal("reasoning", input[0]!["type"]!.GetValue<string>());
        Assert.Equal("function_call", input[1]!["type"]!.GetValue<string>());
        Assert.Equal("""{"id":"42"}""", input[1]!["arguments"]!.GetValue<string>());
        Assert.Equal("function_call_output", input[2]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void ResponsesHistoryUsesOutputTextForAssistantTurns()
    {
        var input = OpenAIResponsesProvider.BuildBody(
            new List<ChatMessage>
            {
                new("system", "Be concise."),
                new("user", "First question."),
                new("assistant", "First answer."),
                new("user", "Second question."),
            },
            new PriestConfig("responses", "gpt-test"),
            null,
            null,
            false)["input"]!.AsArray();

        Assert.Equal("input_text", input[0]!["content"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("input_text", input[1]!["content"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("output_text", input[2]!["content"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("input_text", input[3]!["content"]![0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void ResponsesParserSurfacesSafeSummaryUsageAndContentFilter()
    {
        var result = OpenAIResponsesProvider.ParseResponse(JsonNode.Parse("""
            {
              "status":"completed",
              "output":[
                {
                  "type":"reasoning",
                  "id":"rs_1",
                  "summary":[{"type":"summary_text","text":"Checked two options."}],
                  "encrypted_content":"opaque"
                },
                {
                  "type":"function_call",
                  "call_id":"call_1",
                  "name":"lookup",
                  "arguments":"{\"id\":\"42\"}"
                }
              ],
              "usage":{
                "input_tokens":100,
                "input_tokens_details":{"cached_tokens":80},
                "output_tokens":25,
                "output_tokens_details":{"reasoning_tokens":20}
              }
            }
            """));

        Assert.Equal("tool_calls", result.FinishReason);
        Assert.Equal("Checked two options.", result.Reasoning!.Summary);
        Assert.Equal("openai.responses.reasoning.v1", result.Reasoning.Continuation![0].Format);
        Assert.Equal(20, result.ReasoningTokens);
        Assert.Equal("42", result.ToolCalls![0].Arguments["id"]!.GetValue<string>());

        var filtered = OpenAIResponsesProvider.ParseResponse(JsonNode.Parse("""
            {"status":"incomplete","incomplete_details":{"reason":"content_filter"},"output":[]}
            """));
        Assert.Equal("content_filter", filtered.FinishReason);
    }

    [Fact]
    public void ResponsesParserDoesNotExposeOrReplayRawReasoning()
    {
        var result = OpenAIResponsesProvider.ParseResponse(JsonNode.Parse("""
            {
              "status":"completed",
              "output":[
                {
                  "type":"reasoning",
                  "id":"rs_raw",
                  "content":[{"type":"reasoning_text","text":"private trace"}]
                },
                {"type":"function_call","call_id":"call_1","name":"lookup","arguments":"{}"}
              ]
            }
            """));

        Assert.Null(result.Reasoning);
    }

    [Fact]
    public void AnthropicAndOllamaMapNeutralReasoningWithProviderOverrides()
    {
        var anthropicConfig = new PriestConfig("anthropic", "claude-test")
        {
            Reasoning = new ReasoningConfig
            {
                Enabled = true,
                Effort = ReasoningEffort.High,
                Summary = ReasoningSummaryMode.Auto,
            },
        };
        var continuation = JsonNode.Parse(
            """{"type":"thinking","thinking":"summary","signature":"opaque"}""");
        var anthropic = AnthropicProvider.BuildBody(
            anthropicConfig,
            new List<ChatMessage>
            {
                new(
                    "assistant",
                    "",
                    ToolCalls: new List<ToolCall>
                    {
                        new("call_1", "lookup", new JsonObject()),
                    },
                    Reasoning: new ReasoningInfo(Continuation: new List<OpaqueReasoningState>
                    {
                        new("anthropic.messages.thinking.v1", continuation),
                    })),
            },
            "",
            null,
            null,
            false);

        Assert.Equal("adaptive", anthropic["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal("summarized", anthropic["thinking"]!["display"]!.GetValue<string>());
        Assert.Equal("high", anthropic["output_config"]!["effort"]!.GetValue<string>());
        Assert.Equal("thinking", anthropic["messages"]![0]!["content"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("tool_use", anthropic["messages"]![0]!["content"]![1]!["type"]!.GetValue<string>());

        var ollamaConfig = new PriestConfig("ollama", "qwen")
        {
            Reasoning = new ReasoningConfig { Effort = ReasoningEffort.High },
            ProviderOptions = new() { ["think"] = false },
        };
        var ollama = OllamaProvider.BuildBody(
            new List<ChatMessage> { new("user", "Hi") },
            ollamaConfig,
            null,
            null,
            false);
        Assert.False(ollama["think"]!.GetValue<bool>());

        var invalid = new PriestConfig("ollama", "qwen")
        {
            Reasoning = new ReasoningConfig { Effort = ReasoningEffort.XHigh },
        };
        var error = Assert.Throws<Errors.PriestException>(() =>
            OllamaProvider.BuildBody(
                new List<ChatMessage> { new("user", "Hi") },
                invalid,
                null,
                null,
                false));
        Assert.Equal(Errors.PriestErrorCode.RequestInvalid, error.Code);
    }

    [Fact]
    public async Task ResponsesStreamingHandlesCrlfAndAvoidsDuplicateToolEnd()
    {
        const string sse =
            "data: {\"type\":\"response.reasoning_summary_text.delta\",\"delta\":\"Checking\"}\r\n\r\n" +
            "data: {\"type\":\"response.output_item.added\",\"output_index\":1,\"item\":{\"type\":\"function_call\",\"call_id\":\"call_1\",\"name\":\"lookup\",\"arguments\":\"\"}}\r\n\r\n" +
            "data: {\"type\":\"response.function_call_arguments.delta\",\"output_index\":1,\"delta\":\"{\\\"id\\\":\\\"42\\\"}\"}\r\n\r\n" +
            "data: {\"type\":\"response.function_call_arguments.done\",\"output_index\":1,\"arguments\":\"{\\\"id\\\":\\\"42\\\"}\",\"name\":\"lookup\"}\r\n\r\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[{\"type\":\"function_call\",\"call_id\":\"call_1\",\"name\":\"lookup\",\"arguments\":\"{\\\"id\\\":\\\"42\\\"}\"}],\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}}\r\n\r\n";

        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        });
        var provider = new OpenAIResponsesProvider(httpClient: new HttpClient(handler));
        var events = new List<AdapterStreamEvent>();
        await foreach (var ev in provider.StreamEventsAsync(
            new List<ChatMessage> { new("user", "Hi") },
            new PriestConfig("responses", "gpt-test")))
            events.Add(ev);

        Assert.Contains(events, ev => ev.Type == "reasoning_summary_delta" && ev.Text == "Checking");
        Assert.Single(events, ev => ev.Type == "tool_call_end");
        Assert.Equal(15, events.Single(ev => ev.Type == "usage").InputTokens
            + events.Single(ev => ev.Type == "usage").OutputTokens);
        Assert.Equal("tool_calls", events[^1].FinishReason);
    }

    [Fact]
    public async Task EnginePropagatesReasoningAndToolLoopCopiesIt()
    {
        var reasoning = new ReasoningInfo(
            "Used the lookup plan.",
            new List<OpaqueReasoningState>
            {
                new("test.reasoning.v1", JsonNode.Parse("""{"opaque":true}""")),
            });
        var adapter = new ScriptedAdapter(new[]
        {
            new AdapterResult(
                "",
                "tool_calls",
                ToolCalls: new List<ToolCall>
                {
                    new("call_1", "lookup", new JsonObject()),
                },
                ReasoningTokens: 7,
                Reasoning: reasoning),
            new AdapterResult("done", "stop"),
        });
        var engine = new PriestEngine(
            new StaticProfileLoader(),
            adapters: new Dictionary<string, IProviderAdapter> { ["mock"] = adapter });
        var request = new PriestRequest(Config, "Use a tool")
        {
            Tools = new List<ToolDefinition> { new("lookup") },
        };

        var result = await ToolLoop.RunWithToolsAsync(
            engine,
            request,
            _ => Task.FromResult(new ToolExecutionResult("found")));

        var assistant = Assert.IsType<AssistantToolTurn>(result.Exchange[0]);
        Assert.Same(reasoning, assistant.Reasoning);
        Assert.Same(reasoning, adapter.Calls[1].Messages[^2].Reasoning);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_response(request));
    }
}
