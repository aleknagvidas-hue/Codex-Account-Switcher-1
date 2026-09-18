using System.Security.Cryptography;

namespace CodexAccountSwitcher.Services;

internal enum AuthWriteStage { Prepared, Replaced }

internal sealed class AuthRecoveryException : IOException
{
    public AuthRecoveryException(string backup, Exception inner)
        : base($"Recovery could not be verified. Keep Codex closed. Encrypted recovery backup (if one existed): {backup}", inner) { }
}

/// <summary>One replacement owns one encrypted backup. Recovery ignores all older backups.</summary>
internal sealed class AuthFileTransaction
{
    private readonly Action<AuthWriteStage>? _checkpoint;
    private readonly ICredentialProtector _protector;
    public AuthFileTransaction(Action<AuthWriteStage>? checkpoint = null, ICredentialProtector? protector = null)
    {
        _checkpoint = checkpoint;
        _protector = protector ?? new WindowsCredentialProtector();
    }

    public async Task ReplaceAsync(string activePath, byte[] target, CancellationToken token)
    {
        AuthIdentity.ValidateAndFingerprint(target);
        var directory = Path.GetDirectoryName(Path.GetFullPath(activePath))!;
        Directory.CreateDirectory(directory);
        var id = Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(directory, $"auth.switching.{id}.tmp");
        var backupDirectory = Path.Combine(directory, "switcher-backups");
        var backup = Path.Combine(backupDirectory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{id}.dpapi");
        byte[]? previous = File.Exists(activePath) ? await File.ReadAllBytesAsync(activePath, token) : null;
        var replaced = false;
        try
        {
            if (previous is not null)
            {
                AuthIdentity.ValidateAndFingerprint(previous);
                Directory.CreateDirectory(backupDirectory);
                var encryptedBackup = _protector.Protect(previous);
                try
                {
                    await WriteDurablyAsync(backup, encryptedBackup, token);
                }
                finally { CryptographicOperations.ZeroMemory(encryptedBackup); }
                await VerifyBackupAsync(backup, previous, token);
            }

            await WriteDurablyAsync(temporary, target, token);
            await ValidateAuthFileAsync(temporary, token);
            _checkpoint?.Invoke(AuthWriteStage.Prepared);
            token.ThrowIfCancellationRequested();
            // Refuse a race with another writer instead of replacing a sign-in we did not back up.
            var current = File.Exists(activePath) ? await File.ReadAllBytesAsync(activePath, token) : null;
            try
            {
                if ((previous is null) != (current is null) ||
                    (previous is not null && !previous.AsSpan().SequenceEqual(current)))
                    throw new IOException("The active sign-in changed during preparation. Try again with Codex closed.");
            }
            finally
            {
                if (current is not null) CryptographicOperations.ZeroMemory(current);
            }
            if (previous is null) File.Move(temporary, activePath);
            else File.Replace(temporary, activePath, null);
            replaced = true;
            _checkpoint?.Invoke(AuthWriteStage.Replaced);
            token.ThrowIfCancellationRequested();
            var actual = await File.ReadAllBytesAsync(activePath, token);
            try
            {
                if (!actual.AsSpan().SequenceEqual(target) ||
                    AuthIdentity.ValidateAndFingerprint(actual) != AuthIdentity.ValidateAndFingerprint(target))
                    throw new IOException("The replacement sign-in did not match the selected account.");
            }
            finally { CryptographicOperations.ZeroMemory(actual); }
        }
        catch
        {
            if (replaced)
            {
                try
                {
                // A later writer owns its changes; never overwrite them during recovery.
                var current = File.Exists(activePath) ? await File.ReadAllBytesAsync(activePath) : null;
                try
                {
                    if (current is null || !current.AsSpan().SequenceEqual(target))
                        throw new IOException("The active sign-in changed after replacement.");
                }
                finally { if (current is not null) CryptographicOperations.ZeroMemory(current); }
                if (previous is null) File.Delete(activePath);
                else
                {
                    var encryptedBackup = await File.ReadAllBytesAsync(backup, CancellationToken.None);
                    byte[] restored;
                    try { restored = _protector.Unprotect(encryptedBackup); }
                    finally { CryptographicOperations.ZeroMemory(encryptedBackup); }
                    try
                    {
                        await WriteDurablyAsync(temporary, restored, CancellationToken.None);
                        if (File.Exists(activePath)) File.Replace(temporary, activePath, null);
                        else File.Move(temporary, activePath);
                        var actual = File.ReadAllBytes(activePath);
                        try
                        {
                            if (!actual.AsSpan().SequenceEqual(previous))
                                throw new IOException("Recovery could not be verified. Keep Codex closed and use the encrypted backup.");
                        }
                        finally { CryptographicOperations.ZeroMemory(actual); }
                    }
                    finally { CryptographicOperations.ZeroMemory(restored); }
                }
                }
                catch (Exception recoveryError)
                {
                    throw new AuthRecoveryException(backup, recoveryError);
                }
            }
            throw;
        }
        finally
        {
            if (previous is not null) CryptographicOperations.ZeroMemory(previous);
            // Cleanup must not hide a recovery failure from the caller.
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task WriteDurablyAsync(string path, byte[] bytes, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
        stream.Flush(flushToDisk: true);
    }

    private async Task VerifyBackupAsync(string path, byte[] expected, CancellationToken token)
    {
        var encrypted = await File.ReadAllBytesAsync(path, token);
        byte[] roundTrip;
        try { roundTrip = _protector.Unprotect(encrypted); }
        finally { CryptographicOperations.ZeroMemory(encrypted); }
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(expected, roundTrip))
                throw new IOException("The recovery backup could not be verified. Nothing was replaced.");
        }
        finally { CryptographicOperations.ZeroMemory(roundTrip); }
    }

    private static async Task ValidateAuthFileAsync(string path, CancellationToken token)
    {
        var bytes = await File.ReadAllBytesAsync(path, token);
        try { AuthIdentity.ValidateAndFingerprint(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
