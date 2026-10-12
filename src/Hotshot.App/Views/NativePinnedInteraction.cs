using System.ComponentModel;
using System.Runtime.InteropServices;
using Hotshot.Capture;
using Hotshot.Interop;
using Hotshot.Shell;

namespace Hotshot.Views;

/// <summary>Window-local styles and a native, independently clickable, timed recovery window. No hooks.</summary>
internal sealed class NativePinnedInteraction : IDisposable
{
    private const int ExtendedStyle = -20;
    private const long ThroughFlags = Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT;
    private readonly nint _handle;
    private readonly long _originalFlags;
    private readonly Action _recovered;
    private readonly Action<Exception> _restoreFailed;
    private NativePinnedRecovery? _recovery;
    private bool _active;
    private byte _opacity = 255;

    public NativePinnedInteraction(nint handle, Action recovered, Action<Exception> restoreFailed)
    {
        _handle = handle; _recovered = recovered; _restoreFailed = restoreFailed;
        _originalFlags = ReadStyle() & ThroughFlags;
    }

    public void Enable()
    {
        if (_active) return;
        // Create, show and arm recovery before making the image window untouchable.
        var recovery = new NativePinnedRecovery(() => Restore());
        try { recovery.Open(_handle); }
        catch { recovery.Dispose(); throw; }
        _recovery = recovery;
        _active = true;
        try
        {
            ApplyAppearance();
        }
        catch
        {
            Restore();
            throw;
        }
    }

    public void SetOpacity(double percent)
    {
        var previous = _opacity;
        _opacity = (byte)Math.Round(Math.Clamp(percent, 20, 100) * 255 / 100);
        try { ApplyAppearance(); }
        catch
        {
            _opacity = previous;
            try { ApplyAppearance(); }
            catch (Exception ex) { _restoreFailed(ex); }
            throw;
        }
    }

    public void Restore(bool notify = true)
    {
        if (!_active) return;
        Exception? error = null;
        try
        {
            if (NativePinnedApi.IsWindow(_handle))
                ApplyAppearance(through: false);
        }
        catch (Exception ex) { error = ex; }
        finally
        {
            _active = false;
            _recovery?.Dispose(); _recovery = null;
        }
        // Failure closes the pin rather than leaving a transparent, unrecoverable window.
        if (error is not null) _restoreFailed(error);
        else if (notify) _recovered();
    }

    private void ApplyAppearance(bool? through = null)
    {
        var clickThrough = through ?? _active;
        var flags = (ReadStyle() & ~ThroughFlags) | _originalFlags;
        if (clickThrough) flags |= Win32.WS_EX_TRANSPARENT;
        if (clickThrough || _opacity != 255) flags |= Win32.WS_EX_LAYERED;
        WriteStyle(flags);
        if ((flags & Win32.WS_EX_LAYERED) != 0 &&
            !NativePinnedApi.SetLayeredWindowAttributes(_handle, 0, _opacity, Win32.LWA_ALPHA))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not apply pinned window opacity/click-through.");
    }

    private long ReadStyle()
    {
        Marshal.SetLastPInvokeError(0);
        var style = NativePinnedApi.GetWindowLongPtr(_handle, ExtendedStyle);
        var error = Marshal.GetLastPInvokeError();
        if (style == 0 && error != 0) throw new Win32Exception(error);
        return (long)style;
    }

    private void WriteStyle(long style)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = NativePinnedApi.SetWindowLongPtr(_handle, ExtendedStyle, (nint)style);
        var error = Marshal.GetLastPInvokeError();
        if (previous == 0 && error != 0) throw new Win32Exception(error);
        const uint frameChanged = 0x0020;
        if (!Win32.SetWindowPos(_handle, 0, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | frameChanged))
            throw new InvalidOperationException("Could not refresh the pinned window's input style.");
    }

    public void Dispose()
    {
        Restore(notify: false);
        _recovery?.Dispose(); _recovery = null;
    }
}

internal sealed class NativePinnedRecovery(Action resume) : NativeWindow
{
    private const uint Command = 0x0111;
    private const nuint TimerId = 1;
    private const int LifetimeSeconds = 20;
    private nint _button;
    private long _deadline;
    private bool _disposing;

    public void Open(nint owner)
    {
        if (!NativePinnedApi.GetWindowRect(owner, out var bounds))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        var monitor = Monitors.FromRect(new PixelRect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top));
        var work = monitor.WorkArea;
        var width = Math.Min(work.Width, (int)Math.Round(300 * monitor.Scale));
        var height = Math.Min(work.Height, (int)Math.Round(48 * monitor.Scale));
        var x = Math.Clamp(bounds.Left + 12, work.Left, work.Right - width);
        var y = Math.Clamp(bounds.Top + 12, work.Top, work.Bottom - height);
        CreateHandle("Hotshot.PinnedRecovery", Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW,
            Win32.WS_POPUP, x, y, width, height, "Resume Hotshot pinned image");
        Marshal.SetLastPInvokeError(0);
        var previousOwner = NativePinnedApi.SetWindowLongPtr(Handle, -8, owner);
        if (previousOwner == 0 && Marshal.GetLastPInvokeError() != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!Win32.SetWindowDisplayAffinity(Handle, Win32.WDA_EXCLUDEFROMCAPTURE))
            throw new InvalidOperationException("Could not exclude the pin recovery control from captures.");
        const uint childVisiblePushButton = 0x50000000;
        _button = Win32.CreateWindowEx(0, "BUTTON", "Resume pinned image (20 s)", childVisiblePushButton,
            0, 0, width, height, Handle, 1, Win32.GetModuleHandle(null), 0);
        if (_button == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create the pin recovery button.");
        _deadline = Environment.TickCount64 + LifetimeSeconds * 1000;
        if (NativePinnedApi.SetTimer(Handle, TimerId, 1000, 0) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not arm automatic pin recovery.");
        Win32.ShowWindow(Handle, Win32.SW_SHOWNOACTIVATE);
    }

    protected override nint WndProc(uint msg, nint wParam, nint lParam)
    {
        if (msg == Command && (wParam & 0xFFFF) == 1 && ((wParam >> 16) & 0xFFFF) == 0 && lParam == _button)
        {
            resume();
            return 0;
        }
        if (msg == Win32.WM_CLOSE || (msg == Win32.WM_KEYDOWN && (int)wParam == Win32.VK_ESCAPE))
        {
            resume();
            return 0;
        }
        if (msg == Win32.WM_TIMER && (nuint)wParam == TimerId)
        {
            var remaining = _deadline - Environment.TickCount64;
            if (remaining <= 0) resume();
            else NativePinnedApi.SetWindowText(_button, $"Resume pinned image ({(remaining + 999) / 1000} s)");
            return 0;
        }
        return base.WndProc(msg, wParam, lParam);
    }

    protected override void OnDestroyed()
    {
        _button = 0;
        if (!_disposing) resume();
    }

    public override void Dispose()
    {
        _disposing = true;
        if (Handle != 0) Win32.KillTimer(Handle, TimerId);
        base.Dispose();
    }
}

internal static partial class NativePinnedApi
{
    [StructLayout(LayoutKind.Sequential)]
    public struct WindowRect { public int Left, Top, Right, Bottom; }
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hwnd, out WindowRect rect);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nuint SetTimer(nint hwnd, nuint id, uint milliseconds, nint callback);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowText(nint hwnd, string text);
}
