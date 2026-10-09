using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Hotshot.Interop;

namespace Hotshot.Capture;

public sealed record WindowInfo(nint Handle, PixelRect Bounds, string Title, string ClassName, uint ProcessId);

public static unsafe class WindowEnumerator
{
    private static readonly ConcurrentDictionary<uint, string> ProcessNames = new();
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Windows.UI.Core.CoreWindow", "ForegroundStaging", "MultitaskingViewFrame",
        "XamlExplorerHostIslandWindow", "Shell_InputSwitchTopLevelWindow",
    };

    /// <summary>Visible, non-cloaked top-level windows in z-order (topmost first).</summary>
    public static IReadOnlyList<WindowInfo> GetVisibleWindows(uint? excludeProcessId = null)
    {
        var handles = new List<nint>(256);
        var gc = GCHandle.Alloc(handles);
        try
        {
            Native.EnumWindows(&OnWindow, GCHandle.ToIntPtr(gc));
        }
        finally
        {
            gc.Free();
        }

        var result = new List<WindowInfo>(handles.Count);
        foreach (var hwnd in handles)
        {
            if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd) || Native.IsCloaked(hwnd))
            {
                continue;
            }

            var exStyle = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            if ((exStyle & Native.WS_EX_TRANSPARENT) != 0)
            {
                continue;
            }

            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (excludeProcessId == pid)
            {
                continue;
            }

            var className = Native.GetWindowClass(hwnd);
            if (IgnoredClasses.Contains(className))
            {
                continue;
            }

            var title = Native.GetWindowTitle(hwnd);
            if ((exStyle & Native.WS_EX_TOOLWINDOW) != 0 && title.Length == 0)
            {
                continue;
            }

            var bounds = GetBounds(hwnd);
            if (bounds is not { } b || b.Width < 8 || b.Height < 8)
            {
                continue;
            }

            result.Add(new WindowInfo(hwnd, b, title, className, pid));
        }

        return result;
    }

    /// <summary>Visible window bounds (DWM extended frame, without the invisible resize border).</summary>
    public static PixelRect? GetBounds(nint hwnd)
    {
        RECT rect;
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, &rect, sizeof(RECT)) != 0
            && !Native.GetWindowRect(hwnd, out rect))
        {
            return null;
        }

        var r = PixelRect.FromRect(rect);
        return r.IsEmpty ? null : r;
    }

    public static string GetProcessName(nint hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        return GetProcessName(pid);
    }

    public static string GetProcessName(uint pid)
    {
        if (pid == 0)
        {
            return string.Empty;
        }

        return ProcessNames.GetOrAdd(pid, static id =>
        {
            var process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, id);
            if (process == 0)
            {
                return string.Empty;
            }

            try
            {
                var buffer = stackalloc char[1024];
                var size = 1024;
                return Native.QueryFullProcessImageName(process, 0, buffer, ref size)
                    ? Path.GetFileNameWithoutExtension(new string(buffer, 0, size))
                    : string.Empty;
            }
            finally
            {
                Native.CloseHandle(process);
            }
        });
    }

    /// <summary>Process name and title of the current foreground window (for naming tokens).</summary>
    public static (string App, string Title) GetForegroundInfo()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == 0)
        {
            return (string.Empty, string.Empty);
        }

        hwnd = Native.GetAncestor(hwnd, Native.GA_ROOT) is var root && root != 0 ? root : hwnd;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == (uint)Environment.ProcessId)
        {
            return (string.Empty, string.Empty);
        }

        return (GetProcessName(pid), Native.GetWindowTitle(hwnd));
    }

    [UnmanagedCallersOnly]
    private static int OnWindow(nint hwnd, nint data)
    {
        ((List<nint>)GCHandle.FromIntPtr(data).Target!).Add(hwnd);
        return 1;
    }
}
