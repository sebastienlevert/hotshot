using System.ComponentModel;
using Hotshot.Interop;

namespace Hotshot.Capture;

public sealed record DisplayColorInfo(int MonitorIndex, PixelRect Bounds, double Scale, bool? HdrSupported,
    bool? HdrEnabled, string ActiveColorMode, int? BitsPerColorChannel, int NativeStatus);

/// <summary>Read-only display facts. Unsupported color queries remain unknown, not SDR/HDR success.</summary>
public static unsafe class DisplayEnvironment
{
    private const uint OnlyActivePaths = 2;
    private const int InsufficientBuffer = 122;
    private const int NotFound = 1168;

    public static IReadOnlyList<DisplayColorInfo> Read()
    {
        using var awareness = new DpiAwarenessScope();
        var paths = ReadPaths();
        var colors = new Dictionary<string, NativeDisplayConfiguration.AdvancedColor>(StringComparer.OrdinalIgnoreCase);
        var errors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var name = new NativeDisplayConfiguration.SourceName
            {
                Header = Header(1, (uint)sizeof(NativeDisplayConfiguration.SourceName), path.Source.Adapter, path.Source.Id),
            };
            var status = NativeDisplayConfiguration.DisplayConfigGetDeviceInfo(&name.Header);
            if (status != 0) throw new Win32Exception(status, "The active display source could not be identified.");
            var device = new string(name.Name).TrimEnd('\0');
            var color = new NativeDisplayConfiguration.AdvancedColor
            {
                Header = Header(15, (uint)sizeof(NativeDisplayConfiguration.AdvancedColor), path.Target.Adapter, path.Target.Id),
            };
            status = NativeDisplayConfiguration.DisplayConfigGetDeviceInfo(&color.Header);
            if (status == 0) colors[device] = color;
            else errors[device] = status;
        }
        return Monitors.GetAll().Select(monitor =>
        {
            if (!colors.TryGetValue(monitor.DeviceName, out var color))
                return new DisplayColorInfo(monitor.Index, monitor.Bounds, monitor.Scale, null, null, "Unknown", null,
                    errors.GetValueOrDefault(monitor.DeviceName, NotFound));
            var mode = color.ActiveColorMode switch { 0 => "SDR", 1 => "WCG", 2 => "HDR", _ => "Unknown" };
            return new DisplayColorInfo(monitor.Index, monitor.Bounds, monitor.Scale, (color.Flags & 16) != 0,
                (color.Flags & 32) != 0, mode, checked((int)color.BitsPerChannel), 0);
        }).ToArray();
    }

    private static NativeDisplayConfiguration.Header Header(uint type, uint size, NativeDisplayConfiguration.AdapterId adapter, uint id) =>
        new() { Type = type, Size = size, Adapter = adapter, Id = id };

    private static NativeDisplayConfiguration.Path[] ReadPaths()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var status = NativeDisplayConfiguration.GetDisplayConfigBufferSizes(OnlyActivePaths, out var pathCount, out var modeCount);
            if (status != 0) throw new Win32Exception(status, "Display topology could not be queried.");
            if (pathCount is 0 or > 64 || modeCount > 256)
                throw new InvalidOperationException("Display topology returned unsupported buffer dimensions.");
            var paths = new NativeDisplayConfiguration.Path[pathCount];
            var modes = new NativeDisplayConfiguration.Mode[Math.Max(modeCount, 1)];
            fixed (NativeDisplayConfiguration.Path* pathData = paths)
            fixed (NativeDisplayConfiguration.Mode* modeData = modes)
                status = NativeDisplayConfiguration.QueryDisplayConfig(OnlyActivePaths, ref pathCount, pathData,
                    ref modeCount, modeData, 0);
            if (status == InsufficientBuffer) continue;
            if (status != 0) throw new Win32Exception(status, "Active display paths could not be read.");
            return paths.Take(checked((int)pathCount)).ToArray();
        }
        throw new InvalidOperationException("Display topology kept changing. Retry the operation.");
    }
}
