using System.Text;
using System.Text.RegularExpressions;

namespace CodexAccountSwitcher.Services;

internal static partial class CodexCredentialStoreService
{
    private const string RequiredSetting = "cli_auth_credentials_store = \"file\"";

    public static async Task EnsureFileStoreAsync(string configPath, CancellationToken cancellationToken = default)
    {
        configPath = Path.GetFullPath(configPath);
        var directory = Path.GetDirectoryName(configPath)
            ?? throw new InvalidOperationException("The Codex configuration path has no parent directory.");
        Directory.CreateDirectory(directory);

        var existing = File.Exists(configPath)
            ? await File.ReadAllTextAsync(configPath, cancellationToken)
            : string.Empty;
        var updated = ForceFileStore(existing);
        if (string.Equals(existing, updated, StringComparison.Ordinal)) return;

        var temporary = Path.Combine(directory, $".{Path.GetFileName(configPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                updated,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            File.Move(temporary, configPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { }
        }
    }

    internal static string ForceFileStore(string config)
    {
        if (CredentialStoreLine().IsMatch(config))
            return CredentialStoreLine().Replace(config, RequiredSetting, 1);

        if (string.IsNullOrEmpty(config)) return RequiredSetting + Environment.NewLine;
        return RequiredSetting + Environment.NewLine + config;
    }

    [GeneratedRegex("^[ \\t]*cli_auth_credentials_store[ \\t]*=.*$", RegexOptions.Multiline)]
    private static partial Regex CredentialStoreLine();
}
