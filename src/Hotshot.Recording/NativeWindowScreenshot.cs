using System.Runtime.InteropServices;

namespace Hotshot.Recording;

internal static partial class NativeWindowScreenshot
{
    internal static readonly nint PerMonitorAwareV2 = -4;
    internal const uint ExtendedFrameBounds = 9;
    internal const uint Cloaked = 14;
    internal const uint RootWindow = 2;
    internal const int ClassAtom = -32;
    internal const int WasStillDrawing = unchecked((int)0x887A000A);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [LibraryImport("combase.dll")]
    internal static partial int RoInitialize(uint initializationType);

    [LibraryImport("combase.dll")]
    internal static partial void RoUninitialize();

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint SetThreadDpiAwarenessContext(nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    internal static partial nint GetAncestor(nint window, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    internal static partial nuint GetClassLongPtr(nint window, int index);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint window);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowDisplayAffinity(nint window, out uint affinity);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(nint window, uint attribute, out Rect value, uint size);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    internal static partial int DwmGetWindowAttributeUInt32(nint window, uint attribute, out uint value, uint size);
}
