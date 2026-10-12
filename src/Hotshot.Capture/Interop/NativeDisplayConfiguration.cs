using System.Runtime.InteropServices;

namespace Hotshot.Interop;

internal static unsafe partial class NativeDisplayConfiguration
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct AdapterId { internal uint Low; internal int High; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Source { internal AdapterId Adapter; internal uint Id, ModeIndex, Flags; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Target
    {
        internal AdapterId Adapter;
        internal uint Id, ModeIndex, Technology, Rotation, Scaling, RefreshNumerator, RefreshDenominator,
            ScanLineOrdering;
        internal int Available;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Path { internal Source Source; internal Target Target; internal uint Flags; }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct Mode { [FieldOffset(0)] internal uint Type; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Header { internal uint Type, Size; internal AdapterId Adapter; internal uint Id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SourceName { internal Header Header; internal fixed char Name[32]; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AdvancedColor
    {
        internal Header Header;
        internal uint Flags, Encoding, BitsPerChannel, ActiveColorMode;
    }

    [LibraryImport("user32.dll")]
    internal static partial int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);

    [LibraryImport("user32.dll")]
    internal static partial int QueryDisplayConfig(uint flags, ref uint pathCount, Path* paths,
        ref uint modeCount, Mode* modes, nint topology);

    [LibraryImport("user32.dll")]
    internal static partial int DisplayConfigGetDeviceInfo(Header* packet);
}
