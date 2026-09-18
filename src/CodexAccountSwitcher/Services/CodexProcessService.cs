using System.Diagnostics;

namespace CodexAccountSwitcher.Services;

public interface ICodexProcessService
{
    Task CloseDesktopAsync(CancellationToken cancellationToken = default);
    Task LaunchDesktopAsync(CancellationToken cancellationToken = default);
}

internal interface ICodexProcessRuntime
{
    DateTimeOffset UtcNow { get; }
    IReadOnlyList<ICodexProcessHandle> GetDesktopProcesses();
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
    void LaunchDesktop();
}

internal interface ICodexProcessHandle : IDisposable
{
    int Id { get; }
    string ProcessName { get; }
    string? ExecutablePath { get; }
    IntPtr MainWindowHandle { get; }
    bool HasExited { get; }
    bool CloseMainWindow();
    void KillProcessTree();
}

public sealed class CodexProcessService : ICodexProcessService
{
    private const int RequiredConsecutiveEmptyScans = 3;
    private readonly ICodexProcessRuntime _runtime;
    private readonly TimeSpan _gracefulCloseTimeout;
    private readonly TimeSpan _forcedCloseTimeout;
    private readonly TimeSpan _quiescenceTimeout;
    private readonly TimeSpan _pollInterval;

    public CodexProcessService()
        : this(new SystemCodexProcessRuntime())
    {
    }

    internal CodexProcessService(
        ICodexProcessRuntime runtime,
        TimeSpan? gracefulCloseTimeout = null,
        TimeSpan? forcedCloseTimeout = null,
        TimeSpan? quiescenceTimeout = null,
        TimeSpan? pollInterval = null)
    {
        _runtime = runtime;
        _gracefulCloseTimeout = gracefulCloseTimeout ?? TimeSpan.FromSeconds(10);
        _forcedCloseTimeout = forcedCloseTimeout ?? TimeSpan.FromSeconds(5);
        _quiescenceTimeout = quiescenceTimeout ?? TimeSpan.FromSeconds(6);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(200);
    }

    public async Task CloseDesktopAsync(CancellationToken cancellationToken = default)
    {
        var initial = FindCodexDesktopProcesses();
        DiagnosticTrace.Write($"desktop shutdown found {initial.Processes.Count} trusted GUI/App Server process(es)");
        if (initial.HasUnverifiedActiveProcess)
        {
            DisposeAll(initial.Processes);
            throw new InvalidOperationException("A running ChatGPT process could not be identified safely, so the switch was canceled.");
        }

        var initialWasEmpty = initial.Processes.Count == 0;
        try
        {
                if (!initialWasEmpty)
                {
                    foreach (var process in initial.Processes)
                    {
                        TryCloseMainWindow(process);
                    }

                    var closedGracefully = await WaitUntilAsync(
                        () => initial.Processes.All(HasDefinitelyExited),
                        _gracefulCloseTimeout,
                        cancellationToken);

                    // The Store app often leaves a windowless renderer/helper alive after
                    // the visible window closes. It is part of the packaged Codex process
                    // tree, so terminate only this verified tree before touching auth.json.
                    if (!closedGracefully)
                    {
                        foreach (var process in initial.Processes)
                            KillIfRunning(process);

                        var closed = await WaitUntilAsync(
                            () => initial.Processes.All(HasDefinitelyExited),
                            _forcedCloseTimeout,
                            cancellationToken);
                        if (!closed)
                            throw new InvalidOperationException("Codex could not be closed. The authentication file was not changed.");
                    }
            }
        }
        finally
        {
            DisposeAll(initial.Processes);
        }

        var quiescence = await StopLateArrivalsUntilQuiescentAsync(initialWasEmpty, cancellationToken);
        if (!quiescence.IsQuiescent)
        {
            if (quiescence.HasUnverifiedActiveProcess)
            {
                throw new InvalidOperationException("An unidentified ChatGPT process remained during shutdown verification. The authentication file was not changed.");
            }

            throw new InvalidOperationException("Codex kept restarting during shutdown. The authentication file was not changed. Wait a few seconds and try again.");
        }

        DiagnosticTrace.Write("desktop GUI and App Server shutdown is quiescent");
    }

    public async Task LaunchDesktopAsync(CancellationToken cancellationToken = default)
    {
        _runtime.LaunchDesktop();

        var started = await WaitUntilAsync(
            () =>
            {
                var scan = FindCodexDesktopProcesses();
                try
                {
                    return scan.Processes.Count > 0;
                }
                finally
                {
                    DisposeAll(scan.Processes);
                }
            },
            TimeSpan.FromSeconds(15),
            cancellationToken);

        if (!started)
        {
            throw new InvalidOperationException("Codex did not restart automatically. Open it from the Start menu.");
        }
    }

    public static IReadOnlyList<int> DetectRunningDesktopProcessIds()
    {
        var service = new CodexProcessService();
        var scan = service.FindCodexDesktopProcesses();
        try
        {
            return scan.Processes.Select(process => process.Id).ToArray();
        }
        finally
        {
            DisposeAll(scan.Processes);
        }
    }

