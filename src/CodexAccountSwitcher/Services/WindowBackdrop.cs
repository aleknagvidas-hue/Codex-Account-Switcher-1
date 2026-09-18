using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CodexAccountSwitcher.Services;

public static class WindowBackdrop
{
    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (HwndSource.FromHwnd(handle) is { } source)
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        }

        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(handle, ref margins);

        var darkMode = 1;
        DwmSetWindowAttribute(handle, 20, ref darkMode, sizeof(int));
        var corner = 2;
        DwmSetWindowAttribute(handle, 33, ref corner, sizeof(int));
        var backdrop = 3;
        DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
}
