using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hotshot.ProductHarness;

internal static unsafe partial class NativeProductWindow
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }

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
