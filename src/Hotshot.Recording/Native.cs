using System.Runtime.InteropServices;

namespace Hotshot.Recording;

internal static partial class Native
{
    internal const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    internal const uint TIMER_ALL_ACCESS = 0x001F0003;
    internal const uint WAIT_OBJECT_0 = 0;

    [LibraryImport("d3d11.dll")]
    internal static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateWaitableTimerEx(nint timerAttributes, string? timerName, uint flags, uint desiredAccess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWaitableTimer(nint timer, in long dueTime, int period, nint completionRoutine, nint argToCompletionRoutine, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(nint hwnd);
}
