namespace CodexAccountSwitcher.Services;

internal static class DiagnosticTrace
{
    public static void Write(string message)
    {
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("CODEX_SWITCHER_DIAGNOSTIC_HOME"),
            Path.Combine(AppPaths.DataRoot, "logs"),
            AppContext.BaseDirectory
        }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            try
            {
                Directory.CreateDirectory(root!);
                File.AppendAllText(Path.Combine(root!, "startup.log"), $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
