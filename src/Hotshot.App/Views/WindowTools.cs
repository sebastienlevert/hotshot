using Hotshot.Capture;
using Hotshot.Interop;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Hotshot.Core.Settings;

namespace Hotshot.Views;

internal static class WindowTools
{
    public static void Configure(Window window, int width, int height, bool compact = false, bool excludeFromCapture = true,
        AppServices? app = null, string? layoutKey = null)
    {
        var monitor = Monitors.FromCursor();
        var w = Math.Min((int)(width * monitor.Scale), monitor.WorkArea.Width);
        var h = Math.Min((int)(height * monitor.Scale), monitor.WorkArea.Height);
        window.AppWindow.MoveAndResize(new RectInt32(
            monitor.WorkArea.X + (monitor.WorkArea.Width - w) / 2,
            monitor.WorkArea.Y + (monitor.WorkArea.Height - h) / 2, w, h));
        if (app is not null && layoutKey is not null)
        {
            if (app.Settings.Layout.Windows.TryGetValue(layoutKey, out var saved) && saved.Width >= 200 && saved.Height >= 150 && saved.Scale > 0)
            {
                var bounds = new PixelRect(saved.X, saved.Y, saved.Width, saved.Height);
                var target = Monitors.GetAll().OrderByDescending(candidate => (long)bounds.Intersect(candidate.WorkArea).Width * bounds.Intersect(candidate.WorkArea).Height).First();
                var rw = Math.Clamp((int)(saved.Width * target.Scale / saved.Scale), Math.Min(200, target.WorkArea.Width), target.WorkArea.Width);
                var rh = Math.Clamp((int)(saved.Height * target.Scale / saved.Scale), Math.Min(150, target.WorkArea.Height), target.WorkArea.Height);
                window.AppWindow.MoveAndResize(new RectInt32(Math.Clamp(saved.X, target.WorkArea.X, target.WorkArea.Right - rw),
                    Math.Clamp(saved.Y, target.WorkArea.Y, target.WorkArea.Bottom - rh), rw, rh));
            }
            window.AppWindow.Closing += (_, _) =>
            {
                var position = window.AppWindow.Position; var size = window.AppWindow.Size;
                var scale = Monitors.FromRect(new PixelRect(position.X, position.Y, size.Width, size.Height)).Scale;
                app.Settings.Layout.Windows[layoutKey] = new WindowLayout(position.X, position.Y, size.Width, size.Height, scale);
                app.SaveSettings();
            };
        }
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
        if (!Win32.SetWindowDisplayAffinity(hwnd, excludeFromCapture ? Win32.WDA_EXCLUDEFROMCAPTURE : Win32.WDA_NONE))
        {
            Log.Warn("Could not set a Hotshot window's capture visibility.");
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
