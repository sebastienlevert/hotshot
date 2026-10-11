using System.Runtime.InteropServices;

namespace Hotshot.AgentHarness;

internal static partial class HarnessNative
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessDpiAwarenessContext(nint context);

    [LibraryImport("user32.dll")]
    internal static partial uint GetClipboardSequenceNumber();
}
