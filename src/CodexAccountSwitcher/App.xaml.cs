using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace CodexAccountSwitcher;

public partial class App : System.Windows.Application
{
    private const string InstanceMutexName = "Local\\CodexAccountSwitcher.Windows";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Services.DiagnosticTrace.Write($"unhandled failure: {args.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, args) => { Services.DiagnosticTrace.Write($"unobserved task failure: {args.Exception}"); args.SetObserved(); };
        DispatcherUnhandledException += (_, args) =>
        {
            Services.DiagnosticTrace.Write($"dispatcher failure: {args.Exception}");
            MessageBox.Show(args.Exception.Message, "Codex Account Switcher could not continue", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            Shutdown(1);
        };
        Services.DiagnosticTrace.Write("startup entered");
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var renderIndex = Array.FindIndex(e.Args, value => string.Equals(value, "--render-demo", StringComparison.OrdinalIgnoreCase));
        var renderPath = renderIndex >= 0 && renderIndex + 1 < e.Args.Length ? e.Args[renderIndex + 1] : null;
        base.OnStartup(e);
        var demo = e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase) || renderPath is not null;
        var background = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        var selfCloseTest = e.Args.Contains("--self-close-test", StringComparer.OrdinalIgnoreCase);
        var enableAutostart = e.Args.Contains("--enable-autostart", StringComparer.OrdinalIgnoreCase);
        if (enableAutostart)
        {
            var enabled = Services.AutostartService.SetEnabled(true);
            Services.DiagnosticTrace.Write($"autostart command completed: enabled={enabled}");
            Shutdown(enabled ? 0 : 1);
            return;
        }
        if (!demo && !selfCloseTest)
        {
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\CodexAccountSwitcher.Show");
            _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
            if (!isFirstInstance)
            {
                Services.DiagnosticTrace.Write("second instance redirected to the existing window");
                _showSignal.Set();
                if (WaitForExistingWindowAndActivate())
                {
                    Shutdown();
                    return;
                }

                if (!TakeOverWindowlessInstance())
                {
                    Services.DiagnosticTrace.Write("existing instance recovery failed");
                    Shutdown(1);
                    return;
                }

                Services.DiagnosticTrace.Write("windowless instance replaced; starting a fresh panel");
            }
        }

        var window = new MainWindow(demo, background);
        MainWindow = window;
        if (_showSignal is not null)
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal,
                (_, _) => Dispatcher.BeginInvoke(window.ShowPanel), null, Timeout.Infinite, executeOnlyOnce: false);
        Services.DiagnosticTrace.Write("main window constructed");
        window.Show();
        if (!background) { window.WindowState = WindowState.Normal; window.Activate(); }
        Services.DiagnosticTrace.Write("main window shown");
        if (selfCloseTest)
        {
            window.ContentRendered += (_, _) => window.CloseForRendering();
        }
        if (renderPath is not null)
        {
            window.ContentRendered += (_, _) =>
            {
                window.RenderPreview(renderPath);
                window.CloseForRendering();
                Shutdown();
            };
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _instanceMutex?.ReleaseMutex(); }
        catch (ApplicationException) { }
        _instanceMutex?.Dispose();
        _showWait?.Unregister(null);
        _showSignal?.Dispose();
        base.OnExit(e);
    }

    private static bool WaitForExistingWindowAndActivate()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (ActivateExistingWindow()) return true;
            Thread.Sleep(100);
        }
        return false;
    }

    private bool TakeOverWindowlessInstance()
    {
        var stoppedAny = false;
        foreach (var process in Process.GetProcessesByName("CodexAccountSwitcher"))
        {
            try
            {
                if (process.Id == Environment.ProcessId || FindPanelWindow(process.Id) != IntPtr.Zero) continue;
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
                stoppedAny = true;
            }
            catch (Exception ex)
            {
                Services.DiagnosticTrace.Write($"windowless instance could not be stopped: {ex.GetType().Name}");
            }
            finally { process.Dispose(); }
        }

        if (!stoppedAny || _instanceMutex is null) return false;
        try { return _instanceMutex.WaitOne(3000); }
        catch (AbandonedMutexException) { return true; }
    }

    private static bool ActivateExistingWindow()
    {
        foreach (var process in Process.GetProcessesByName("CodexAccountSwitcher"))
        {
            try
            {
                if (process.Id == Environment.ProcessId) continue;
                var panelHandle = FindPanelWindow(process.Id);
                if (panelHandle == IntPtr.Zero) continue;
                ShowWindowAsync(panelHandle, 9); // SW_RESTORE
                BringWindowToTop(panelHandle);
                SetForegroundWindow(panelHandle);
                Services.DiagnosticTrace.Write("existing panel restored by window enumeration");
                return true;
            }
            catch { }
            finally { process.Dispose(); }
        }
        return false;
    }

    private static IntPtr FindPanelWindow(int processId)
    {
        // Process.MainWindowHandle ignores hidden windows and can resolve to the
        // notification-area helper. Find the titled WPF panel explicitly.
        IntPtr panelHandle = IntPtr.Zero;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var ownerProcessId);
            if (ownerProcessId != (uint)processId) return true;

            var titleLength = GetWindowTextLength(handle);
            if (titleLength <= 0) return true;
            var title = new StringBuilder(titleLength + 1);
            GetWindowText(handle, title, title.Capacity);
            if (!title.ToString().StartsWith("Codex Account Switcher", StringComparison.Ordinal)) return true;

            panelHandle = handle;
            return false;
        }, IntPtr.Zero);
        return panelHandle;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

}
