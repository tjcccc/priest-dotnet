using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Priest.Errors;
using Priest.Schema;

namespace Priest.Providers;

/// <summary>OpenAI-compatible provider. Uses SSE streaming via /v1/chat/completions.</summary>
public class OpenAICompatProvider : IProviderAdapter
{
    private readonly string _baseUrl;
    private readonly string? _apiKey;
    private static readonly HttpClient _http = new();

    public OpenAICompatProvider(string baseUrl, string? apiKey = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
    }

    public async Task<AdapterResult> CompleteAsync(IList<ChatMessage> messages, PriestConfig config,
        OutputSpec? outputSpec = null, AdapterCallOptions? options = null, CancellationToken ct = default)
    {
        var body = BuildBody(messages, config, outputSpec, options, stream: false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(config.Timeout);

        HttpResponseMessage response;
        try
        {
            var req = CreateRequest($"{_baseUrl}/v1/chat/completions", body);
            response = await _http.SendAsync(req, cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw PriestException.RequestAborted("openai-compat");
        }
        catch (OperationCanceledException)
        {
            throw PriestException.ProviderTimeout("openai-compat", config.Timeout);
        }
        catch (Exception ex) { throw PriestException.ProviderError("openai-compat", ex.Message); }

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            throw PriestException.ProviderError("openai-compat", $"HTTP {(int)response.StatusCode}: {err}");
        }

        var data = await response.Content.ReadAsStringAsync(ct);
        var node = JsonNode.Parse(data);
        var message = node?["choices"]?[0]?["message"];
        var toolCalls = ParseToolCalls(message?["tool_calls"]?.AsArray());
        var text    = message?["content"]?.GetValue<string>() ?? "";
        var finish  = node?["choices"]?[0]?["finish_reason"]?.GetValue<string>();
        var inToks  = node?["usage"]?["prompt_tokens"]?.GetValue<int>();
        var outToks = node?["usage"]?["completion_tokens"]?.GetValue<int>();
        return new AdapterResult(text,
            toolCalls is not null ? "tool_calls" : MapFinishReason(finish),
            inToks, outToks, toolCalls);
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
        var body = BuildBody(messages, config, outputSpec, options, stream: true);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(config.Timeout);

        HttpResponseMessage response;
        try
        {
            var req = CreateRequest($"{_baseUrl}/v1/chat/completions", body);
            response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw PriestException.RequestAborted("openai-compat");
        }
        catch (OperationCanceledException)
        {
            throw PriestException.ProviderTimeout("openai-compat", config.Timeout);
        }
        catch (Exception ex) { throw PriestException.ProviderError("openai-compat", ex.Message); }

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            throw PriestException.ProviderError("openai-compat", $"HTTP {(int)response.StatusCode}: {err}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // Tool-call fragments accumulate per provider index until the stream ends.
        var partials = new SortedDictionary<int, PartialToolCall>();
        string? finishReason = null;
        int? inputTokens = null, outputTokens = null;

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ")) continue;
            var data = line[6..];
            if (data == "[DONE]") break;
            JsonNode? node;
            try { node = JsonNode.Parse(data); } catch { continue; }

            inputTokens = node?["usage"]?["prompt_tokens"]?.GetValue<int>() ?? inputTokens;
            outputTokens = node?["usage"]?["completion_tokens"]?.GetValue<int>() ?? outputTokens;

            var choice = node?["choices"]?[0];
            if (choice is null) continue;
            finishReason = choice["finish_reason"]?.GetValue<string>() ?? finishReason;

            var chunk = choice["delta"]?["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(chunk))
                yield return new AdapterStreamEvent("text_delta") { Text = chunk };

            foreach (var fragment in choice["delta"]?["tool_calls"]?.AsArray() ?? [])
            {
                var index = fragment?["index"]?.GetValue<int>() ?? 0;
                var id = fragment?["id"]?.GetValue<string>();
                var name = fragment?["function"]?["name"]?.GetValue<string>();
                if (!partials.TryGetValue(index, out var partial))
                {
                    partial = new PartialToolCall { Id = id, Name = name };
                    partials[index] = partial;
                    yield return new AdapterStreamEvent("tool_call_start") { Index = index, Id = id, Name = name };
                }
                else
                {
                    if (id is not null) partial.Id = id;
                    if (name is not null) partial.Name = name;
                }
                var argsDelta = fragment?["function"]?["arguments"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(argsDelta))
                {
                    partial.Args.Append(argsDelta);
                    yield return new AdapterStreamEvent("tool_call_delta") { Index = index, ArgumentsDelta = argsDelta };
                }
            }
        }

        foreach (var (index, partial) in partials)
        {
            yield return new AdapterStreamEvent("tool_call_end")
            {
                Index = index,
                ToolCall = new ToolCall(partial.Id ?? $"call_{index}", partial.Name ?? "", ParseArguments(partial.Args.ToString())),
            };
        }
        if (inputTokens.HasValue || outputTokens.HasValue)
            yield return new AdapterStreamEvent("usage") { InputTokens = inputTokens, OutputTokens = outputTokens };
        yield return new AdapterStreamEvent("finish")
        {
            FinishReason = partials.Count > 0 ? "tool_calls" : MapFinishReason(finishReason) ?? "stop",
        };
    }

    private sealed class PartialToolCall
    {
        public string? Id;
        public string? Name;
        public readonly StringBuilder Args = new();
    }

    private static JsonObject BuildBody(IList<ChatMessage> messages, PriestConfig config,
        OutputSpec? outputSpec, AdapterCallOptions? options, bool stream)
    {
        var arr = new JsonArray();
        foreach (var m in messages)
        {
            if (m.Role == "tool")
            {
                arr.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = m.ToolCallId, ["content"] = m.Content });
            }
            else if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 })
            {
                var calls = new JsonArray();
                foreach (var call in m.ToolCalls)
                    calls.Add(new JsonObject
                    {
                        ["id"] = call.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = call.Name,
                            ["arguments"] = call.Arguments.ToJsonString(),
                        },
                    });
                arr.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = m.Content.Length > 0 ? m.Content : null,
                    ["tool_calls"] = calls,
                });
            }
            else
            {
                arr.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
            }
        }

        var body = new JsonObject
        {
            ["model"]    = config.Model,
            ["messages"] = arr,
            ["stream"]   = stream,
        };
        if (outputSpec?.JsonSchema is not null)
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"]   = outputSpec.JsonSchemaName,
                    ["schema"] = outputSpec.JsonSchema.DeepClone(),
                    ["strict"] = outputSpec.JsonSchemaStrict,
                },
            };
        else if (outputSpec?.ProviderFormat == Schema.ProviderFormat.Json)
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
        if (config.MaxOutputTokens.HasValue) body["max_tokens"] = config.MaxOutputTokens.Value;
        if (options?.Tools is { Count: > 0 } tools)
        {
            var toolArr = new JsonArray();
            foreach (var tool in tools)
                toolArr.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"]        = tool.Name,
                        ["description"] = tool.Description ?? "",
                        ["parameters"]  = tool.Parameters?.DeepClone() ?? new JsonObject(),
                    },
                });
            body["tools"] = toolArr;
            if (options.ToolChoice is { } choice)
            {
                body["tool_choice"] = choice.Mode is not null
                    ? JsonValue.Create(choice.Mode)
                    : new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = choice.Name } };
            }
        }
        foreach (var kv in config.ProviderOptions) body[kv.Key] = kv.Value?.DeepClone();
        return body;
    }

    private static List<ToolCall>? ParseToolCalls(JsonArray? raw)
    {
        if (raw is null || raw.Count == 0) return null;
        var calls = new List<ToolCall>();
        for (var i = 0; i < raw.Count; i++)
        {
            var item = raw[i];
            var name = item?["function"]?["name"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name)) continue;
            calls.Add(new ToolCall(
                item?["id"]?.GetValue<string>() ?? $"call_{i}",
                name,
                ParseArguments(item?["function"]?["arguments"]?.GetValue<string>() ?? "")));
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

    // Mirrors the Python reference _map_finish_reason table, extended with tool_calls.
    private static string? MapFinishReason(string? reason) => reason switch
    {
        null             => null,
        "stop"           => "stop",
        "length"         => "length",
        "content_filter" => "content_filter",
        "tool_calls"     => "tool_calls",
        _                => "unknown",
    };

    private HttpRequestMessage CreateRequest(string url, JsonObject body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (_apiKey is not null) req.Headers.Add("Authorization", $"Bearer {_apiKey}");
        return req;
    }
}
