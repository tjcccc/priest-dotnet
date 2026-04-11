namespace Priest.Errors;

/// <summary>All priest error codes. Values match the spec-defined strings.</summary>
public static class PriestErrorCode
{
    public const string ProfileNotFound       = "PROFILE_NOT_FOUND";
    public const string ProfileInvalid        = "PROFILE_INVALID";
    public const string SessionNotFound       = "SESSION_NOT_FOUND";
    public const string SessionStoreError     = "SESSION_STORE_ERROR";
    public const string ProviderNotRegistered = "PROVIDER_NOT_REGISTERED";
    public const string ProviderTimeout       = "PROVIDER_TIMEOUT";
    public const string ProviderError         = "PROVIDER_ERROR";
    public const string ProviderRateLimited   = "PROVIDER_RATE_LIMITED";
    public const string RequestInvalid        = "REQUEST_INVALID";
    public const string InternalError         = "INTERNAL_ERROR";
}

/// <summary>
/// Base exception for all priest errors.
///
/// Two exceptions are always thrown and never placed into PriestResponse.Error:
/// - PROVIDER_NOT_REGISTERED — no adapter means no response can be constructed.
/// - SESSION_NOT_FOUND — the caller explicitly opted out of session creation.
///
/// All other provider errors are caught and placed into PriestResponse.Error.
/// </summary>
public class PriestException : Exception
{
    public string Code { get; }
    public Dictionary<string, string> Details { get; }

    public PriestException(string code, string message, Dictionary<string, string>? details = null)
        : base(message)
    {
        Code = code;
        Details = details ?? new();
    }

    public static PriestException ProfileNotFound(string name) =>
        new(PriestErrorCode.ProfileNotFound, $"Profile '{name}' not found",
            new() { ["profile"] = name });

    public static PriestException SessionNotFound(string sessionId) =>
        new(PriestErrorCode.SessionNotFound, $"Session '{sessionId}' not found",
            new() { ["session_id"] = sessionId });

    public static PriestException ProviderNotRegistered(string provider) =>
        new(PriestErrorCode.ProviderNotRegistered, $"Provider '{provider}' is not registered",
            new() { ["provider"] = provider });

    public static PriestException ProviderTimeout(string provider, TimeSpan timeout) =>
        new(PriestErrorCode.ProviderTimeout, $"Provider '{provider}' timed out after {timeout.TotalSeconds}s",
            new() { ["provider"] = provider, ["timeout"] = timeout.TotalSeconds.ToString() });

    public static PriestException ProviderError(string provider, string message) =>
        new(PriestErrorCode.ProviderError, $"Provider '{provider}' error: {message}",
            new() { ["provider"] = provider });

    public static PriestException ProviderRateLimited(string provider, double? retryAfter = null)
    {
        var msg = retryAfter.HasValue
            ? $"Provider '{provider}' rate limited — retry after {retryAfter}s"
            : $"Provider '{provider}' rate limited";
        var details = new Dictionary<string, string> { ["provider"] = provider };
        if (retryAfter.HasValue) details["retry_after"] = retryAfter.Value.ToString();
        return new(PriestErrorCode.ProviderRateLimited, msg, details);
    }
}
