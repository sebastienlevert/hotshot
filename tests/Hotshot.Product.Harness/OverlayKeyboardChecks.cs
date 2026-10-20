using Hotshot.Capture;
using Hotshot.CaptureFlow;
using Hotshot.Interop;
using Hotshot.Shell;

namespace Hotshot.ProductHarness;

internal static class OverlayKeyboardChecks
{
    public static async Task RunAsync()
    {
        var bounds = Monitors.VirtualScreen;
        using var bitmap = new GdiBitmap(bounds.Width, bounds.Height);
        bitmap.Span.Fill(96);
        foreach (var mode in Enum.GetValues<OverlayMode>())
        foreach (var dragging in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var selection = CaptureOverlay.SelectAsync(bitmap, bounds, new OverlayOptions(mode, false, false, false), cancellation.Token);
            try
            {
                var window = Win32.FindWindow("Hotshot.CaptureOverlay", null);
                _ = NativeProductWindow.GetWindowThreadProcessId(window, out var processId);
                if (window == 0 || processId != (uint)Environment.ProcessId || NativeProductWindow.GetFocus() != window)
                    throw new InvalidDataException("The generated overlay did not receive native keyboard focus.");
                var dialogCode = Win32.SendMessage(window, Win32.WM_GETDLGCODE, Win32.VK_ESCAPE, 0);
                if ((dialogCode & Win32.DLGC_WANTALLKEYS) == 0)
                    throw new InvalidDataException("Dialog navigation can consume Escape before it reaches the overlay.");
                if (dragging)
                {
                    Post(window, Win32.WM_LBUTTONDOWN, 1, Position(100, 100));
                    Post(window, Win32.WM_MOUSEMOVE, 1, Position(200, 160));
                }
                Post(window, Win32.WM_KEYDOWN, Win32.VK_ESCAPE, 1);
                if (await selection.WaitAsync(TimeSpan.FromSeconds(3)) is not null)
                    throw new InvalidDataException("Escape selected a capture instead of cancelling.");
                if (NativeProductWindow.GetFocus() == window || NativeProductWindow.GetCapture() == window)
                    throw new InvalidDataException("Cancelling the overlay retained native keyboard or mouse capture.");
                Console.WriteLine($"PASS overlay queued Escape: {mode}, dragging={dragging}, no selection and input released.");
            }
            finally
            {
                cancellation.Cancel();
                await selection.WaitAsync(TimeSpan.FromSeconds(3));
            }
        }
    }

    public static async Task RunPointerAsync()
    {
        var bounds = Monitors.VirtualScreen;
        using var bitmap = new GdiBitmap(bounds.Width, bounds.Height);
        bitmap.Span.Fill(96);
        foreach (var monitor in Monitors.GetAll())
        {
            using var target = new PointerFixture(monitor.Bounds);
            Win32.ShowWindow(target.Handle, Win32.SW_SHOW);
            Win32.SetWindowPos(target.Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE);
            var targetBounds = WindowEnumerator.GetBounds(target.Handle)
                ?? throw new InvalidDataException("The generated pointer target has no native bounds.");
            var (screenX, screenY) = targetBounds.Center;
            var x = screenX - bounds.X;
            var y = screenY - bounds.Y;
            foreach (var mode in Enum.GetValues<OverlayMode>())
            foreach (var dragging in new[] { false, true })
            {
                using var cancellation = new CancellationTokenSource();
                var selection = CaptureOverlay.SelectAsync(bitmap, bounds,
                    new OverlayOptions(mode, true, true, true, target.Handle), cancellation.Token);
                try
                {
                    var window = Win32.FindWindow("Hotshot.CaptureOverlay", null);
                    _ = NativeProductWindow.GetWindowThreadProcessId(window, out var processId);
                    if (window == 0 || processId != (uint)Environment.ProcessId)
                        throw new InvalidDataException("The generated pointer overlay was not created.");
                    RequireCrosshair();
                    Win32.SendMessage(window, Win32.WM_MOUSEMOVE, 0, Position(x, y));
                    Win32.SendMessage(window, Win32.WM_SETCURSOR, window, 1);
                    RequireCrosshair();
                    Win32.SendMessage(window, Win32.WM_LBUTTONDOWN, 1, Position(x, y));
                    if (dragging)
                    {
                        Win32.SendMessage(window, Win32.WM_MOUSEMOVE, 1, Position(x + 60, y + 40));
                        RequireCrosshair();
                    }
                    Win32.SendMessage(window, Win32.WM_LBUTTONUP, 0, Position(x + (dragging ? 60 : 0), y + (dragging ? 40 : 0)));
                    var result = await selection.WaitAsync(TimeSpan.FromSeconds(3));
                    if (dragging)
                    {
                        if (result is not RegionSelection region || region.Rect != new PixelRect(screenX, screenY, 61, 41))
                            throw new InvalidDataException("Dragging over a window did not select the expected physical-pixel region.");
                    }
                    else if (result is not WindowSelection selected || selected.Window.Handle != target.Handle ||
                        selected.Rect != targetBounds.Intersect(bounds))
                        throw new InvalidDataException("Clicking with the crosshair did not preserve whole-window capture.");
                    if (NativeProductWindow.GetFocus() == window || NativeProductWindow.GetCapture() == window)
                        throw new InvalidDataException("Completing the overlay retained native input capture.");
                    Console.WriteLine($"PASS overlay crosshair and pointer selection: {mode}, monitor={monitor.Index}, dragging={dragging}.");
                }
                finally
                {
                    cancellation.Cancel();
                    await selection.WaitAsync(TimeSpan.FromSeconds(3));
                }
            }
        }
    }

    private static void RequireCrosshair()
    {
        if (NativeProductWindow.GetCursor() != Win32.LoadCursor(0, Win32.IDC_CROSS))
            throw new InvalidDataException("The overlay must show a selection crosshair before and during a drag, including over windows.");
    }

    private sealed class PointerFixture : NativeWindow
    {
        public PointerFixture(PixelRect monitor) =>
            CreateHandle("Hotshot.Pointer.Target", Win32.WS_EX_TOOLWINDOW, Win32.WS_POPUP,
                monitor.X + 40, monitor.Y + 40, 320, 200, "Generated pointer target");
    }

    private static nint Position(int x, int y) => (y << 16) | x;

    private static void Post(nint window, uint message, nint wParam, nint lParam)
    {
        if (!Win32.PostMessage(window, message, wParam, lParam))
            throw new InvalidOperationException("Could not queue input to the generated overlay.");
    }
}
