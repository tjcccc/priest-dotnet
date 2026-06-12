using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Priest.Errors;
using Priest.Schema;

namespace Priest.Providers;

/// <summary>Ollama provider. Uses NDJSON streaming via the /api/chat endpoint.</summary>
public class OllamaProvider : IProviderAdapter
{
    private readonly string _baseUrl;
    private static readonly HttpClient _http = new();

    public OllamaProvider(string baseUrl = "http://localhost:11434")
    {
        _baseUrl = baseUrl.TrimEnd('/');
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
            var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            response = await _http.SendAsync(req, cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw PriestException.RequestAborted("ollama");
        }
        catch (OperationCanceledException)
        {
            throw PriestException.ProviderTimeout("ollama", config.Timeout);
        }
        catch (Exception ex)
        {
            throw PriestException.ProviderError("ollama", ex.Message);
        }

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            throw PriestException.ProviderError("ollama", $"HTTP {(int)response.StatusCode}: {err}");
        }

        var data = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        var message = data?["message"];
        var toolCalls = ParseToolCalls(message?["tool_calls"]?.AsArray());
        return new AdapterResult(
            message?["content"]?.GetValue<string>() ?? "",
            toolCalls is not null ? "tool_calls" : MapDoneReason(data?["done_reason"]?.GetValue<string>()),
            data?["prompt_eval_count"]?.GetValue<int>(),
            data?["eval_count"]?.GetValue<int>(),
            toolCalls);
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
            var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw PriestException.RequestAborted("ollama");
        }
        catch (OperationCanceledException)
        {
            throw PriestException.ProviderTimeout("ollama", config.Timeout);
        }
        catch (Exception ex)
        {
            throw PriestException.ProviderError("ollama", ex.Message);
        }

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            throw PriestException.ProviderError("ollama", $"HTTP {(int)response.StatusCode}: {err}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var toolCallIndex = 0;

        string? line;
        // The connect timeout ends once headers arrive; the caller token stays armed.
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? node;
            try { node = JsonNode.Parse(line); } catch { continue; }

            var chunk = node?["message"]?["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(chunk))
                yield return new AdapterStreamEvent("text_delta") { Text = chunk };

            // Ollama delivers each tool call whole in one chunk.
            var calls = ParseToolCalls(node?["message"]?["tool_calls"]?.AsArray(), toolCallIndex);
            foreach (var call in calls ?? [])
            {
                yield return new AdapterStreamEvent("tool_call_start") { Index = toolCallIndex, Id = call.Id, Name = call.Name };
                yield return new AdapterStreamEvent("tool_call_end") { Index = toolCallIndex, ToolCall = call };
                toolCallIndex++;
            }

            if (node?["done"]?.GetValue<bool>() == true)
            {
                var inToks = node?["prompt_eval_count"]?.GetValue<int>();
                var outToks = node?["eval_count"]?.GetValue<int>();
                if (inToks.HasValue || outToks.HasValue)
                    yield return new AdapterStreamEvent("usage") { InputTokens = inToks, OutputTokens = outToks };
                yield return new AdapterStreamEvent("finish")
                {
                    FinishReason = toolCallIndex > 0 ? "tool_calls" : MapDoneReason(node?["done_reason"]?.GetValue<string>()),
                };
                break;
            }
        }
    }

    private static JsonObject BuildBody(IList<ChatMessage> messages, PriestConfig config,
        OutputSpec? outputSpec, AdapterCallOptions? options, bool stream)
    {
        var body = new JsonObject
        {
            ["model"]    = config.Model,
            ["messages"] = BuildMessages(messages),
            ["stream"]   = stream,
        };
        if (outputSpec?.JsonSchema is not null) body["format"] = outputSpec.JsonSchema.DeepClone();
        else if (outputSpec?.ProviderFormat == Schema.ProviderFormat.Json) body["format"] = "json";
        if (config.MaxOutputTokens.HasValue)
            body["options"] = new JsonObject { ["num_predict"] = config.MaxOutputTokens.Value };
        if (options?.Tools is { Count: > 0 } tools)
        {
            // Ollama accepts OpenAI-shaped tools; it has no tool_choice parameter.
            var arr = new JsonArray();
            foreach (var tool in tools)
                arr.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"]        = tool.Name,
                        ["description"] = tool.Description ?? "",
                        ["parameters"]  = tool.Parameters?.DeepClone() ?? new JsonObject(),
                    },
                });
            body["tools"] = arr;
        }
        foreach (var kv in config.ProviderOptions) body[kv.Key] = kv.Value?.DeepClone();
        return body;
    }

    private static JsonArray BuildMessages(IList<ChatMessage> messages)
    {
        var arr = new JsonArray();
        foreach (var m in messages)
        {
            if (m.Role == "tool")
            {
                // Ollama correlates tool results by tool_name, not call id.
                arr.Add(new JsonObject { ["role"] = "tool", ["content"] = m.Content, ["tool_name"] = m.Name });
            }
            else if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 })
            {
                // Synthesized call ids are dropped on the wire.
                var calls = new JsonArray();
                foreach (var call in m.ToolCalls)
                    calls.Add(new JsonObject
                    {
                        ["function"] = new JsonObject
                        {
                            ["name"]      = call.Name,
                            ["arguments"] = call.Arguments.DeepClone(),
                        },
                    });
                arr.Add(new JsonObject { ["role"] = "assistant", ["content"] = m.Content, ["tool_calls"] = calls });
            }
            else
            {
                arr.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
            }
        }
        return arr;
    }

    /// <summary>Parse Ollama wire tool calls, synthesizing ids "call_N" in order.</summary>
    private static List<ToolCall>? ParseToolCalls(JsonArray? raw, int startIndex = 0)
    {
        if (raw is null || raw.Count == 0) return null;
        var calls = new List<ToolCall>();
        foreach (var item in raw)
        {
            var function = item?["function"];
            var name = function?["name"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name)) continue;
            var arguments = function?["arguments"] as JsonObject;
            calls.Add(new ToolCall($"call_{startIndex + calls.Count}", name,
                arguments?.DeepClone()?.AsObject() ?? new JsonObject()));
        }
        return calls.Count > 0 ? calls : null;
    }

    // Mirrors the Python reference _map_finish_reason table.
    private static string MapDoneReason(string? reason) => reason switch
    {
        null     => "stop",
        "stop"   => "stop",
        "length" => "length",
        "load"   => "stop",
        _        => "unknown",
    };
}
