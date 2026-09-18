namespace CodexAccountSwitcher.Services;

internal sealed class CodexRequestException : InvalidOperationException
{
    public string Reason { get; }
    public bool RequiresLogin => Reason is "refresh_token_reused" or "refresh_token_expired" or "refresh_token_invalidated" or "authentication_required";

    private CodexRequestException(string reason, string message) : base(message) => Reason = reason;

    internal static CodexRequestException FromServerMessage(string message, int? code = null)
    {
        // Classify only known values. Never copy server text, tokens, URLs or account data into logs/UI.
        var normalized = message.ToLowerInvariant();
        if (normalized.Contains("refresh_token_reused") || normalized.Contains("refresh token was already used"))
            return new("refresh_token_reused", "This saved login's refresh token was already used. Reconnect this account once.");
        if (normalized.Contains("refresh_token_expired") || normalized.Contains("refresh token has expired"))
            return new("refresh_token_expired", "This saved login has expired. Reconnect this account once.");
        if (normalized.Contains("refresh_token_invalidated") || normalized.Contains("refresh token has been invalidated"))
            return new("refresh_token_invalidated", "This saved login was revoked. Reconnect this account once.");
        if (normalized.Contains("401") || normalized.Contains("unauthorized") || normalized.Contains("not authenticated") || normalized.Contains("not logged in"))
            return new("authentication_required", "Codex requires this account to sign in again.");
        if (normalized.Contains("connection") || normalized.Contains("timed out") || normalized.Contains("error sending request") || normalized.Contains("dns"))
            return new("connection_failed", "Codex could not reach the sign-in service. Check the connection and retry Switch.");
        if (code is -32601 or -32602)
            return new("protocol_error", "The installed Codex version rejected the authentication request.");
        return new("request_failed", "Codex could not validate this sign-in. Retry Switch; reconnect the account if it keeps failing.");
    }
}
