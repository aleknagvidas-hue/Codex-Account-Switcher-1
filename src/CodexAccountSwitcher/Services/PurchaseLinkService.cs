using System.Diagnostics;

namespace CodexAccountSwitcher.Services;

internal static class PurchaseLinkService
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();
        if (trimmed.Length > 2048
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("Enter a complete http:// or https:// purchase link.");
        }

        return uri.AbsoluteUri;
    }

    public static string DisplayHost(string? value)
    {
        var normalized = Normalize(value);
        if (normalized is null) return string.Empty;
        var host = new Uri(normalized).Host;
        return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
    }

    public static bool IsValid(string? value)
    {
        try { return Normalize(value) is not null; }
        catch (ArgumentException) { return false; }
    }

    public static ProcessStartInfo BuildStartInfo(string value)
    {
        var normalized = Normalize(value)
            ?? throw new ArgumentException("A purchase link is required.");
        return new ProcessStartInfo(normalized) { UseShellExecute = true };
    }
}
