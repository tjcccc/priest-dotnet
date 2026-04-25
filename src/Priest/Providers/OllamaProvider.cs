using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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
        OutputSpec? outputSpec = null, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        await foreach (var chunk in StreamAsync(messages, config, outputSpec, ct))
            sb.Append(chunk);
        return new AdapterResult(sb.ToString(), "stop");
    }

    public async IAsyncEnumerable<string> StreamAsync(IList<ChatMessage> messages, PriestConfig config,
        OutputSpec? outputSpec = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"]    = config.Model,
            ["messages"] = BuildMessages(messages),
            ["stream"]   = true,
        };
        if (outputSpec?.JsonSchema is not null) body["format"] = outputSpec.JsonSchema.DeepClone();
        else if (outputSpec?.ProviderFormat == Schema.ProviderFormat.Json) body["format"] = "json";
        if (config.MaxOutputTokens.HasValue)
            body["options"] = new JsonObject { ["num_predict"] = config.MaxOutputTokens.Value };
        foreach (var kv in config.ProviderOptions) body[kv.Key] = kv.Value?.DeepClone();

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
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
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

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? node;
            try { node = JsonNode.Parse(line); } catch { continue; }
            var chunk = node?["message"]?["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(chunk)) yield return chunk;
        }
    }

    private static JsonArray BuildMessages(IList<ChatMessage> messages)
    {
        var arr = new JsonArray();
        foreach (var m in messages)
            arr.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
        return arr;
    }
}
