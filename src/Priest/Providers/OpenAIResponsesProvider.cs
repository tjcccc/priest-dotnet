using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Priest.Errors;
using Priest.Schema;

namespace Priest.Providers;

/// <summary>
/// First-class provider for OpenAI's Responses endpoint. This adapter is
/// separate from OpenAICompatProvider and does not alter Chat Completions.
/// </summary>
public class OpenAIResponsesProvider : IProviderAdapter
{
    private const string DefaultBaseUrl = "https://api.openai.com";
    private const string ReasoningFormat = "openai.responses.reasoning.v1";

    private static readonly HttpClient DefaultHttpClient = new();

    private readonly string? _apiKey;
    private readonly string _url;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly HttpClient _http;

    /// <param name="baseUrl">Base URL used to form /v1/responses.</param>
    /// <param name="apiKey">Optional bearer API key.</param>
    /// <param name="url">Optional exact Responses endpoint URL.</param>
    /// <param name="headers">Additional request headers, overriding defaults.</param>
    /// <param name="httpClient">Optional host-owned HTTP dispatcher/transport.</param>
    public OpenAIResponsesProvider(
        string baseUrl = DefaultBaseUrl,
        string? apiKey = null,
        string? url = null,
        IReadOnlyDictionary<string, string>? headers = null,
        HttpClient? httpClient = null)
    {
        _apiKey = apiKey;
        _url = url ?? $"{baseUrl.TrimEnd('/')}/v1/responses";
        _headers = headers ?? new Dictionary<string, string>();
        _http = httpClient ?? DefaultHttpClient;
    }

    public bool SupportsProviderTool(ProviderToolDefinition tool, PriestConfig config)
        => tool.Type == ProviderToolDefinition.WebSearch.Type;

    public async Task<AdapterResult> CompleteAsync(
        IList<ChatMessage> messages,
        PriestConfig config,
        OutputSpec? outputSpec = null,
        AdapterCallOptions? options = null,
        CancellationToken ct = default)
    {
        var body = BuildBody(messages, config, outputSpec, options, stream: false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(config.Timeout);

        try
        {
            using var request = CreateRequest(body);
            using var response = await _http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync(ct);
                throw PriestException.ProviderError(
                    "openai-responses",
                    $"HTTP {(int)response.StatusCode}: {errorText}");
            }

            var data = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            return ParseResponse(data);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw PriestException.RequestAborted("openai-responses");
        }
        catch (OperationCanceledException)
        {
            throw PriestException.ProviderTimeout("openai-responses", config.Timeout);
        }
        catch (PriestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw PriestException.ProviderError("openai-responses", ex.Message);
        }
    }

