using Priest.Schema;

namespace Priest.Providers;

public record ChatMessage(string Role, string Content);

/// <summary>Interface that all provider adapters must implement.</summary>
public interface IProviderAdapter
{
    /// <summary>Execute a request and return the full response.</summary>
    Task<AdapterResult> CompleteAsync(IList<ChatMessage> messages, PriestConfig config,
        OutputSpec? outputSpec = null, CancellationToken ct = default);

    /// <summary>Yield text chunks as they arrive.</summary>
    IAsyncEnumerable<string> StreamAsync(IList<ChatMessage> messages, PriestConfig config,
        OutputSpec? outputSpec = null, CancellationToken ct = default);
}
