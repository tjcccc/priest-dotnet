using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Priest.Errors;
using Priest.Schema;

namespace Priest.Providers;

/// <summary>Anthropic provider. Uses SSE streaming via /v1/messages.</summary>
public class AnthropicProvider : IProviderAdapter
{
    private const string ApiUrl          = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";
    // Spec-defined default (behavior/providers.md): Anthropic requires max_tokens.
    private const int    DefaultMaxTokens = 8096;

    private readonly string _apiKey;
    private static readonly HttpClient _http = new();

    public AnthropicProvider(string apiKey)
    {
        _apiKey = apiKey;
    }

    public async Task<AdapterResult> CompleteAsync(IList<ChatMessage> messages, PriestConfig config,
        OutputSpec? outputSpec = null, AdapterCallOptions? options = null, CancellationToken ct = default)
    {
        var (system, chat) = SplitMessages(messages);
        var body = BuildBody(config, chat, system, outputSpec, options, stream: false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(config.Timeout);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(CreateRequest(body), cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw PriestException.RequestAborted("anthropic");
        }
        catch (OperationCanceledException)
        {
            throw PriestException.ProviderTimeout("anthropic", config.Timeout);
        }
        catch (Exception ex) { throw PriestException.ProviderError("anthropic", ex.Message); }

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            throw PriestException.ProviderError("anthropic", $"HTTP {(int)response.StatusCode}: {err}");
        }

        var data    = await response.Content.ReadAsStringAsync(ct);
        var node    = JsonNode.Parse(data);
        var content = node?["content"]?.AsArray();
        var text    = string.Concat(
            content?.Where(b => b?["type"]?.GetValue<string>() == "text")
                    .Select(b => b?["text"]?.GetValue<string>() ?? "") ?? []);
        var toolCalls = ParseToolUseBlocks(content);
        var finish  = node?["stop_reason"]?.GetValue<string>();
        var inToks  = node?["usage"]?["input_tokens"]?.GetValue<int>();
        var outToks = node?["usage"]?["output_tokens"]?.GetValue<int>();
        var cachedToks = node?["usage"]?["cache_read_input_tokens"]?.GetValue<int>();
        return new AdapterResult(text,
            toolCalls is not null ? "tool_calls" : MapStopReason(finish),
            inToks, outToks, cachedToks, toolCalls);
    }

