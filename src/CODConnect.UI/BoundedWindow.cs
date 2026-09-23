using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CODConnect.UI;

internal static class BoundedWindow
{
    private const int StyleIndex = -16;
    private const int MaximizeBox = 0x00010000;
    private const int SystemCommand = 0x0112;
    private const int Maximize = 0xF030;

    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            SetWindowLong(handle, StyleIndex, GetWindowLong(handle, StyleIndex) & ~MaximizeBox);
            HwndSource.FromHwnd(handle)?.AddHook(BlockMaximize);
        };
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState == WindowState.Maximized) window.WindowState = WindowState.Normal;
        };
    }

    private static nint BlockMaximize(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == SystemCommand && ((long)wParam & 0xFFF0) == Maximize) handled = true;
        return 0;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(nint hwnd, int index, int value);
}
