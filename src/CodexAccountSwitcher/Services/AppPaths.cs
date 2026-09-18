using System.Diagnostics;

namespace CodexAccountSwitcher.Services;

public static class AppPaths
{
    public const string CodexPackagePrefix = "OpenAI.Codex_";
    public const string CodexAppUserModelId = "OpenAI.Codex_2p2nqsd0c76g0!App";

    public static string DataRoot
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable("CODEX_SWITCHER_DATA_HOME");
            return string.IsNullOrWhiteSpace(overridePath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexAccountSwitcher")
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(overridePath));
        }
    }

    public static string CurrentCodexHome
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
        }
    }

    public static string CurrentAuthPath => Path.Combine(CurrentCodexHome, "auth.json");

    public static bool IsPathInside(string candidate, string root)
    {
        var candidateFull = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidateFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
    }

    public static string? FindCodexExecutable()
    {
        var npmCodexPackage = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm",
            "node_modules",
            "@openai",
            "codex",
            "node_modules",
            "@openai",
            "codex-win32-x64",
            "vendor",
            "x86_64-pc-windows-msvc",
            "bin",
            "codex.exe");
        if (File.Exists(npmCodexPackage))
        {
            return npmCodexPackage;
        }

        foreach (var process in Process.GetProcessesByName("codex"))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (IsTrustedCodexExecutable(path))
                {
                    return path;
                }
            }
            catch
            {
                // Access to another process can fail; continue to deterministic fallbacks.
            }
            finally
            {
                process.Dispose();
            }
        }

        var localRuntimeRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI", "Codex", "bin");
        try
        {
            var local = Directory.EnumerateFiles(localRuntimeRoot, "codex.exe", SearchOption.AllDirectories)
                .Where(IsLocalCodexExecutable)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (local is not null) return local;
        }
        catch { /* Continue to the packaged fallback. */ }

        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            try
            {
                var guiPath = process.MainModule?.FileName;
                if (IsPackagedCodexGui(guiPath))
                {
                    var candidate = Path.Combine(Path.GetDirectoryName(guiPath)!, "resources", "codex.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch
            {
                // Continue to package enumeration.
            }
            finally
            {
                process.Dispose();
            }
        }

        var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        try
        {
            return Directory.EnumerateDirectories(windowsApps, "OpenAI.Codex_*", SearchOption.TopDirectoryOnly)
                .Select(directory => Path.Combine(directory, "app", "resources", "codex.exe"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    public static bool IsPackagedCodexGui(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && path.Contains($"{Path.DirectorySeparatorChar}WindowsApps{Path.DirectorySeparatorChar}{CodexPackagePrefix}", StringComparison.OrdinalIgnoreCase)
        && string.Equals(Path.GetFileName(path), "ChatGPT.exe", StringComparison.OrdinalIgnoreCase);

    public static bool IsPackagedCodexExecutable(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && path.Contains($"{Path.DirectorySeparatorChar}WindowsApps{Path.DirectorySeparatorChar}{CodexPackagePrefix}", StringComparison.OrdinalIgnoreCase)
        && string.Equals(Path.GetFileName(path), "codex.exe", StringComparison.OrdinalIgnoreCase)
        && path.Contains($"{Path.DirectorySeparatorChar}resources{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    public static bool IsLocalCodexExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !string.Equals(Path.GetFileName(path), "codex.exe", StringComparison.OrdinalIgnoreCase))
            return false;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        return IsPathInside(path, root);
    }

    public static bool IsTrustedCodexExecutable(string? path) =>
        IsPackagedCodexExecutable(path) || IsLocalCodexExecutable(path);
}