    public async IAsyncEnumerable<string> StreamAsync(IList<ChatMessage> messages, PriestConfig config,
        OutputSpec? outputSpec = null, AdapterCallOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var ev in StreamEventsAsync(messages, config, outputSpec, options, ct))
            if (ev.Type == "text_delta" && ev.Text is not null) yield return ev.Text;
    }

    public async IAsyncEnumerable<AdapterStreamEvent> StreamEventsAsync(IList<ChatMessage> messages, PriestConfig config,
        OutputSpec? outputSpec = null, AdapterCallOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (system, chat) = SplitMessages(messages);
        var body = BuildBody(config, chat, system, outputSpec, options, stream: true);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(config.Timeout);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(CreateRequest(body), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw PriestException.RequestAborted("anthropic");
        }
        catch (OperationCanceledException)
        {
            throw PriestException.ProviderTimeout("anthropic", config.Timeout);
        }
        catch (Exception ex) { throw PriestException.ProviderError("anthropic", ex.Message); }

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            throw PriestException.ProviderError("anthropic", $"HTTP {(int)response.StatusCode}: {err}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // Anthropic block index -> in-progress tool call state. Tool-call event
        // indexes are assigned in tool_use block order, independent of text blocks.
        var toolBlocks = new Dictionary<int, ToolBlockState>();
        var toolCount = 0;
        string? stopReason = null;
        int? inputTokens = null, outputTokens = null, cachedInputTokens = null;

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (!line.StartsWith("data: ")) continue;
            JsonNode? node;
            try { node = JsonNode.Parse(line[6..]); } catch { continue; }

            switch (node?["type"]?.GetValue<string>())
            {
                case "message_start":
                    inputTokens = node?["message"]?["usage"]?["input_tokens"]?.GetValue<int>() ?? inputTokens;
                    cachedInputTokens = node?["message"]?["usage"]?["cache_read_input_tokens"]?.GetValue<int>() ?? cachedInputTokens;
                    break;
                case "content_block_start":
                {
                    var block = node?["content_block"];
                    var index = node?["index"]?.GetValue<int>();
                    if (block?["type"]?.GetValue<string>() == "tool_use" && index.HasValue)
                    {
                        var state = new ToolBlockState
                        {
                            ToolIndex = toolCount++,
                            Id = block?["id"]?.GetValue<string>(),
                            Name = block?["name"]?.GetValue<string>(),
                        };
                        toolBlocks[index.Value] = state;
                        yield return new AdapterStreamEvent("tool_call_start") { Index = state.ToolIndex, Id = state.Id, Name = state.Name };
                    }
                    break;
                }
                case "content_block_delta":
                {
                    var delta = node?["delta"];
                    var deltaType = delta?["type"]?.GetValue<string>();
                    if (deltaType == "text_delta")
                    {
                        var text = delta?["text"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(text))
                            yield return new AdapterStreamEvent("text_delta") { Text = text };
                    }
                    else if (deltaType == "input_json_delta")
                    {
                        var index = node?["index"]?.GetValue<int>();
                        var fragment = delta?["partial_json"]?.GetValue<string>();
                        if (index.HasValue && toolBlocks.TryGetValue(index.Value, out var state) && !string.IsNullOrEmpty(fragment))
                        {
                            state.Json.Append(fragment);
                            yield return new AdapterStreamEvent("tool_call_delta") { Index = state.ToolIndex, ArgumentsDelta = fragment };
                        }
                    }
                    break;
                }
                case "content_block_stop":
                {
                    var index = node?["index"]?.GetValue<int>();
                    if (index.HasValue && toolBlocks.Remove(index.Value, out var state))
                    {
                        yield return new AdapterStreamEvent("tool_call_end")
                        {
                            Index = state.ToolIndex,
                            ToolCall = new ToolCall(state.Id ?? $"call_{state.ToolIndex}", state.Name ?? "", ParseArguments(state.Json.ToString())),
                        };
                    }
                    break;
                }
                case "message_delta":
                    stopReason = node?["delta"]?["stop_reason"]?.GetValue<string>() ?? stopReason;
                    outputTokens = node?["usage"]?["output_tokens"]?.GetValue<int>() ?? outputTokens;
                    break;
            }
        }

        if (inputTokens.HasValue || outputTokens.HasValue)
            yield return new AdapterStreamEvent("usage") { InputTokens = inputTokens, OutputTokens = outputTokens, CachedInputTokens = cachedInputTokens };
        yield return new AdapterStreamEvent("finish")
        {
            FinishReason = toolCount > 0 ? "tool_calls" : MapStopReason(stopReason),
        };
    }

    private sealed class ToolBlockState
    {
        public int ToolIndex;
        public string? Id;
        public string? Name;
        public readonly StringBuilder Json = new();
    }

    private static (string system, List<ChatMessage> chat) SplitMessages(IList<ChatMessage> messages)
    {
        var systemParts = messages.Where(m => m.Role == "system").Select(m => m.Content);
        var chat = messages.Where(m => m.Role != "system").ToList();
        return (string.Join("\n\n", systemParts), chat);
    }

    private static JsonObject BuildBody(PriestConfig config, List<ChatMessage> chat, string system,
        OutputSpec? outputSpec, AdapterCallOptions? options, bool stream)
    {
        var arr = new JsonArray();
        var pendingToolResults = new JsonArray();

        void FlushToolResults()
        {
            if (pendingToolResults.Count > 0)
            {
                arr.Add(new JsonObject { ["role"] = "user", ["content"] = pendingToolResults });
                pendingToolResults = new JsonArray();
            }
        }

        foreach (var m in chat)
        {
            if (m.Role == "tool")
            {
                // Consecutive tool results merge into one user message
                // (Anthropic requires alternating roles).
                pendingToolResults.Add(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = m.ToolCallId,
                    ["content"] = m.Content,
                });
                continue;
            }
            FlushToolResults();
            if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 })
            {
                var blocks = new JsonArray();
                if (m.Content.Length > 0)
                    blocks.Add(new JsonObject { ["type"] = "text", ["text"] = m.Content });
                foreach (var call in m.ToolCalls)
                    blocks.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = call.Id,
                        ["name"] = call.Name,
                        ["input"] = call.Arguments.DeepClone(),
                    });
                arr.Add(new JsonObject { ["role"] = "assistant", ["content"] = blocks });
                continue;
            }
            arr.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
        }
        FlushToolResults();

        var systemText = system;
        if (outputSpec?.JsonSchema is not null)
        {
            var instruction =
                "Respond with a valid JSON object that conforms to the following JSON Schema:\n\n" +
                $"<schema>\n{outputSpec.JsonSchema.ToJsonString(new JsonSerializerOptions { WriteIndented = true })}\n</schema>\n\n" +
                "Return only the JSON object — no explanation, no markdown fences.";
            systemText = string.IsNullOrEmpty(systemText) ? instruction : $"{systemText}\n\n{instruction}";
        }

        var body = new JsonObject
        {
            ["model"]      = config.Model,
            ["max_tokens"] = config.MaxOutputTokens ?? DefaultMaxTokens,
            ["messages"]   = arr,
            ["stream"]     = stream,
        };
        if (!string.IsNullOrEmpty(systemText)) body["system"] = systemText;
        if (options?.Tools is { Count: > 0 } tools)
        {
            var toolArr = new JsonArray();
            foreach (var tool in tools)
                toolArr.Add(new JsonObject
                {
                    ["name"]         = tool.Name,
                    ["description"]  = tool.Description ?? "",
                    ["input_schema"] = tool.Parameters?.DeepClone()
                        ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
                });
            body["tools"] = toolArr;
            if (options.ToolChoice is { } choice)
            {
                body["tool_choice"] = choice.Name is not null
                    ? new JsonObject { ["type"] = "tool", ["name"] = choice.Name }
                    : new JsonObject { ["type"] = choice.Mode == "required" ? "any" : choice.Mode };
            }
        }
        foreach (var kv in config.ProviderOptions) body[kv.Key] = kv.Value?.DeepClone();
        return body;
    }

    private static List<ToolCall>? ParseToolUseBlocks(JsonArray? content)
    {
        if (content is null) return null;
        var calls = new List<ToolCall>();
        for (var i = 0; i < content.Count; i++)
        {
            var block = content[i];
            if (block?["type"]?.GetValue<string>() != "tool_use") continue;
            var name = block?["name"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name)) continue;
            calls.Add(new ToolCall(
                block?["id"]?.GetValue<string>() ?? $"call_{i}",
                name,
                block?["input"]?.DeepClone() as JsonObject ?? new JsonObject()));
        }
        return calls.Count > 0 ? calls : null;
    }

    /// <summary>Per spec, unparseable or non-object argument JSON becomes an empty object.</summary>
    private static JsonObject ParseArguments(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new JsonObject();
        try
        {
            return JsonNode.Parse(raw) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    // Mirrors the Python reference _map_finish_reason table, extended with tool_use.
    private static string? MapStopReason(string? reason) => reason switch
    {
        null            => null,
        "end_turn"      => "stop",
        "max_tokens"    => "length",
        "stop_sequence" => "stop",
        "tool_use"      => "tool_calls",
        _               => "unknown",
    };

    private HttpRequestMessage CreateRequest(JsonObject body) =>
        new(HttpMethod.Post, ApiUrl)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            Headers = { { "x-api-key", _apiKey }, { "anthropic-version", AnthropicVersion } },
        };
}
