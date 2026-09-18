using System.Security.Cryptography;
using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Services;

public sealed record SwitchResult(bool Relaunched, string? Warning);

public sealed class AuthSwitchService
{
    private readonly ProfileStore _store;
    private readonly ICodexProcessService _processService;
    private readonly string _activeAuthPath;
    private readonly AuthFileTransaction _transaction;
    private readonly ICredentialProtector _backupProtector;
    private readonly ICodexDesktopAuthLifecycle _authLifecycle;

    public AuthSwitchService(ProfileStore store, ICodexProcessService? processService = null, string? activeAuthPath = null)
        : this(store, processService, activeAuthPath, new WindowsCredentialProtector(), null) { }

    internal AuthSwitchService(
        ProfileStore store,
        ICodexProcessService? processService,
        string? activeAuthPath,
        ICredentialProtector backupProtector,
        AuthFileTransaction? transaction = null,
        ICodexDesktopAuthLifecycle? authLifecycle = null)
    {
        _store = store;
        _processService = processService ?? new CodexProcessService();
        _activeAuthPath = Path.GetFullPath(activeAuthPath ?? AppPaths.CurrentAuthPath);
        _backupProtector = backupProtector;
        _transaction = transaction ?? new AuthFileTransaction(protector: backupProtector);
        _authLifecycle = authLifecycle ?? new CodexDesktopAuthLifecycle();
    }

    public async Task<SwitchResult> SwitchAsync(AccountProfile target, CancellationToken cancellationToken = default)
    {
        var targetAuth = await _store.GetAuthAsync(target, cancellationToken);
        byte[]? departingAuth = null;
        try
        {
            var targetFingerprint = AuthIdentity.ValidateAndFingerprint(targetAuth);
            if (!string.Equals(targetFingerprint, target.Fingerprint, StringComparison.Ordinal))
                throw new InvalidDataException("The selected saved sign-in does not match its account profile.");

            DiagnosticTrace.Write("switch phase: validate saved login before desktop shutdown");
            var prepared = await _authLifecycle.PrepareAsync(targetAuth,
                latest => _store.UpdateAuthAsync(target, latest, CancellationToken.None), cancellationToken);
            CryptographicOperations.ZeroMemory(targetAuth);
            targetAuth = prepared;
            if (AuthIdentity.ValidateAndFingerprint(targetAuth) != targetFingerprint)
                throw new InvalidDataException("The validated sign-in belongs to a different account.");
            await _store.UpdateAuthAsync(target, targetAuth, CancellationToken.None);

            var configPath = Path.Combine(Path.GetDirectoryName(_activeAuthPath)!, "config.toml");
            await CodexCredentialStoreService.EnsureFileStoreAsync(configPath, cancellationToken);

            DiagnosticTrace.Write("switch phase: close desktop");
            var authWasReplaced = false;
            try
            {
                await _processService.CloseDesktopAsync(cancellationToken);
                departingAuth = await SaveDepartingAccountAsync(cancellationToken);
                await _processService.CloseDesktopAsync(cancellationToken);
                await ReplaceActiveAuthAsync(targetAuth, cancellationToken);
                authWasReplaced = true;
                DiagnosticTrace.Write("switch phase: verify installed login");
                await _authLifecycle.VerifyAsync(targetFingerprint, cancellationToken);

                var acceptedAuth = await File.ReadAllBytesAsync(_activeAuthPath, cancellationToken);
                try
                {
                    if (!string.Equals(AuthIdentity.ValidateAndFingerprint(acceptedAuth), targetFingerprint, StringComparison.Ordinal))
                        throw new InvalidOperationException("Codex accepted a different account than the one selected.");
                    await _store.UpdateAuthAsync(target, acceptedAuth, cancellationToken);
                }
                finally { CryptographicOperations.ZeroMemory(acceptedAuth); }
                DiagnosticTrace.Write("switch phase: launch desktop");
                await _processService.LaunchDesktopAsync(cancellationToken);
            }
            catch (AuthRecoveryException)
            {
                throw;
            }
            catch (Exception switchError)
            {
                DiagnosticTrace.Write($"switch failure: {switchError.GetType().Name}; reason={(switchError as CodexRequestException)?.Reason ?? "local_or_transport"}");
                if (authWasReplaced)
                {
                    try { await RecoverPreviousAccountAsync(departingAuth, targetFingerprint); }
                    catch (Exception recoveryError)
                    {
                        throw new AuthRecoveryException("previous Codex sign-in", recoveryError);
                    }
                }
                try { await _processService.LaunchDesktopAsync(CancellationToken.None); }
                catch { }
                if (authWasReplaced)
                    throw new InvalidOperationException(
                        "The switch did not complete. The previous sign-in was restored. " + switchError.Message,
                        switchError);
                throw;
            }

            target.LastUsedAt = DateTimeOffset.UtcNow;
            try
            {
                await _store.SaveProfileAsync(target, cancellationToken);
                return new SwitchResult(true, null);
            }
            catch (Exception ex)
            {
                return new SwitchResult(true, $"The switch completed, but the usage history could not be updated: {ex.Message}");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(targetAuth);
            if (departingAuth is not null) CryptographicOperations.ZeroMemory(departingAuth);
        }
    }

    private async Task<byte[]?> SaveDepartingAccountAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_activeAuthPath)) return null;
        var current = await File.ReadAllBytesAsync(_activeAuthPath, cancellationToken);
        try
        {
            string fingerprint;
            try { fingerprint = AuthIdentity.ValidateAndFingerprint(current); }
            catch { throw new InvalidDataException("The current Codex authentication file could not be validated, so the switch was canceled."); }

            var profile = (await _store.LoadAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Fingerprint, fingerprint, StringComparison.Ordinal));
            if (profile is not null) await _store.UpdateAuthAsync(profile, current, cancellationToken);
            return current;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(current);
            throw;
        }
    }

    private async Task RestoreDepartingAccountAsync(byte[] departingAuth)
    {
        var departingFingerprint = AuthIdentity.ValidateAndFingerprint(departingAuth);
        if (File.Exists(_activeAuthPath))
        {
            var current = await File.ReadAllBytesAsync(_activeAuthPath);
            try
            {
                if (string.Equals(AuthIdentity.ValidateAndFingerprint(current), departingFingerprint, StringComparison.Ordinal)) return;
            }
            catch { }
            finally { CryptographicOperations.ZeroMemory(current); }
        }

        await new AuthFileTransaction(protector: _backupProtector)
            .ReplaceAsync(_activeAuthPath, departingAuth, CancellationToken.None);
    }

    private async Task RecoverPreviousAccountAsync(byte[]? departingAuth, string targetFingerprint)
    {
        await _processService.CloseDesktopAsync(CancellationToken.None);
        if (departingAuth is not null)
        {
            await RestoreDepartingAccountAsync(departingAuth);
            return;
        }

        if (!File.Exists(_activeAuthPath)) return;
        var current = await File.ReadAllBytesAsync(_activeAuthPath);
        try
        {
            if (!string.Equals(AuthIdentity.ValidateAndFingerprint(current), targetFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("The active Codex authentication changed during recovery.");
        }
        finally { CryptographicOperations.ZeroMemory(current); }
        File.Delete(_activeAuthPath);
    }

    private Task ReplaceActiveAuthAsync(byte[] targetAuth, CancellationToken cancellationToken) =>
        _transaction.ReplaceAsync(_activeAuthPath, targetAuth, cancellationToken);
}
