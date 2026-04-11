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
        OutputSpec? outputSpec = null, CancellationToken ct = default)
        => Task.FromResult(new AdapterResult(_responseText, "stop", 10, 5));

    public async IAsyncEnumerable<string> StreamAsync(IList<ChatMessage> messages, PriestConfig config,
        OutputSpec? outputSpec = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var word in _responseText.Split(' '))
        {
            yield return word;
            await Task.Yield();
        }
    }
}
