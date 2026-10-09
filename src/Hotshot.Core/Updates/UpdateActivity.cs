using System.Runtime.InteropServices;

namespace Hotshot.Core.Updates;

public readonly record struct UpdateActivity(
    bool Capturing = false,
    bool Recording = false,
    bool Converting = false,
    bool UnsavedEdits = false,
    bool EditorBusy = false,
    bool ActiveWindow = false,
    bool UnsavedSettings = false,
    bool Exiting = false)
{
    public bool CanRestart => !(Capturing || Recording || Converting || UnsavedEdits ||
        EditorBusy || ActiveWindow || UnsavedSettings || Exiting);
}

public static class UpdateChannel
{
    public static string ForArchitecture(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "win-x64",
        Architecture.Arm64 => "win-arm64",
        _ => throw new PlatformNotSupportedException($"Hotshot updates do not support {architecture}."),
    };
}
