using Forms = System.Windows.Forms;

namespace CodexAccountSwitcher.Services;

internal sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _refresh;
    private bool _disposed;

    public TrayIconService(Action show, Action refresh, Action quit)
    {
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("Show accounts", null, (_, _) => show());
        _refresh = new Forms.ToolStripMenuItem("Refresh usage", null, (_, _) => refresh());
        _menu.Items.Add(_refresh);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Quit", null, (_, _) => quit());
        _icon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Codex Account Switcher",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) show(); };
        DiagnosticTrace.Write("notification-area icon created");
    }

    public bool IsVisible => _icon.Visible;
    public void SetBusy(bool busy) => _refresh.Enabled = !busy;
    public void ShowError(string message)
    {
        if (_disposed) return;
        _icon.BalloonTipTitle = "Codex Account Switcher";
        _icon.BalloonTipText = message.Length > 240 ? message[..240] : message;
        _icon.BalloonTipIcon = Forms.ToolTipIcon.Warning;
        _icon.ShowBalloonTip(5000);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
