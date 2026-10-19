using Hotshot.Capture;
using Hotshot.CaptureFlow;
using Hotshot.Interop;

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

    private static nint Position(int x, int y) => (y << 16) | x;

    private static void Post(nint window, uint message, nint wParam, nint lParam)
    {
        if (!Win32.PostMessage(window, message, wParam, lParam))
            throw new InvalidOperationException("Could not queue input to the generated overlay.");
    }
}
