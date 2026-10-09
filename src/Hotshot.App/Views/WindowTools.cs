using Hotshot.Capture;
using Hotshot.Interop;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Hotshot.Views;

internal static class WindowTools
{
    public static void Configure(Window window, int width, int height, bool compact = false)
    {
        var monitor = Monitors.FromCursor();
        var w = Math.Min((int)(width * monitor.Scale), monitor.WorkArea.Width);
        var h = Math.Min((int)(height * monitor.Scale), monitor.WorkArea.Height);
        window.AppWindow.MoveAndResize(new RectInt32(
            monitor.WorkArea.X + (monitor.WorkArea.Width - w) / 2,
            monitor.WorkArea.Y + (monitor.WorkArea.Height - h) / 2, w, h));
        window.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Hotshot.ico"));
        UiStyles.BindTitleBarTheme(window);
        if (window.AppWindow.Presenter is OverlappedPresenter presenter && compact)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (!Win32.SetWindowDisplayAffinity(hwnd, Win32.WDA_EXCLUDEFROMCAPTURE))
        {
            Log.Warn("Could not exclude a Hotshot window from captures.");
        }
    }

    public static void PlaceNearTray(Window window)
    {
        var work = Monitors.FromCursor().WorkArea;
        var size = window.AppWindow.Size;
        window.AppWindow.Move(new PointInt32(
            work.Right - size.Width - 12,
            work.Bottom - size.Height - 12));
    }
}