    public async IAsyncEnumerable<string> StreamAsync(
        IList<ChatMessage> messages,
        PriestConfig config,
        OutputSpec? outputSpec = null,
        AdapterCallOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var ev in StreamEventsAsync(messages, config, outputSpec, options, ct))
            if (ev.Type == "text_delta" && ev.Text is not null)
                yield return ev.Text;
    }

    public async IAsyncEnumerable<AdapterStreamEvent> StreamEventsAsync(
        IList<ChatMessage> messages,
        PriestConfig config,
        OutputSpec? outputSpec = null,
        AdapterCallOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = BuildBody(messages, config, outputSpec, options, stream: true);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(config.Timeout);

        HttpResponseMessage response;
        try
        {
            using var request = CreateRequest(body);
            response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw PriestException.RequestAborted("openai-responses");
        }
        catch (OperationCanceledException)
        {
            throw PriestException.ProviderTimeout("openai-responses", config.Timeout);
        }
        catch (Exception ex)
        {
            throw PriestException.ProviderError("openai-responses", ex.Message);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync(ct);
                throw PriestException.ProviderError(
                    "openai-responses",
                    $"HTTP {(int)response.StatusCode}: {errorText}");
            }

            using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream);
            var partials = new Dictionary<int, PartialToolCall>();
            var emittedCallIds = new HashSet<string>();
            var nextEventIndex = 0;
            var terminalSeen = false;

            PartialToolCall EnsurePartial(int outputIndex, JsonObject? item = null)
            {
                if (!partials.TryGetValue(outputIndex, out var partial))
                {
                    partial = new PartialToolCall
                    {
                        EventIndex = nextEventIndex++,
                        CallId = StringValue(item?["call_id"]),
                        Name = StringValue(item?["name"]),
                        Arguments = StringValue(item?["arguments"]) ?? "",
                    };
                    partials[outputIndex] = partial;
                }
                else if (item is not null)
                {
                    partial.CallId = StringValue(item["call_id"]) ?? partial.CallId;
                    partial.Name = StringValue(item["name"]) ?? partial.Name;
                    if (item["arguments"] is not null)
                        partial.Arguments = StringValue(item["arguments"]) ?? partial.Arguments;
                }
                return partial;
            }

            AdapterStreamEvent? FinishPartial(PartialToolCall partial)
            {
                if (partial.Ended) return null;
                partial.Ended = true;
                var id = partial.CallId ?? $"call_{partial.EventIndex}";
                emittedCallIds.Add(id);
                return new AdapterStreamEvent("tool_call_end")
                {
                    Index = partial.EventIndex,
                    ToolCall = new ToolCall(id, partial.Name ?? "", ParseArguments(partial.Arguments)),
                };
            }

            await foreach (var data in ReadSseData(reader, cts.Token))
            {
                if (data == "[DONE]") break;

                JsonObject? ev;
                try { ev = JsonNode.Parse(data) as JsonObject; }
                catch (JsonException) { continue; }
                if (ev is null) continue;

                var type = StringValue(ev["type"]);
                if (type == "response.output_text.delta")
                {
                    if (StringValue(ev["delta"]) is { Length: > 0 } delta)
                        yield return new AdapterStreamEvent("text_delta") { Text = delta };
                    continue;
                }

                if (type == "response.reasoning_summary_text.delta")
                {
                    if (StringValue(ev["delta"]) is { Length: > 0 } delta)
                        yield return new AdapterStreamEvent("reasoning_summary_delta") { Text = delta };
                    continue;
                }

                if (type == "response.output_item.added")
                {
                    var item = ev["item"] as JsonObject;
                    if (StringValue(item?["type"]) != "function_call") continue;
                    var outputIndex = IntValue(ev["output_index"]) ?? partials.Count;
                    var partial = EnsurePartial(outputIndex, item);
                    yield return new AdapterStreamEvent("tool_call_start")
                    {
                        Index = partial.EventIndex,
                        Id = partial.CallId,
                        Name = partial.Name,
                    };
                    continue;
                }

                if (type == "response.function_call_arguments.delta")
                {
                    var partial = EnsurePartial(IntValue(ev["output_index"]) ?? 0);
                    if (StringValue(ev["delta"]) is { Length: > 0 } delta)
                    {
                        partial.Arguments += delta;
                        yield return new AdapterStreamEvent("tool_call_delta")
                        {
                            Index = partial.EventIndex,
                            ArgumentsDelta = delta,
                        };
                    }
                    continue;
                }

                if (type == "response.function_call_arguments.done")
                {
                    var partial = EnsurePartial(IntValue(ev["output_index"]) ?? 0);
                    partial.Arguments = StringValue(ev["arguments"]) ?? partial.Arguments;
                    partial.Name = StringValue(ev["name"]) ?? partial.Name;
                    if (FinishPartial(partial) is { } finished) yield return finished;
                    continue;
                }

                if (type == "response.output_item.done")
                {
                    var item = ev["item"] as JsonObject;
                    if (StringValue(item?["type"]) != "function_call") continue;
                    var partial = EnsurePartial(IntValue(ev["output_index"]) ?? 0, item);
                    if (FinishPartial(partial) is { } finished) yield return finished;
                    continue;
                }

                if (type == "response.completed")
                {
                    terminalSeen = true;
                    var parsed = ParseResponse(ev["response"]);

                    foreach (var partial in partials.Values.OrderBy(item => item.EventIndex))
                        if (FinishPartial(partial) is { } finished) yield return finished;

                    foreach (var call in parsed.ToolCalls ?? [])
                    {
                        if (!emittedCallIds.Add(call.Id)) continue;
                        var index = nextEventIndex++;
                        yield return new AdapterStreamEvent("tool_call_start")
                        {
                            Index = index,
                            Id = call.Id,
                            Name = call.Name,
                        };
                        yield return new AdapterStreamEvent("tool_call_end")
                        {
                            Index = index,
                            ToolCall = call,
                        };
                    }

                    if (parsed.InputTokens.HasValue || parsed.OutputTokens.HasValue
                        || parsed.CachedInputTokens.HasValue || parsed.ReasoningTokens.HasValue)
                    {
                        yield return new AdapterStreamEvent("usage")
                        {
                            InputTokens = parsed.InputTokens,
                            OutputTokens = parsed.OutputTokens,
                            CachedInputTokens = parsed.CachedInputTokens,
                            ReasoningTokens = parsed.ReasoningTokens,
                        };
                    }
                    yield return new AdapterStreamEvent("finish")
                    {
                        FinishReason = parsed.FinishReason,
                        Reasoning = parsed.Reasoning,
                    };
                    yield break;
                }

                if (type is "response.failed" or "response.cancelled")
                {
                    terminalSeen = true;
                    throw ProviderResponseError(ev["response"]);
                }

                if (type == "error")
                {
                    terminalSeen = true;
                    throw PriestException.ProviderError(
                        "openai-responses",
                        StringValue(ev["message"]) ?? ev.ToJsonString());
                }
            }

            foreach (var partial in partials.Values.OrderBy(item => item.EventIndex))
                if (FinishPartial(partial) is { } finished) yield return finished;

            if (!terminalSeen)
            {
                yield return new AdapterStreamEvent("finish")
                {
                    FinishReason = partials.Count > 0 ? "tool_calls" : "stop",
                };
            }
        }
    }

    internal static JsonObject BuildBody(
        IList<ChatMessage> messages,
        PriestConfig config,
        OutputSpec? outputSpec,
        AdapterCallOptions? options,
        bool stream)
    {
        var body = new JsonObject { ["store"] = false };

        if (config.MaxOutputTokens.HasValue)
            body["max_output_tokens"] = config.MaxOutputTokens.Value;

        if (BuildReasoningConfig(config) is { } reasoning)
            body["reasoning"] = reasoning;

        if (outputSpec?.JsonSchema is not null)
        {
            body["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = outputSpec.JsonSchemaName ?? "response",
                    ["schema"] = outputSpec.JsonSchema.DeepClone(),
                    ["strict"] = outputSpec.JsonSchemaStrict,
                },
            };
        }
        else if (outputSpec?.ProviderFormat == ProviderFormat.Json)
        {
            body["text"] = new JsonObject
            {
                ["format"] = new JsonObject { ["type"] = "json_object" },
            };
        }

        var toolArray = new JsonArray();
        foreach (var tool in options?.ProviderTools ?? Array.Empty<ProviderToolDefinition>())
        {
            toolArray.Add(new JsonObject { ["type"] = tool.Type });
        }
        foreach (var tool in options?.Tools ?? Array.Empty<ToolDefinition>())
        {
            toolArray.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description ?? "",
                ["parameters"] = tool.Parameters?.DeepClone() ?? new JsonObject(),
            });
        }
        if (toolArray.Count > 0)
        {
            body["tools"] = toolArray;
            if (options?.ToolChoice is { } choice)
            {
                body["tool_choice"] = choice.Name is not null
                    ? new JsonObject { ["type"] = "function", ["name"] = choice.Name }
                    : JsonValue.Create(choice.Mode);
            }
        }

        foreach (var (key, value) in config.ProviderOptions)
            body[key] = value?.DeepClone();

        // Adapter-owned operation invariants override provider options.
        body["model"] = config.Model;
        body["input"] = BuildInput(messages);
        body["stream"] = stream;
        return body;
    }

    internal static AdapterResult ParseResponse(JsonNode? data)
    {
        if (data is not JsonObject response) response = new JsonObject();
        var status = StringValue(response["status"]);
        if (status is "failed" or "cancelled" || response["error"] is not null)
            throw ProviderResponseError(response);

        var textParts = new List<string>();
        var toolCalls = new List<ToolCall>();
        var summaryParts = new List<string>();
        var reasoningStates = new List<OpaqueReasoningState>();

        foreach (var item in response["output"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var type = StringValue(item["type"]);
            if (type == "message")
            {
                foreach (var part in item["content"]?.AsArray().OfType<JsonObject>() ?? [])
                {
                    if (StringValue(part["type"]) == "output_text"
                        && StringValue(part["text"]) is { } text)
                        textParts.Add(text);
                }
                continue;
            }

            if (type == "function_call" && StringValue(item["name"]) is { Length: > 0 } name)
            {
                toolCalls.Add(new ToolCall(
                    StringValue(item["call_id"]) ?? StringValue(item["id"]) ?? $"call_{toolCalls.Count}",
                    name,
                    ParseArguments(StringValue(item["arguments"]) ?? "")));
                continue;
            }

            if (type == "reasoning")
            {
                foreach (var part in item["summary"]?.AsArray().OfType<JsonObject>() ?? [])
                {
                    if (StringValue(part["type"]) == "summary_text"
                        && StringValue(part["text"]) is { Length: > 0 } summary)
                        summaryParts.Add(summary);
                }
                if (SafeReasoningState(item) is { } state) reasoningStates.Add(state);
            }
        }

        var hasTools = toolCalls.Count > 0;
        var continuation = hasTools && reasoningStates.Count > 0 ? reasoningStates : null;
        ReasoningInfo? reasoning = summaryParts.Count > 0 || continuation is not null
            ? new ReasoningInfo(
                summaryParts.Count > 0 ? string.Join("\n\n", summaryParts) : null,
                continuation)
            : null;

        var usage = response["usage"];
        return new AdapterResult(
            string.Concat(textParts),
            MapFinishReason(response, hasTools),
            IntValue(usage?["input_tokens"]),
            IntValue(usage?["output_tokens"]),
            IntValue(usage?["input_tokens_details"]?["cached_tokens"]),
            hasTools ? toolCalls : null,
            IntValue(usage?["output_tokens_details"]?["reasoning_tokens"]),
            reasoning);
    }

    private static JsonArray BuildInput(IList<ChatMessage> messages)
    {
        var input = new JsonArray();
        foreach (var message in messages)
        {
            if (message.Role == "tool")
            {
                input.Add(new JsonObject
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = message.ToolCallId,
                    ["output"] = message.Content,
                });
                continue;
            }

            if (message.Role == "assistant" && message.ToolCalls is { Count: > 0 })
            {
                foreach (var state in message.Reasoning?.Continuation ?? [])
                    if (state.Format == ReasoningFormat)
                        input.Add(state.Value?.DeepClone());

                foreach (var call in message.ToolCalls)
                {
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call",
                        ["call_id"] = call.Id,
                        ["name"] = call.Name,
                        ["arguments"] = call.Arguments.ToJsonString(),
                    });
                }
                continue;
            }

            input.Add(new JsonObject
            {
                ["role"] = message.Role,
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = message.Role == "assistant" ? "output_text" : "input_text",
                        ["text"] = message.Content,
                    },
                },
            });
        }
        return input;
    }

    private static JsonObject? BuildReasoningConfig(PriestConfig config)
    {
        var requested = config.Reasoning;
        if (requested is null) return null;

        var reasoning = new JsonObject();
        if (requested.Effort.HasValue)
            reasoning["effort"] = ReasoningEffortValue(requested.Effort.Value);
        else if (requested.Enabled == false)
            reasoning["effort"] = "none";

        if (requested.Summary == ReasoningSummaryMode.Auto)
            reasoning["summary"] = "auto";

        return reasoning.Count > 0 ? reasoning : null;
    }

    private static OpaqueReasoningState? SafeReasoningState(JsonObject item)
    {
        // Raw reasoning content must never be surfaced or replayed.
        if (item["content"] is JsonArray { Count: > 0 }) return null;

        var value = new JsonObject { ["type"] = "reasoning" };
        foreach (var key in new[] { "id", "status", "summary", "encrypted_content" })
            if (item[key] is { } field) value[key] = field.DeepClone();

        if (item["encrypted_content"] is null && item["id"] is null) return null;
        return new OpaqueReasoningState(ReasoningFormat, value);
    }

    private static string MapFinishReason(JsonObject response, bool hasTools)
    {
        if (hasTools) return "tool_calls";
        var status = StringValue(response["status"]);
        if (status == "incomplete")
        {
            return StringValue(response["incomplete_details"]?["reason"]) switch
            {
                "max_output_tokens" => "length",
                "content_filter" => "content_filter",
                _ => "unknown",
            };
        }
        return status is null or "completed" ? "stop" : "unknown";
    }

    private static PriestException ProviderResponseError(JsonNode? data)
    {
        var error = data?["error"];
        var code = StringValue(error?["code"]);
        var prefix = code is null ? "" : $"{code}: ";
        var message = StringValue(error?["message"])
            ?? $"response status {StringValue(data?["status"]) ?? "failed"}";
        return PriestException.ProviderError("openai-responses", prefix + message);
    }

    private HttpRequestMessage CreateRequest(JsonObject body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (_apiKey is not null)
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");

        foreach (var (name, value) in _headers)
        {
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                request.Content.Headers.Remove(name);
                request.Content.Headers.TryAddWithoutValidation(name, value);
            }
            else if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                request.Content.Headers.Remove(name);
                request.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }
        return request;
    }

    private static async IAsyncEnumerable<string> ReadSseData(
        StreamReader reader,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var dataLines = new List<string>();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (dataLines.Count > 0)
                {
                    yield return string.Join("\n", dataLines);
                    dataLines.Clear();
                }
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
                dataLines.Add(line[5..].TrimStart(' '));
        }
        if (dataLines.Count > 0) yield return string.Join("\n", dataLines);
    }

    private static JsonObject ParseArguments(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new JsonObject();
        try { return JsonNode.Parse(raw) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    private static string ReasoningEffortValue(ReasoningEffort effort) => effort switch
    {
        ReasoningEffort.None => "none",
        ReasoningEffort.Minimal => "minimal",
        ReasoningEffort.Low => "low",
        ReasoningEffort.Medium => "medium",
        ReasoningEffort.High => "high",
        ReasoningEffort.XHigh => "xhigh",
        ReasoningEffort.Max => "max",
        _ => throw new ArgumentOutOfRangeException(nameof(effort)),
    };

    private static string? StringValue(JsonNode? value)
    {
        try { return value?.GetValue<string>(); }
        catch (InvalidOperationException) { return null; }
    }

    private static int? IntValue(JsonNode? value)
    {
        try { return value?.GetValue<int>(); }
        catch (InvalidOperationException) { return null; }
    }

    private sealed class PartialToolCall
    {
        public int EventIndex { get; init; }
        public string? CallId { get; set; }
        public string? Name { get; set; }
        public string Arguments { get; set; } = "";
        public bool Ended { get; set; }
    }
}
