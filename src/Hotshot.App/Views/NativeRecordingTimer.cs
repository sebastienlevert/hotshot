using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hotshot.Views;

internal static unsafe partial class NativeRecordingTimer
{
    private const nuint SubclassId = 0x485354;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint window,
        delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> callback, nuint id, nuint data);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(nint window,
        delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> callback, nuint id);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);

    public static void AllowCompactSize(nint window)
    {
        if (!SetWindowSubclass(window, &WindowProcedure, SubclassId, 0))
            throw new InvalidOperationException("Could not configure the recording timer's size.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProcedure(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == 0x0082)
            RemoveWindowSubclass(window, &WindowProcedure, id);
        var result = DefSubclassProc(window, message, wParam, lParam);
        if (message == 0x0024 && lParam != 0)
        {
            // Borderless WinUI retains the system's normal-window minimum size.
            var limits = (MinMaxInfo*)lParam;
            limits->MinTrackSize = new Point { X = 1, Y = 1 };
        }
        return result;
    }
}
