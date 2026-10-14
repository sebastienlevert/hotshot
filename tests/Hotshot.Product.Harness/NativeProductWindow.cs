using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hotshot.ProductHarness;

internal static unsafe partial class NativeProductWindow
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)]
    private struct UserObjectFlags { public int Inherit, Reserved; public uint Flags; }

    [LibraryImport("user32.dll")]
    private static partial nint GetProcessWindowStation();
    [LibraryImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetUserObjectInformation(nint handle, int index, out UserObjectFlags flags, uint length, out uint needed);
    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
    [LibraryImport("user32.dll")]
    private static partial nint GetThreadDesktop(uint thread);
    [LibraryImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDesktopInputInformation(nint handle, int index, out int receivesInput, uint length, out uint needed);
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowDisplayAffinity(nint window, out uint affinity);

    public static uint CaptureAffinity(nint window)
    {
        if (!GetWindowDisplayAffinity(window, out var affinity))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Cannot query this fixture window's capture visibility.");
        return affinity;
    }

    public static bool IsInteractiveWindowStation()
    {
        if (!GetUserObjectInformation(GetProcessWindowStation(), 1, out var flags, (uint)sizeof(UserObjectFlags), out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Cannot query this process's window station.");
        return (flags.Flags & 1) != 0;
    }

    public static bool OwnDesktopReceivesInput()
    {
        // Query only our existing desktop's metadata; never open or capture the user's desktop.
        if (!GetDesktopInputInformation(GetThreadDesktop(GetCurrentThreadId()), 6, out var input, sizeof(int), out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Cannot query whether this process's desktop receives input.");
        return input != 0;
    }

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint window,
        delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> callback, nuint id, nuint data);
    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);

    public static void AllowPublicationSize(nint window)
    {
        if (!SetWindowSubclass(window, &WindowProcedure, 0x485350, 0))
            throw new InvalidOperationException("Cannot size the fixture-owned publication window.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProcedure(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        var result = DefSubclassProc(window, message, wParam, lParam);
        if (message == 0x0024 && lParam != 0)
        {
            // Only this harness's windows may exceed the attached portrait display for a wide UI capture.
            var limits = (MinMaxInfo*)lParam;
            limits->MaxTrackSize = new Point { X = 4000, Y = 3000 };
        }
        return result;
    }
}
