using Priest.Providers;
using Priest.Schema;

namespace Priest.Tests;

public class MockAdapter : IProviderAdapter
{
    private readonly string _responseText;

    public MockAdapter(string responseText = "mock response")
    {
        _responseText = responseText;
    }

    public Task<AdapterResult> CompleteAsync(IList<ChatMessage> messages, PriestConfig config,
        OutputSpec? outputSpec = null, AdapterCallOptions? options = null, CancellationToken ct = default)
        => Task.FromResult(new AdapterResult(_responseText, "stop", 10, 5));

    public async IAsyncEnumerable<string> StreamAsync(IList<ChatMessage> messages, PriestConfig config,
        OutputSpec? outputSpec = null, AdapterCallOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var word in _responseText.Split(' '))
        {
            yield return word;
            await Task.Yield();
        }
    }
}

/// <summary>
/// Adapter scripted with a sequence of AdapterResults, one per CompleteAsync
/// call. Records every messages list and call options it receives.
/// </summary>
public class ScriptedAdapter : IProviderAdapter
{
    private readonly IReadOnlyList<AdapterResult> _results;
    private int _cursor;

    public List<(IList<ChatMessage> Messages, AdapterCallOptions? Options)> Calls { get; } = new();

    public ScriptedAdapter(IReadOnlyList<AdapterResult> results)
    {
        _results = results;
    }

    public Task<AdapterResult> CompleteAsync(IList<ChatMessage> messages, PriestConfig config,
        OutputSpec? outputSpec = null, AdapterCallOptions? options = null, CancellationToken ct = default)
    {
        Calls.Add((messages, options));
        var result = _results[Math.Min(_cursor, _results.Count - 1)];
        _cursor++;
        return Task.FromResult(result);
    }

    public async IAsyncEnumerable<string> StreamAsync(IList<ChatMessage> messages, PriestConfig config,
        OutputSpec? outputSpec = null, AdapterCallOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var result = await CompleteAsync(messages, config, outputSpec, options, ct);
        if (result.Text.Length > 0) yield return result.Text;
    }
}
