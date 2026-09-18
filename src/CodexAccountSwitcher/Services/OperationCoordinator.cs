namespace CodexAccountSwitcher.Services;

public sealed class OperationCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<bool> TryRunAsync(Func<CancellationToken, Task> action, CancellationToken token = default)
    {
        try { if (!await _gate.WaitAsync(0, token)) return false; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        try { await action(token); return true; }
        finally { _gate.Release(); }
    }
    public async Task RunAsync(Func<CancellationToken, Task> action, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try { await action(token); }
        finally { _gate.Release(); }
    }
}

public sealed class RefreshSchedule
{
    public int Failures { get; private set; }
    public DateTimeOffset NextAttempt { get; private set; }
    public void Complete(bool success, int intervalMinutes, DateTimeOffset now)
    {
        Failures = success ? 0 : Math.Min(Failures + 1, 5);
        NextAttempt = now.AddMinutes(Math.Min(60, intervalMinutes * Math.Pow(2, Failures)));
    }
    public bool IsDue(DateTimeOffset now) => now >= NextAttempt;
}
