using System.Runtime.InteropServices;

namespace Hotshot.Gif;

internal static partial class NativeMethods
{
    public const int COINIT_MULTITHREADED = 0x0;
    public const int S_OK = 0;
    public const int S_FALSE = 1;

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint reserved, int coInit);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();
}
