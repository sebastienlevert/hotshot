using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Hotshot.Interop;

namespace Hotshot.Capture;

public sealed record MonitorInfo(nint Handle, PixelRect Bounds, PixelRect WorkArea, bool IsPrimary, int Index, string DeviceName, double Scale);

public static unsafe class Monitors
{
    public static PixelRect VirtualScreen => new(
        Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN),
        Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN),
        Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN),
        Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));

    /// <summary>All monitors ordered left-to-right then top-to-bottom; <see cref="MonitorInfo.Index"/> is 1-based in that order.</summary>
    public static IReadOnlyList<MonitorInfo> GetAll()
    {
        var handles = new List<nint>();
        var gc = GCHandle.Alloc(handles);
        try
        {
            Native.EnumDisplayMonitors(0, null, &OnMonitor, GCHandle.ToIntPtr(gc));
        }
        finally
        {
            gc.Free();
        }

        var raw = new List<(nint Handle, PixelRect Bounds, PixelRect Work, bool Primary, string Device, double Scale)>();
        foreach (var handle in handles)
        {
            var info = new MONITORINFOEXW { cbSize = sizeof(MONITORINFOEXW) };
            if (!Native.GetMonitorInfo(handle, &info))
            {
                continue;
            }

            var device = new string(info.szDevice).TrimEnd('\0');
            var scale = Native.GetDpiForMonitor(handle, 0, out var dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
            raw.Add((handle, PixelRect.FromRect(info.rcMonitor), PixelRect.FromRect(info.rcWork),
                (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0, device, scale));
        }

        return raw
            .OrderBy(m => m.Bounds.X)
            .ThenBy(m => m.Bounds.Y)
            .Select((m, i) => new MonitorInfo(m.Handle, m.Bounds, m.Work, m.Primary, i + 1, m.Device, m.Scale))
            .ToList();
    }

    public static MonitorInfo FromPoint(int x, int y)
    {
        var all = GetAll();
        var handle = Native.MonitorFromPoint(new POINT(x, y), Native.MONITOR_DEFAULTTONEAREST);
        return all.FirstOrDefault(m => m.Handle == handle)
            ?? all.FirstOrDefault(m => m.Bounds.Contains(x, y))
            ?? all.First(m => m.IsPrimary);
    }

    public static MonitorInfo FromCursor()
    {
        Native.GetCursorPos(out var pt);
        return FromPoint(pt.X, pt.Y);
    }

    public static MonitorInfo FromRect(PixelRect rect)
    {
        var (cx, cy) = rect.Center;
        return FromPoint(cx, cy);
    }

    [UnmanagedCallersOnly]
    private static int OnMonitor(nint monitor, nint hdc, RECT* rect, nint data)
    {
        var list = (List<nint>)GCHandle.FromIntPtr(data).Target!;
        list.Add(monitor);
        return 1;
    }
}
