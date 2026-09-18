using System.Text.Json;
using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Services;

public sealed class ProfileStore
{
    private readonly string _root;
    private readonly string _profilesDirectory;
    private readonly string _metadataPath;
    private readonly ICredentialProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public ProfileStore(string? root = null) : this(root, new WindowsCredentialProtector()) { }

    internal ProfileStore(string? root, ICredentialProtector protector)
    {
        _protector = protector;
        _root = Path.GetFullPath(root ?? AppPaths.DataRoot);
        _profilesDirectory = Path.Combine(_root, "profiles");
        _metadataPath = Path.Combine(_root, "profiles.json");
        Directory.CreateDirectory(_profilesDirectory);
    }

    public string Root => _root;

    public async Task<IReadOnlyList<AccountProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadUnlockedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<AccountProfile> AddAsync(
        string displayName, string colorHex, byte[] authJson, CancellationToken cancellationToken = default) =>
        SaveAccountAsync(displayName, colorHex, authJson, false, null, cancellationToken);

    public Task<AccountProfile> AddAsync(
        string displayName, string colorHex, byte[] authJson, string browserProfileKey, CancellationToken cancellationToken = default) =>
        SaveAccountAsync(displayName, colorHex, authJson, false, BrowserProfileService.ValidateProfileKey(browserProfileKey), cancellationToken);

    public Task<AccountProfile> SaveCurrentAsync(
        string displayName, string colorHex, byte[] authJson, CancellationToken cancellationToken = default) =>
        SaveAccountAsync(displayName, colorHex, authJson, true, null, cancellationToken);

    private async Task<AccountProfile> SaveAccountAsync(
        string displayName,
        string colorHex,
        byte[] authJson,
        bool renewExisting,
        string? browserProfileKey,
        CancellationToken cancellationToken = default)
    {
        var alias = ValidateAlias(displayName);
        var fingerprint = AuthIdentity.ValidateAndFingerprint(authJson);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profiles = (await LoadUnlockedAsync(cancellationToken)).ToList();
            var existing = profiles.FirstOrDefault(profile => string.Equals(profile.Fingerprint, fingerprint, StringComparison.Ordinal));
            if (existing is not null)
            {
                if (renewExisting)
                {
                    await WriteEncryptedAuthAsync(existing.Id, authJson, cancellationToken);
                    if (existing.BrowserProfileKey is null && browserProfileKey is not null)
                    {
                        existing.BrowserProfileKey = browserProfileKey;
                        await SaveMetadataUnlockedAsync(profiles, cancellationToken);
                    }
                    return existing;
                }
                throw new InvalidOperationException("This account is already saved.");
            }
            var profile = new AccountProfile
            {
                Id = Guid.NewGuid().ToString("N"),
                DisplayName = alias,
                ColorHex = ValidateColor(colorHex),
                Fingerprint = fingerprint,
                BrowserProfileKey = browserProfileKey,
                CreatedAt = DateTimeOffset.UtcNow
            };

            await WriteEncryptedAuthAsync(profile.Id, authJson, cancellationToken);
            profiles.Add(profile);
            await SaveMetadataUnlockedAsync(profiles, cancellationToken);
            return profile;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<byte[]> GetAuthAsync(AccountProfile profile, CancellationToken cancellationToken = default)
    {
        var path = AuthPath(profile.Id);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The saved credential data was not found.", path);
        }

        var encrypted = await File.ReadAllBytesAsync(path, cancellationToken);
        try
        {
            var auth = _protector.Unprotect(encrypted);
            try
            {
                var fingerprint = AuthIdentity.ValidateAndFingerprint(auth);
                if (!string.Equals(fingerprint, profile.Fingerprint, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The saved credential data does not match this account.");
                }

                return auth;
            }
            catch
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(auth);
                throw;
            }
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    public async Task UpdateAuthAsync(AccountProfile profile, byte[] authJson, CancellationToken cancellationToken = default)
    {
        var fingerprint = AuthIdentity.ValidateAndFingerprint(authJson);
        if (!string.Equals(fingerprint, profile.Fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Credential data from a different account cannot overwrite this profile.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WriteEncryptedAuthAsync(profile.Id, authJson, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveProfileAsync(AccountProfile profile, CancellationToken cancellationToken = default)
    {
        profile.DisplayName = ValidateAlias(profile.DisplayName);
        profile.ColorHex = ValidateColor(profile.ColorHex);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profiles = (await LoadUnlockedAsync(cancellationToken)).ToList();
            var index = profiles.FindIndex(item => item.Id == profile.Id);
            if (index < 0)
            {
                throw new InvalidOperationException("The account to update was not found.");
            }

            profiles[index] = profile;
            await SaveMetadataUnlockedAsync(profiles, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(AccountProfile profile, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profiles = (await LoadUnlockedAsync(cancellationToken)).Where(item => item.Id != profile.Id).ToList();
            await SaveMetadataUnlockedAsync(profiles, cancellationToken);
            var authPath = AuthPath(profile.Id);
            if (File.Exists(authPath))
            {
                File.Delete(authPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<AccountProfile>> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_metadataPath))
        {
            return Array.Empty<AccountProfile>();
        }

        await using var stream = File.OpenRead(_metadataPath);
        return await JsonSerializer.DeserializeAsync<List<AccountProfile>>(stream, _jsonOptions, cancellationToken)
            ?? new List<AccountProfile>();
    }

    private async Task SaveMetadataUnlockedAsync(IReadOnlyList<AccountProfile> profiles, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(profiles, _jsonOptions);
        await AtomicWriteAsync(_metadataPath, bytes, cancellationToken);
    }

    private async Task WriteEncryptedAuthAsync(string id, byte[] authJson, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_profilesDirectory);
        var encrypted = _protector.Protect(authJson);
        try
        {
            await AtomicWriteAsync(AuthPath(id), encrypted, cancellationToken);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    private static async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private string AuthPath(string id) => Path.Combine(_profilesDirectory, $"{id}.auth.dpapi");

    private static string ValidateAlias(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length is < 1 or > 40)
        {
            throw new ArgumentException("The display name must contain between 1 and 40 characters.");
        }

        return trimmed;
    }

    private static string ValidateColor(string value)
    {
        if (value.Length == 7 && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit))
        {
            return value.ToUpperInvariant();
        }

        return "#7C8CFF";
    }
}
