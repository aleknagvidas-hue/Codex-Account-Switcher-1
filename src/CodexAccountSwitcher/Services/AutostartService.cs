using Microsoft.Win32;
using System.Diagnostics;

namespace CodexAccountSwitcher.Services;

internal static class AutostartService
{
    private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "CodexAccountSwitcher";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception ex)
        {
            DiagnosticTrace.Write($"autostart status unavailable: {ex.GetType().Name}");
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        var executable = Process.GetCurrentProcess().MainModule?.FileName;
        if (enabled && (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))) return false;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return false;
            if (enabled) key.SetValue(ValueName, BuildCommand(executable!), RegistryValueKind.String);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            return IsEnabled() == enabled;
        }
        catch (Exception ex)
        {
            DiagnosticTrace.Write($"autostart change failed: {ex.GetType().Name}");
            return false;
        }
    }

    internal static string BuildCommand(string executable) => $"\"{executable}\" --background";
}
