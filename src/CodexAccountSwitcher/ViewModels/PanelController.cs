using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CodexAccountSwitcher.Models;
using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.ViewModels;

public sealed class PanelController : INotifyPropertyChanged
{
    private readonly ProfileStore _store;
    private readonly CodexAppServerClient _server = new();
    private readonly OperationCoordinator _operations = new();
    private readonly Dictionary<string, RefreshSchedule> _schedules = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _activeOperation;
    private readonly PanelSettings _settings;
    private bool _busy;
    private bool _protocolVerified;
    private string? _confirmedActiveFingerprint;
    private string _status = "Ready. Finish running tasks before switching.";
    private string? _lastError;
    public PanelController(bool demo = false)
    {
        IsDemo = demo;
        _store = demo
            ? new ProfileStore(Environment.GetEnvironmentVariable("CODEX_SWITCHER_DEMO_HOME")
                ?? Path.Combine(Path.GetTempPath(), "CodexAccountSwitcherDemo"))
            : new ProfileStore();
        _settings = demo ? new() : PanelSettings.Load(AppPaths.DataRoot);
        if (demo) SeedPreview();
    }
    public bool IsDemo { get; }
    public ObservableCollection<AccountProfileViewModel> Accounts { get; } = new();
    public ObservableCollection<AccountProfileViewModel> PanelAccounts { get; } = new();
    public bool IsEmpty => Accounts.Count == 0;
    public string Summary => IsDemo ? "Preview • synthetic accounts" : $"{Accounts.Count} saved accounts • no account limit • credentials stay on this PC";
    public string Status { get => _status; private set { _status = value; Notify(); } }
    public bool Busy { get => _busy; private set { _busy = value; Notify(); Notify(nameof(CanInteract)); } }
    public bool CanInteract => !Busy;
    public string? LastError { get => _lastError; private set { _lastError = value; Notify(); Notify(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(LastError);
    public bool BackgroundRefresh
    {
        get => _settings.BackgroundRefresh;
        set { _settings.BackgroundRefresh = value; SaveSettings(); Notify(); Notify(nameof(RefreshDescription)); }
    }
    public int RefreshMinutes
    {
        get => _settings.RefreshMinutes;
        set { _settings.RefreshMinutes = new[] { 2, 5, 10, 15 }.Contains(value) ? value : 5; SaveSettings(); Notify(); Notify(nameof(RefreshDescription)); }
    }
    public int[] RefreshIntervals { get; } = { 2, 5, 10, 15 };
    public string RefreshDescription => !BackgroundRefresh ? "Auto-refresh off" :
        !_protocolVerified && !IsDemo ? "Usage check starts when you press Refresh" : $"Auto-refresh every {RefreshMinutes} min • read-only tokens";
    private void SaveSettings() { if (!IsDemo) _settings.Save(AppPaths.DataRoot); }
    public void SetStatus(string message) => Status = message;

    public async Task InitializeAsync()
    {
        if (IsDemo) return;
        await ExecuteAsync("Loading saved accounts…", async token =>
        {
            await ReloadAsync(token);
            await ConfirmCurrentActiveAsync(token);
            Status = "Ready. Select an account to switch, or press Refresh to check usage.";
            Notify(nameof(RefreshDescription));
        });
    }

    public async Task TickAsync()
    {
        foreach (var account in Accounts) { account.FreshForMinutes = RefreshMinutes * 2; account.RefreshBindings(); }
        if (BackgroundRefresh && !Busy && _protocolVerified && !IsDemo) await RefreshAsync(false);
    }

    public async Task RefreshAsync(bool force)
    {
        if (Busy || IsDemo) return;
        if (!force && !Accounts.Any(a => !_schedules.TryGetValue(a.Profile.Id, out var schedule) || schedule.IsDue(DateTimeOffset.UtcNow))) return;
        await ExecuteAsync("Refreshing usage…", async token =>
        {
            if (!_protocolVerified)
            {
                await _server.ProbeExternalTokenModeAsync(token);
                _protocolVerified = true;
                Notify(nameof(RefreshDescription));
            }
            MarkActive();
            var checkedCount = 0;
            foreach (var account in Accounts)
            {
                if (!_schedules.TryGetValue(account.Profile.Id, out var schedule))
                    _schedules[account.Profile.Id] = schedule = new RefreshSchedule();
                if (!force && !schedule.IsDue(DateTimeOffset.UtcNow)) continue;
                token.ThrowIfCancellationRequested();
                checkedCount++;
                account.IsRefreshing = true;
                var success = false;
                try
                {
                    byte[] auth;
                    if (account.IsActive && File.Exists(AppPaths.CurrentAuthPath))
                    {
                        auth = await File.ReadAllBytesAsync(AppPaths.CurrentAuthPath, token);
                        if (AuthIdentity.ValidateAndFingerprint(auth) != account.Profile.Fingerprint)
                            throw new InvalidOperationException("The active account changed. Refresh again.");
                        // Capture a renewed active sign-in locally; never write back to Codex during polling.
                        await _store.UpdateAuthAsync(account.Profile, auth, token);
                    }
                    else auth = await _store.GetAuthAsync(account.Profile, token);
                    try
                    {
                        var result = await _server.ReadUsageAsync(auth, token);
                        success = result.Snapshot.Status == "available";
                        if (success)
                        {
                            result.Snapshot.PlanType ??= account.Profile.Usage?.PlanType;
                            account.Profile.Usage = result.Snapshot;
                        }
                        else MarkUnavailable(account, "Usage windows are unavailable.");
                    }
                    finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(auth); }
                }
                catch (OperationCanceledException) { throw; }
                catch { MarkUnavailable(account, "Refresh failed. The sign-in may need renewal; retry later."); }
                finally
                {
                    account.IsRefreshing = false;
                    account.RefreshBindings();
                    schedule.Complete(success, RefreshMinutes, DateTimeOffset.UtcNow);
                }
                await _store.SaveProfileAsync(account.Profile, token);
            }
            if (checkedCount > 0) Status = "Refresh complete. Stale and unavailable readings are labelled; failed accounts will retry with backoff.";
        }, clearError: force);
    }

    internal static void MarkUnavailable(AccountProfileViewModel account, string message)
    {
        // Preserve the previous successful reading and its timestamp; it must remain visibly stale.
        account.Profile.Usage ??= new UsageSnapshot { CheckedAt = DateTimeOffset.MinValue };
        account.Profile.Usage.Status = "stale";
        account.Profile.Usage.Message = message;
    }

    public Task SaveCurrentAsync(string alias, string color) => ExecuteAsync("Saving current account…", async token =>
    {
        EnsureReal();
        var auth = await File.ReadAllBytesAsync(AppPaths.CurrentAuthPath, token);
        try { await _store.SaveCurrentAsync(alias, color, auth, token); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(auth); }
        await ReloadAsync(token);
        Status = "Current sign-in saved or renewed locally. Existing labels are preserved. Use Refresh to load usage.";
    });
    public Task LoginAsync(string alias, string color) => ExecuteAsync("Complete sign-in in your browser…", async token =>
    {
        EnsureReal();
        var browserProfileKey = Guid.NewGuid().ToString("N");
        var login = await _server.LoginAsync(browserProfileKey, token);
        try
        {
            var profile = await _store.AddAsync(alias, color, login.AuthJson, browserProfileKey, token);
            profile.Usage = new UsageSnapshot { PlanType = login.PlanType, CheckedAt = DateTimeOffset.MinValue };
            await _store.SaveProfileAsync(profile, token);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(login.AuthJson); }
        await ReloadAsync(token);
        Status = "Account added with its own private website session. Your active Codex account was not changed.";
    });
    public Task SwitchAsync(AccountProfileViewModel account) => ExecuteAsync("Checking selected sign-in…", async token =>
    {
        EnsureReal();
        MarkActive();
        if (account.IsActive) { Status = "This account is already active."; return; }
        await SwitchCoreAsync(account, token);
    });
    private async Task SwitchCoreAsync(AccountProfileViewModel account, CancellationToken token)
    {
        try
        {
            Status = $"Checking {account.DisplayName} before closing Codex…";
            SwitchResult result;
            try { result = await new AuthSwitchService(_store).SwitchAsync(account.Profile, token); }
            catch (CodexRequestException ex) when (ex.RequiresLogin)
            {
                await ReconnectCoreAsync(account, token);
                Status = $"Sign-in renewed. Switching to {account.DisplayName}…";
                result = await new AuthSwitchService(_store).SwitchAsync(account.Profile, token);
            }
            _confirmedActiveFingerprint = result.Relaunched ? account.Profile.Fingerprint : null;
            await ReloadAsync(token);
            Status = result.Warning ?? $"Switched to {account.DisplayName}. Codex has restarted.";
        }
        catch
        {
            _confirmedActiveFingerprint = null;
            MarkActive();
            throw;
        }
    }
    public Task ReconnectAsync(AccountProfileViewModel account) => ExecuteAsync("Renewing selected sign-in…", async token =>
    {
        EnsureReal();
        await ReconnectCoreAsync(account, token);
        await SwitchCoreAsync(account, token);
    });
    private async Task ReconnectCoreAsync(AccountProfileViewModel account, CancellationToken token)
    {
        Status = $"Sign in to {account.DisplayName} in the browser. Switching continues automatically; Codex stays open until then.";
        var key = account.Profile.BrowserProfileKey ?? Guid.NewGuid().ToString("N");
        var login = await _server.LoginAsync(key, token);
        try
        {
            // UpdateAuth refuses a different identity, even if the browser defaults to another account.
            await _store.UpdateAuthAsync(account.Profile, login.AuthJson, CancellationToken.None);
            account.Profile.BrowserProfileKey = key;
            await _store.SaveProfileAsync(account.Profile, CancellationToken.None);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(login.AuthJson); }
    }
    public Task RenameAsync(AccountProfileViewModel account, string alias, string color) => ExecuteAsync("Saving label…", async token =>
    {
        EnsureReal();
        account.Profile.DisplayName = alias; account.Profile.ColorHex = color;
        await _store.SaveProfileAsync(account.Profile, token);
        account.RefreshBindings();
        Status = "Label saved.";
    });
    public Task DeleteAsync(AccountProfileViewModel account) => ExecuteAsync("Removing local profile…", async token =>
    {
        EnsureReal(); MarkActive();
        if (account.IsActive) throw new InvalidOperationException("Switch away from the active account before deleting its saved profile.");
        await _store.DeleteAsync(account.Profile, token);
        await ReloadAsync(token);
        Status = "Local profile removed. The OpenAI account was not deleted.";
    });
    private async Task ReloadAsync(CancellationToken token)
    {
        var profiles = await _store.LoadAsync(token);
        Accounts.Clear(); PanelAccounts.Clear();
        foreach (var profile in profiles.OrderBy(x => x.CreatedAt))
        {
            var account = new AccountProfileViewModel(profile) { FreshForMinutes = RefreshMinutes * 2 };
            Accounts.Add(account);
            PanelAccounts.Add(account);
        }
        MarkActive(); Notify(nameof(IsEmpty)); Notify(nameof(Summary));
    }
    private void MarkActive()
    {
        foreach (var account in Accounts)
            account.IsActive = string.Equals(account.Profile.Fingerprint, _confirmedActiveFingerprint, StringComparison.Ordinal);
    }
    private async Task ConfirmCurrentActiveAsync(CancellationToken token)
    {
        _confirmedActiveFingerprint = null;
        try
        {
            if (!File.Exists(AppPaths.CurrentAuthPath)) { MarkActive(); return; }
            var auth = await File.ReadAllBytesAsync(AppPaths.CurrentAuthPath, token);
            string fingerprint;
            try { fingerprint = AuthIdentity.ValidateAndFingerprint(auth); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(auth); }
            await _server.VerifyDesktopAccountAsync(fingerprint, token);
            _confirmedActiveFingerprint = fingerprint;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            DiagnosticTrace.Write($"active account could not be confirmed: {ex.GetType().Name}: {ex.Message}");
        }
        MarkActive();
    }
    private void EnsureReal() { if (IsDemo) throw new InvalidOperationException("Preview only. No real sign-ins are read or changed in demo mode."); }
    private async Task ExecuteAsync(string message, Func<CancellationToken, Task> action, bool clearError = true)
    {
        DiagnosticTrace.Write($"operation started: {message}");
        if (_lifetime.IsCancellationRequested) return;
        await _operations.TryRunAsync(async lifetimeToken =>
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            _activeOperation = operation;
            var token = operation.Token;
            if (clearError) LastError = null;
            string? error = null;
            Busy = true; Status = message;
            try { await action(token); }
            catch (OperationCanceledException)
            {
                DiagnosticTrace.Write($"operation canceled: {message}");
                Status = "Operation canceled. Any completed authentication replacement was recovered.";
            }
            catch (Exception ex)
            {
                DiagnosticTrace.Write($"operation failed: {message} | {ex.GetType().Name}: {ex.Message}");
                Status = ex is InvalidOperationException or IOException ? ex.Message : "The operation could not be completed. Your account data was not logged.";
                LastError = Status;
                error = Status;
            }
            finally
            {
                Busy = false;
                _activeOperation = null;
                DiagnosticTrace.Write($"operation finished: {message} | status={Status}");
            }
            if (error is not null) ErrorRaised?.Invoke(error);
        }, _lifetime.Token);
    }
    public void CancelCurrentOperation() => _activeOperation?.Cancel();
    public async Task StopAsync()
    {
        _lifetime.Cancel();
        await _operations.RunAsync(_ => Task.CompletedTask);
    }
    public void RequestStop() => _lifetime.Cancel();
    private void SeedPreview()
    {
        var names = new[] { "Primary", "Work", "Personal", "Research account with a longer label" };
        var colors = new[] { "#A89BFF", "#62D5B4", "#F1AE80", "#88B9F2" };
        for (var i = 0; i < 4; i++)
        {
            var vm = new AccountProfileViewModel(new AccountProfile
            {
                DisplayName = names[i], ColorHex = colors[i],
                Usage = new UsageSnapshot
                {
                    Status = i == 2 ? "stale" : "available", PlanType = "plus",
                    CheckedAt = DateTimeOffset.UtcNow.AddMinutes(i == 2 ? -18 : -1),
                    ShortTerm = new UsageWindow { RemainingPercent = 82 - i * 19, WindowDurationMinutes = 300, ResetsAt = DateTimeOffset.UtcNow.AddHours(i + 1) },
                    Weekly = i == 3 ? null : new UsageWindow { RemainingPercent = 64 - i * 21, WindowDurationMinutes = 10080, ResetsAt = DateTimeOffset.UtcNow.AddDays(2 + i) }
                }
            }) { IsActive = i == 0 };
            Accounts.Add(vm); PanelAccounts.Add(vm);
        }
        Status = "Preview only. No real accounts, network requests, or switching.";
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<string>? ErrorRaised;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
