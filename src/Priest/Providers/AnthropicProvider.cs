using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Priest.Errors;
using Priest.Schema;

namespace Priest.Providers;

/// <summary>Anthropic provider. Uses SSE streaming via /v1/messages.</summary>
public class AnthropicProvider : IProviderAdapter
{
    private const string ApiUrl          = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";
    private const int    DefaultMaxTokens = 1024;

    private readonly string _apiKey;
    private static readonly HttpClient _http = new();

    public AnthropicProvider(string apiKey)
    {
        _apiKey = apiKey;
    }

    public async Task<AdapterResult> CompleteAsync(IList<ChatMessage> messages, PriestConfig config,
        OutputSpec? outputSpec = null, CancellationToken ct = default)
    {
        var (system, chat) = SplitMessages(messages);
        var body = BuildBody(config, chat, system, stream: false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(config.Timeout);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(CreateRequest(body), cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
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
        var finish  = node?["stop_reason"]?.GetValue<string>();
        var inToks  = node?["usage"]?["input_tokens"]?.GetValue<int>();
        var outToks = node?["usage"]?["output_tokens"]?.GetValue<int>();
        return new AdapterResult(text, finish, inToks, outToks);
    }

    public async IAsyncEnumerable<string> StreamAsync(IList<ChatMessage> messages, PriestConfig config,
        OutputSpec? outputSpec = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (system, chat) = SplitMessages(messages);
        var body = BuildBody(config, chat, system, stream: true);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(config.Timeout);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(CreateRequest(body), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
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
        string? currentEvent = null;

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) { currentEvent = null; continue; }

            if (line.StartsWith("event: ")) { currentEvent = line[7..]; continue; }

            if (line.StartsWith("data: ") && currentEvent == "content_block_delta")
            {
                JsonNode? node;
                try { node = JsonNode.Parse(line[6..]); } catch { continue; }
                if (node?["delta"]?["type"]?.GetValue<string>() == "text_delta")
                {
                    var chunk = node?["delta"]?["text"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(chunk)) yield return chunk;
                }
            }
        }
    }

    private static (string system, List<ChatMessage> chat) SplitMessages(IList<ChatMessage> messages)
    {
        var systemParts = messages.Where(m => m.Role == "system").Select(m => m.Content);
        var chat = messages.Where(m => m.Role != "system").ToList();
        return (string.Join("\n\n", systemParts), chat);
    }

    private static JsonObject BuildBody(PriestConfig config, List<ChatMessage> chat, string system, bool stream)
    {
        var arr = new JsonArray();
        foreach (var m in chat)
            arr.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });

        var body = new JsonObject
        {
            ["model"]      = config.Model,
            ["max_tokens"] = config.MaxOutputTokens ?? DefaultMaxTokens,
            ["messages"]   = arr,
            ["stream"]     = stream,
        };
        if (!string.IsNullOrEmpty(system)) body["system"] = system;
        foreach (var kv in config.ProviderOptions) body[kv.Key] = kv.Value?.DeepClone();
        return body;
    }

    private HttpRequestMessage CreateRequest(JsonObject body) =>
        new(HttpMethod.Post, ApiUrl)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            Headers = { { "x-api-key", _apiKey }, { "anthropic-version", AnthropicVersion } },
        };
}