    private async Task<QuiescenceResult> StopLateArrivalsUntilQuiescentAsync(
        bool initialWasEmpty,
        CancellationToken cancellationToken)
    {
        var deadline = _runtime.UtcNow + _quiescenceTimeout;
        var consecutiveEmptyScans = initialWasEmpty ? 1 : 0;
        var sawUnverifiedActiveProcess = false;

        while (_runtime.UtcNow <= deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scan = FindCodexDesktopProcesses();
            try
            {
                if (scan.HasUnverifiedActiveProcess)
                {
                    sawUnverifiedActiveProcess = true;
                    consecutiveEmptyScans = 0;
                }
                else if (scan.Processes.Count == 0)
                {
                    consecutiveEmptyScans++;
                    if (consecutiveEmptyScans >= RequiredConsecutiveEmptyScans)
                    {
                        return new QuiescenceResult(true, false);
                    }
                }
                else
                {
                    sawUnverifiedActiveProcess = false;
                    consecutiveEmptyScans = 0;
                    foreach (var process in scan.Processes)
                    {
                        TryCloseMainWindow(process);
                        KillIfRunning(process);
                    }
                }
            }
            finally
            {
                DisposeAll(scan.Processes);
            }

            await _runtime.DelayAsync(_pollInterval, cancellationToken);
        }

        return new QuiescenceResult(false, sawUnverifiedActiveProcess);
    }

    private ProcessScan FindCodexDesktopProcesses()
    {
        var result = new List<ICodexProcessHandle>();
        var unverified = false;
        foreach (var process in _runtime.GetDesktopProcesses())
        {
            var keep = false;
            try
            {
                var executablePath = process.ExecutablePath;
                if (string.IsNullOrWhiteSpace(executablePath)
                    && !IsKnownChatGptCompanion(process)
                    && !HasDefinitelyExited(process))
                {
                    unverified = true;
                }
                if (AppPaths.IsPackagedCodexGui(executablePath)
                    || AppPaths.IsTrustedCodexExecutable(executablePath))
                {
                    result.Add(process);
                    keep = true;
                }
            }
            catch
            {
                // A child can disappear between enumeration and path lookup during shutdown.
                // Only a still-running process whose path cannot be proven remains ambiguous.
                if (!IsKnownChatGptCompanion(process) && !HasDefinitelyExited(process))
                {
                    unverified = true;
                }
            }
            finally
            {
                if (!keep)
                {
                    process.Dispose();
                }
            }
        }

        return new ProcessScan(result, unverified);
    }

    private static bool IsKnownChatGptCompanion(ICodexProcessHandle process) =>
        string.Equals(process.ProcessName, "ChatGPT", StringComparison.OrdinalIgnoreCase);

    private static bool IsWindowed(ICodexProcessHandle process)
    {
        try { return process.MainWindowHandle != IntPtr.Zero; }
        catch { return false; }
    }

    private static void TryCloseMainWindow(ICodexProcessHandle process)
    {
        try
        {
            if (!HasDefinitelyExited(process) && process.MainWindowHandle != IntPtr.Zero)
            {
                process.CloseMainWindow();
            }
        }
        catch when (HasDefinitelyExited(process))
        {
            // The process completed between the state check and the close request.
        }
    }

    private static void KillIfRunning(ICodexProcessHandle process)
    {
        try
        {
            if (!HasDefinitelyExited(process))
                process.KillProcessTree();
        }
        catch when (HasDefinitelyExited(process))
        {
            // The process completed between the state check and the kill request.
        }
    }

    private static bool HasDefinitelyExited(ICodexProcessHandle process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch
        {
            // Unknown access failures must not be interpreted as a confirmed exit.
            return false;
        }
    }

    private async Task<bool> WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = _runtime.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (predicate())
            {
                return true;
            }

            if (_runtime.UtcNow >= deadline)
            {
                return false;
            }

            await _runtime.DelayAsync(_pollInterval, cancellationToken);
        }
    }

    private static void DisposeAll(IEnumerable<ICodexProcessHandle> processes)
    {
        foreach (var process in processes)
        {
            process.Dispose();
        }
    }

    private sealed record ProcessScan(List<ICodexProcessHandle> Processes, bool HasUnverifiedActiveProcess);
    private sealed record QuiescenceResult(bool IsQuiescent, bool HasUnverifiedActiveProcess);
}

internal sealed class SystemCodexProcessRuntime : ICodexProcessRuntime
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public IReadOnlyList<ICodexProcessHandle> GetDesktopProcesses() =>
        Process.GetProcessesByName("ChatGPT")
            .Concat(Process.GetProcessesByName("codex"))
            .Select(process => (ICodexProcessHandle)new SystemCodexProcessHandle(process))
            .ToArray();

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);

    public void LaunchDesktop()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"shell:AppsFolder\\{AppPaths.CodexAppUserModelId}",
            UseShellExecute = true
        });
    }
}

internal sealed class SystemCodexProcessHandle(Process process) : ICodexProcessHandle
{
    public int Id => process.Id;
    public string ProcessName => process.ProcessName;
    public string? ExecutablePath => process.MainModule?.FileName;
    public IntPtr MainWindowHandle => process.MainWindowHandle;
    public bool HasExited => process.HasExited;
    public bool CloseMainWindow() => process.CloseMainWindow();
    public void KillProcessTree() => process.Kill(entireProcessTree: true);
    public void Dispose() => process.Dispose();
}
