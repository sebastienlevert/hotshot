namespace Hotshot.Recording;

public readonly record struct RectInt(int X, int Y, int Width, int Height);

public abstract record RecordingTarget;

/// <summary>Records a monitor. <paramref name="Crop"/> is in physical pixels relative to the monitor's top-left; null = whole monitor.</summary>
public sealed record MonitorTarget(nint MonitorHandle, RectInt? Crop = null) : RecordingTarget;

/// <summary>Records a top-level window. The output size is fixed to the window size when recording starts.</summary>
public sealed record WindowTarget(nint WindowHandle) : RecordingTarget;

public sealed class RecordingOptions
{
    public required RecordingTarget Target { get; init; }

    /// <summary>Output .mp4 path. The parent folder is created if needed.</summary>
    public required string OutputPath { get; init; }

    /// <summary>Constant output frame rate, 15..60.</summary>
    public int FramesPerSecond { get; init; } = 30;

    /// <summary>Video bitrate in bits/s; null = auto (≈ width*height*fps*0.1, bounded 2–40 Mbps).</summary>
    public int? VideoBitrate { get; init; }

    public bool CaptureCursor { get; init; } = true;
    public bool CaptureSystemAudio { get; init; }
    public bool CaptureMicrophone { get; init; }

    /// <summary>Capture endpoint id (see <see cref="AudioDevices.GetMicrophones"/>); null = default capture device.</summary>
    public string? MicrophoneDeviceId { get; init; }

    /// <summary>
    /// Use the GPU (D3D11 surface → hardware MFT) encoding path when it works on this adapter. When false, or when the
    /// GPU path fails its one-time probe, frames are read back and encoded by the Microsoft software H.264 encoder.
    /// </summary>
    public bool PreferHardwareEncoder { get; init; } = true;

    /// <summary>Optional diagnostic sink (non-fatal warnings such as a missing microphone). May be invoked on any thread.</summary>
    public Action<string>? Log { get; init; }
}

public sealed record RecordingResult(string Path, TimeSpan Duration, int Width, int Height, long FileSize, bool HasAudio);

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

/// <summary>Which H.264 encoding path the recorder ended up using.</summary>
public enum VideoEncoderPath
{
    /// <summary>D3D11 textures fed to Media Foundation through a DXGI device manager (hardware MFTs when available).</summary>
    Gpu,

    /// <summary>Frames read back to system memory and encoded by the Microsoft software H.264 encoder.</summary>
    Cpu,
}
