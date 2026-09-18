using System.Diagnostics;

namespace CodexAccountSwitcher.Services;

public sealed class BrowserProfileService
{
    private readonly string _profileRoot;

    public BrowserProfileService(string? profileRoot = null)
    {
        _profileRoot = Path.GetFullPath(profileRoot ?? Path.Combine(AppPaths.DataRoot, "browser-profiles"));
    }

    public void OpenAuthenticationUrl(string url, string profileKey)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidDataException("Codex returned an invalid sign-in address.");

        var safeKey = ValidateProfileKey(profileKey);
        var profileDirectory = Path.GetFullPath(Path.Combine(_profileRoot, safeKey));
        if (!AppPaths.IsPathInside(profileDirectory, _profileRoot))
            throw new InvalidOperationException("The browser-profile boundary check failed.");

        Directory.CreateDirectory(profileDirectory);
        var browser = FindBrowserExecutable();
        if (browser is null)
        {
            DiagnosticTrace.Write($"isolated browser unavailable; using system browser for profile {safeKey[..8]}");
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return;
        }

        var start = new ProcessStartInfo
        {
            FileName = browser,
            UseShellExecute = false
        };
        start.ArgumentList.Add($"--user-data-dir={profileDirectory}");
        start.ArgumentList.Add("--no-first-run");
        start.ArgumentList.Add("--no-default-browser-check");
        start.ArgumentList.Add("--new-window");
        start.ArgumentList.Add(url);
        DiagnosticTrace.Write($"launching isolated browser {Path.GetFileName(browser)} for profile {safeKey[..8]}");
        var process = Process.Start(start);
        if (process is null)
            throw new InvalidOperationException("Windows did not start the private sign-in browser.");
    }

    internal static string ValidateProfileKey(string profileKey)
    {
        var trimmed = profileKey.Trim();
        if (trimmed.Length is < 16 or > 64 || !trimmed.All(Uri.IsHexDigit))
            throw new ArgumentException("The private browser profile identifier is invalid.", nameof(profileKey));
        return trimmed.ToLowerInvariant();
    }

    private static string? FindBrowserExecutable()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(local, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
