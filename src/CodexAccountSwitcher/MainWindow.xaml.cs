using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexAccountSwitcher.ViewModels;

namespace CodexAccountSwitcher;

public partial class MainWindow : Window
{
    private readonly PanelController _controller;
    private readonly DispatcherTimer _timer;
    private bool _exiting;
    private bool _initialized;
    private bool _autostartReady;
    private readonly bool _demo;
    private readonly Services.TrayIconService? _tray;
    private bool _allowClose;
    private bool _switchPromptOpen;

    public MainWindow(bool demo = false, bool startHidden = false)
    {
        _demo = demo;
        Services.DiagnosticTrace.Write($"window constructor demo={demo}");
        InitializeComponent();
        Title = "Codex Account Switcher " + (typeof(MainWindow).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "");
        Services.DiagnosticTrace.Write("xaml initialized");
        _controller = new PanelController(demo);
        _controller.ErrorRaised += message => _tray?.ShowError(message);
        DataContext = _controller;
        SourceInitialized += (_, _) => Services.WindowBackdrop.Apply(this);
        StartWithWindowsCheckBox.IsEnabled = !demo;
        var autostartEnabled = !demo && Services.AutostartService.IsEnabled();
        StartWithWindowsCheckBox.IsChecked = autostartEnabled;
        if (autostartEnabled) Services.AutostartService.SetEnabled(true); // Repair an older build path.
        _autostartReady = true;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += async (_, _) => await _controller.TickAsync();
        if (!demo)
        {
            try
            {
                _tray = new Services.TrayIconService(
                    () => Dispatcher.Invoke(ShowPanel),
                    () => Dispatcher.InvokeAsync(async () => await _controller.RefreshAsync(true)),
                    () => Dispatcher.Invoke(ExitApplication));
                _controller.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(PanelController.Busy)) _tray.SetBusy(_controller.Busy);
                };
            }
            catch (Exception ex) { Services.DiagnosticTrace.Write($"tray initialization failed: {ex.GetType().Name}"); }
        }
        Loaded += async (_, _) =>
        {
            if (_initialized) return;
            _initialized = true;
            Services.DiagnosticTrace.Write("window loaded");
            await _controller.InitializeAsync();
            _timer.Start();
            Services.DiagnosticTrace.Write("window ready");
        };
        var initialRender = true;
        ContentRendered += (_, _) =>
        {
            if (!initialRender) return;
            initialRender = false;
            if (startHidden && _tray is not null) Hide();
        };
        Closed += (_, _) => { _tray?.Dispose(); Application.Current.Shutdown(); };
    }

    internal void ShowPanel()
    {
        Services.DiagnosticTrace.Write("show panel requested");
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
        Services.DiagnosticTrace.Write("panel shown and activated");
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _controller.RefreshAsync(true);
    private void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (_autostartReady) SetAutostart(StartWithWindowsCheckBox.IsChecked == true);
    }

    private void SetAutostart(bool enabled)
    {
        if (_demo || !_autostartReady) return;
        _autostartReady = false;
        var saved = Services.AutostartService.SetEnabled(enabled);
        var actual = Services.AutostartService.IsEnabled();
        StartWithWindowsCheckBox.IsChecked = actual;
        _controller.SetStatus(saved
            ? actual ? "The switcher will start with Windows." : "Windows startup is off."
            : "Windows startup could not be changed.");
        _autostartReady = true;
    }

    private async void SaveCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.Busy) return;
        var dialog = new AccountDialog("Save current account") { Owner = this };
        if (dialog.ShowDialog() == true) await _controller.SaveCurrentAsync(dialog.Alias, dialog.SelectedColor);
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.Busy) return;
        var dialog = new AccountDialog("Add another account") { Owner = this };
        if (dialog.ShowDialog() == true) await _controller.LoginAsync(dialog.Alias, dialog.SelectedColor);
    }

    private async void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.Busy || _switchPromptOpen || _exiting) return;
        if (sender is not Button { Tag: AccountProfileViewModel account }) return;
        if (account.IsActive) return;
        _switchPromptOpen = true;
        try
        {
        var confirm = new ConfirmDialog(
            $"Switch to \"{account.DisplayName}\"?",
            "Finish running Codex tasks first. The target login is checked before Codex closes. If it needs renewal, sign in in the browser and switching will continue automatically.",
            "Switch") { Owner = this };
        if (confirm.ShowDialog() == true) await _controller.SwitchAsync(account);
        }
        finally { _switchPromptOpen = false; }
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.Busy || _exiting || sender is not Button { Tag: AccountProfileViewModel account }) return;
        await _controller.ReconnectAsync(account);
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _controller.CancelCurrentOperation();

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.Busy || sender is not Button { Tag: AccountProfileViewModel account }) return;
        var dialog = new AccountDialog("Edit private label", account.DisplayName, account.ColorHex) { Owner = this };
        if (dialog.ShowDialog() == true) await _controller.RenameAsync(account, dialog.Alias, dialog.SelectedColor);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.Busy || sender is not Button { Tag: AccountProfileViewModel account }) return;
        var confirm = new ConfirmDialog(
            $"Remove \"{account.DisplayName}\"?",
            "This deletes only the encrypted profile stored by this utility. It does not delete the OpenAI account.",
            "Remove") { Owner = this };
        if (confirm.ShowDialog() == true) await _controller.DeleteAsync(account);
    }

    private async void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        _timer.Stop();
        await _controller.StopAsync();
        _tray?.Dispose();
        _allowClose = true;
        Close();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitApplication();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_tray is not null && !_exiting) Hide();
        else ExitApplication();
    }

    internal void RenderPreview(string path)
    {
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY)),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    internal void CloseForRendering()
    {
        _exiting = true;
        _allowClose = true;
        _timer.Stop();
        Close();
    }

}
