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
        OutputSpec? outputSpec = null, CancellationToken ct = default)
    {
        var body = BuildBody(messages, config, outputSpec, stream: false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(config.Timeout);

        HttpResponseMessage response;
        try
        {
            var req = CreateRequest($"{_baseUrl}/v1/chat/completions", body);
            response = await _http.SendAsync(req, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
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
        var text    = node?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        var finish  = node?["choices"]?[0]?["finish_reason"]?.GetValue<string>();
        var inToks  = node?["usage"]?["prompt_tokens"]?.GetValue<int>();
        var outToks = node?["usage"]?["completion_tokens"]?.GetValue<int>();
        return new AdapterResult(text, finish, inToks, outToks);
    }

    public async IAsyncEnumerable<string> StreamAsync(IList<ChatMessage> messages, PriestConfig config,
        OutputSpec? outputSpec = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = BuildBody(messages, config, outputSpec, stream: true);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(config.Timeout);

        HttpResponseMessage response;
        try
        {
            var req = CreateRequest($"{_baseUrl}/v1/chat/completions", body);
            response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
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

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ")) continue;
            var data = line[6..];
            if (data == "[DONE]") yield break;
            JsonNode? node;
            try { node = JsonNode.Parse(data); } catch { continue; }
            var chunk = node?["choices"]?[0]?["delta"]?["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(chunk)) yield return chunk;
        }
    }

    private static JsonObject BuildBody(IList<ChatMessage> messages, PriestConfig config,
        OutputSpec? outputSpec, bool stream)
    {
        var arr = new JsonArray();
        foreach (var m in messages)
            arr.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });

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
        foreach (var kv in config.ProviderOptions) body[kv.Key] = kv.Value?.DeepClone();
        return body;
    }

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
