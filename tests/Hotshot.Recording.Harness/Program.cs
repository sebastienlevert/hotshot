using System.Diagnostics;
using Hotshot.Recording;
using Hotshot.Recording.Harness;
using Hotshot.Capture;
using Hotshot.Gif;
using Vortice.MediaFoundation;

const double DurationTolerance = 0.6;
bool windowOnly = args.Contains("--window-only", StringComparer.OrdinalIgnoreCase);

Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
MediaFactory.MFStartup(useLightVersion: false).CheckError();

string outputDirectory = Path.Combine(Path.GetTempPath(), "hotshot-harness");
Directory.CreateDirectory(outputDirectory);
Console.WriteLine($"Output: {outputDirectory}");
Console.WriteLine($"RecordingSupport.IsSupported = {RecordingSupport.IsSupported}");
var microphones = AudioDevices.GetMicrophones();
Console.WriteLine($"Microphones: {(microphones.Count == 0 ? "(none)" : string.Join(", ", microphones.Select(m => m.Name + (m.IsDefault ? " [default]" : ""))))}");

nint primaryMonitor = Native.MonitorFromPoint(default, Native.MONITOR_DEFAULTTOPRIMARY);
var monitorInfo = new Native.MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
Native.GetMonitorInfo(primaryMonitor, ref monitorInfo);
int monitorWidth = monitorInfo.rcMonitor.Right - monitorInfo.rcMonitor.Left;
int monitorHeight = monitorInfo.rcMonitor.Bottom - monitorInfo.rcMonitor.Top;
Console.WriteLine($"Primary monitor: {monitorWidth}x{monitorHeight}");

// Notepad opens a harness-owned file so the recording never shows user documents (e.g. tabs restored from a previous session).
string notepadFile = Path.Combine(outputDirectory, "notepad-target.txt");
var results = new List<(string Name, bool Passed, string Detail)>();
nint notepad = 0;

await Run("1. monitor full, 30 fps, 4 s, no audio", async () =>
{
    var options = Options("monitor-full", new MonitorTarget(primaryMonitor), fps: 30);
    var (result, recorder) = await RecordAsync(options, TimeSpan.FromSeconds(4));
    var stopAgain = await recorder.StopAsync();
    Check(ReferenceEquals(result, stopAgain), "StopAsync is not idempotent");
    await recorder.DisposeAsync();
    var info = Validate(result, monitorWidth & ~1, monitorHeight & ~1, 4, expectAudio: false);
    return $"{Describe(result, recorder)} | {info}";
});

await Run("2. crop 640x481 @60 fps, system+mic audio, 1 s pause (3 s wall)", async () =>
{
    var options = Options("crop-audio-pause", new MonitorTarget(primaryMonitor, new RectInt(100, 100, 640, 481)), fps: 60, systemAudio: true, microphone: true);
    await using var recorder = await ScreenRecorder.StartAsync(options);
    Check(recorder.Width == 640 && recorder.Height == 480, $"expected 640x480 output, got {recorder.Width}x{recorder.Height}");
    _ = Task.Run(async () =>
    {
        for (int i = 0; i < 6; i++)
        {
            Native.MessageBeep(0);
            await Task.Delay(500);
        }
    });

    await Task.Delay(1000);
    recorder.Pause();
    Check(recorder.IsPaused, "IsPaused should be true after Pause()");
    var frozen = recorder.Elapsed;
    await Task.Delay(1000);
    Check(Math.Abs((recorder.Elapsed - frozen).TotalMilliseconds) < 1, "Elapsed advanced while paused");
    recorder.MicrophoneMuted = true;
    recorder.Resume();
    Check(!recorder.IsPaused, "IsPaused should be false after Resume()");
    await Task.Delay(500);
    recorder.MicrophoneMuted = false;
    recorder.SystemAudioMuted = true;
    await Task.Delay(500);
    var result = await recorder.StopAsync();
    var info = Validate(result, 640, 480, 2, expectAudio: true);
    Check(Math.Abs(info.AudioEndSeconds - 2) <= DurationTolerance, $"audio track length {info.AudioEndSeconds:F2}s, expected ~2s");
    return $"{Describe(result, recorder)} | {info}";
});

await Run("3. window target (notepad), 3 s", async () =>
{
    if (!windowOnly) notepad = await LaunchNotepadAsync(notepadFile);
    using var fallback = notepad == 0 ? new TestWindow("Hotshot harness window", 800, 600) : null;
    nint hwnd = notepad != 0 ? notepad : fallback!.Handle;
    string label = notepad != 0 ? "notepad" : "harness window (notepad unavailable)";
    await Task.Delay(300);
    var bounds = Native.GetExtendedFrameBounds(hwnd);
    int expectedWidth = (bounds.Right - bounds.Left) & ~1;
    int expectedHeight = (bounds.Bottom - bounds.Top) & ~1;

    var (result, recorder) = await RecordAsync(Options("window", new WindowTarget(hwnd)), TimeSpan.FromSeconds(3));
    await recorder.DisposeAsync();
    var info = Validate(result, result.Width, result.Height, 3, expectAudio: false);
    Check(Math.Abs(result.Width - expectedWidth) <= 2 && Math.Abs(result.Height - expectedHeight) <= 2,
        $"output {result.Width}x{result.Height} does not match window bounds {expectedWidth}x{expectedHeight}");
    return $"{label} | {Describe(result, recorder)} | {info}";
});

await Run("4. closing the window raises TargetClosed; StopAsync still produces a valid file", async () =>
{
    if (!windowOnly && (notepad == 0 || !Native.IsWindow(notepad)))
    {
        notepad = await LaunchNotepadAsync(notepadFile);
    }

    using var fallback = notepad == 0 ? new TestWindow("Hotshot harness window (close)", 800, 600) : null;
    nint hwnd = notepad != 0 ? notepad : fallback!.Handle;
    string label = notepad != 0 ? "notepad" : "harness window";

    var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var recorder = await ScreenRecorder.StartAsync(Options("window-closed", new WindowTarget(hwnd)));
    var stopwatch = Stopwatch.StartNew();
    recorder.TargetClosed += (_, _) => closed.TrySetResult();
    await Task.Delay(1500);

    // Notepad may ignore WM_CLOSE while it is still restoring tabs, so retry a few times.
    double postedAt = stopwatch.Elapsed.TotalSeconds;
    int attempts = 0;
    for (; attempts < 4 && !closed.Task.IsCompleted; attempts++)
    {
        if (Native.IsWindow(hwnd))
        {
            Native.PostMessage(hwnd, Native.WM_CLOSE, 0, 0);
        }

        await Task.WhenAny(closed.Task, Task.Delay(2000));
    }

    Check(closed.Task.IsCompleted, $"TargetClosed was not raised (window still exists: {Native.IsWindow(hwnd)})");
    double closedAfter = stopwatch.Elapsed.TotalSeconds - postedAt;
    var result = await recorder.StopAsync();
    double wall = stopwatch.Elapsed.TotalSeconds;
    notepad = 0;
    Check(Math.Abs(result.Duration.TotalSeconds - wall) <= DurationTolerance, $"result duration {result.Duration.TotalSeconds:F2}s vs wall clock {wall:F2}s");
    var info = Validate(result, result.Width, result.Height, result.Duration.TotalSeconds, expectAudio: false);
    return $"{label}, TargetClosed {closedAfter:F2}s after WM_CLOSE (attempts: {attempts}) | {Describe(result, recorder)} | {info}";
});

await Run("5. window resized while recording (grow, then shrink) keeps output size, pads with black", async () =>
{
    using var window = new TestWindow("Hotshot harness resize", 800, 600);
    await Task.Delay(300);
    Exception? failure = null;
    await using var recorder = await ScreenRecorder.StartAsync(Options("window-resize", new WindowTarget(window.Handle)));
    recorder.Failed += (_, ex) => failure = ex;
    int width = recorder.Width;
    int height = recorder.Height;
    await Task.Delay(1000);
    window.Resize(1100, 820);
    await Task.Delay(1000);
    window.Resize(500, 360);
    await Task.Delay(1000);
    var result = await recorder.StopAsync();
    Check(failure is null, $"Failed raised: {failure?.Message}");
    var info = Validate(result, width, height, 3, expectAudio: false);

    var initial = DecodeAt(result.Path, 0.5);
    CheckWindowFrame(initial, "t=0.5s");
    Check(IsBlue(initial.Sample(0.9, 0.9)), $"t=0.5s: bottom-right should show the window body, got {initial.Sample(0.9, 0.9)}");
    var grown = DecodeAt(result.Path, 1.6);
    Check(IsBlue(grown.Sample(0.5, 0.5)) && IsBlue(grown.Sample(0.9, 0.9)), $"t=1.6s: grown window should fill the frame, got {grown.Sample(0.5, 0.5)} / {grown.Sample(0.9, 0.9)}");
    var shrunk = DecodeAt(result.Path, 2.6);
    Check(IsBlue(shrunk.Sample(0.3, 0.4)), $"t=2.6s: shrunk window should stay top-left, got {shrunk.Sample(0.3, 0.4)}");
    Check(IsBlack(shrunk.Sample(0.9, 0.9)), $"t=2.6s: area outside the shrunk window should be black, got {shrunk.Sample(0.9, 0.9)}");
    return $"{Describe(result, recorder)} | {info} | pixels: colour/orientation OK, grow OK, shrink pads black";
});

await Run("6. CPU encoder path (PreferHardwareEncoder = false) + system audio", async () =>
{
    using var window = new TestWindow("Hotshot harness CPU path", 661, 501);
    await Task.Delay(300);
    var options = Options("cpu-path", new WindowTarget(window.Handle), fps: 30, systemAudio: true, preferHardware: false);
    var (result, recorder) = await RecordAsync(options, TimeSpan.FromSeconds(2));
    await recorder.DisposeAsync();
    Check(recorder.EncoderPath == VideoEncoderPath.Cpu, $"expected CPU encoder, got {recorder.EncoderPath}");
    Check(result.Width % 2 == 0 && result.Height % 2 == 0, "output size must be even");
    var info = Validate(result, result.Width, result.Height, 2, expectAudio: true);
    CheckWindowFrame(DecodeAt(result.Path, 1.0), "t=1.0s");
    return $"{Describe(result, recorder)} | {info} | pixels: colour/orientation OK";
});

await Run("7. CancelAsync deletes the file", async () =>
{
    var options = Options("cancel", new MonitorTarget(primaryMonitor), fps: 30, systemAudio: true);
    var recorder = await ScreenRecorder.StartAsync(options);
    await Task.Delay(1000);
    await recorder.CancelAsync();
    Check(!File.Exists(options.OutputPath), "output file still exists after CancelAsync");
    await recorder.DisposeAsync();
    return $"{Path.GetFileName(options.OutputPath)} deleted ({recorder.EncoderPath} encoder)";
});

await Run("8. five back-to-back recordings (stability / leaks)", async () =>
{
    var process = Process.GetCurrentProcess();
    Snapshot(process, out int handlesBefore, out long privateBefore);
    var lines = new List<string>();
    for (int i = 0; i < 5; i++)
    {
        bool audio = i % 2 == 0;
        var target = i % 3 == 2
            ? new MonitorTarget(primaryMonitor)
            : new MonitorTarget(primaryMonitor, new RectInt(50 * i, 40 * i, 800 + (2 * i), 600 + i));
        var options = Options($"loop-{i}", target, fps: i % 2 == 0 ? 30 : 60, systemAudio: audio, microphone: audio);
        var (result, recorder) = await RecordAsync(options, TimeSpan.FromSeconds(1.5));
        await recorder.DisposeAsync();
        var info = Validate(result, result.Width, result.Height, 1.5, expectAudio: audio);
        lines.Add($"#{i} {result.Width}x{result.Height}@{options.FramesPerSecond} {info.DurationSeconds:F2}s {(audio ? "A+V" : "V")} {result.FileSize / 1024} KB");
    }

    Snapshot(process, out int handlesAfter, out long privateAfter);
    string leak = $"handles {handlesBefore}->{handlesAfter} ({handlesAfter - handlesBefore:+#;-#;0}), private bytes {privateBefore / 1048576} MB->{privateAfter / 1048576} MB";
    Check(handlesAfter - handlesBefore < 300, $"possible handle leak: {leak}");
    return $"{string.Join("; ", lines)} | {leak}";
});

await Run("9. screenshot PNG and clipboard", async () =>
{
    using var window = new TestWindow("Hotshot screenshot test", 640, 480);
    await Task.Delay(300);
    var bounds = Native.GetExtendedFrameBounds(window.Handle);
    var rect = PixelRect.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
    using var snapshot = ScreenSnapshot.Take(rect);
    var image = snapshot.ToImage();
    var png = await ImageCodec.EncodePngAsync(image);
    string path = Path.Combine(outputDirectory, "screenshot.png");
    await ImageCodec.WriteAtomicAsync(path, png);
    var decoded = await ImageCodec.LoadAsync(path);
    Check(decoded.Width == rect.Width && decoded.Height == rect.Height, "PNG dimensions do not match the capture");
    int center = ((decoded.Height / 2 * decoded.Width) + decoded.Width / 2) * 4;
    Check(decoded.Pixels[center] > decoded.Pixels[center + 2] + 40, "PNG does not contain the blue harness window");
    Check(ClipboardService.SetImage(window.Handle, image, png), "ClipboardService.SetImage failed");
    Check(Native.IsClipboardFormatAvailable(8), "CF_DIB is missing from the clipboard");
    Check(Native.IsClipboardFormatAvailable(Native.RegisterClipboardFormat("PNG")), "PNG clipboard format is missing");
    return $"PNG {decoded.Width}x{decoded.Height}, {png.Length} bytes; clipboard DIB+PNG verified";
});

await Run("10. MP4 to GIF end-to-end", async () =>
{
    string input = Path.Combine(outputDirectory, "cpu-path.mp4");
    string output = Path.Combine(outputDirectory, "recording.gif");
    var result = await GifConverter.ConvertAsync(input, output, new GifOptions { MaxWidth = 320, FramesPerSecond = 10 });
    var bytes = await File.ReadAllBytesAsync(output);
    Check(bytes.AsSpan(0, 6).SequenceEqual("GIF89a"u8), "GIF header is invalid");
    Check(result.FrameCount > 0 && result.Width == 320 && result.Height > 0, "GIF dimensions or frame count are invalid");
    Check(Math.Abs(result.Duration.TotalSeconds - 2) <= DurationTolerance, "GIF duration differs from the recording");
    Check(result.FileSize == bytes.Length && bytes[^1] == 0x3B, "GIF is incomplete");
    Check(File.Exists(input), "Conversion removed the original MP4");
    return $"{result.Width}x{result.Height}, {result.FrameCount} frames, {result.Duration.TotalSeconds:F2}s; original MP4 preserved";
});

await Run("11. paused target close and cancellation", async () =>
{
    using var window = new TestWindow("Hotshot paused recording test", 640, 480);
    var options = Options("paused-close", new WindowTarget(window.Handle));
    await using var recorder = await ScreenRecorder.StartAsync(options);
    var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    recorder.TargetClosed += (_, _) => closed.TrySetResult();
    await Task.Delay(500);
    recorder.Pause();
    var frozen = recorder.Elapsed;
    await Task.Delay(500);
    Check(Math.Abs((recorder.Elapsed - frozen).TotalMilliseconds) < 1, "Elapsed advanced while paused");
    window.Close();
    await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
    await recorder.CancelAsync();
    Check(!File.Exists(options.OutputPath), "Cancelled paused recording was not deleted");
    return "Paused clock stays frozen, TargetClosed fires while paused, CancelAsync removes the MP4";
});

if (notepad != 0 && Native.IsWindow(notepad))
{
    Native.PostMessage(notepad, Native.WM_CLOSE, 0, 0);
}

MediaFactory.MFShutdown();
Console.WriteLine();
Console.WriteLine("==== Summary ====");
foreach (var (name, passed, detail) in results)
{
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")}  {name}");
    Console.WriteLine($"      {detail}");
}

return results.All(r => r.Passed) ? 0 : 1;

async Task Run(string name, Func<Task<string>> scenario)
{
    if (windowOnly && (name.StartsWith("1.") || name.StartsWith("2.") || name.StartsWith("7.") || name.StartsWith("8.")))
    {
        Console.WriteLine($"SKIP {name} (--window-only)");
        return;
    }

    Console.WriteLine();
    Console.WriteLine($"=== {name}");
    try
    {
        string detail = await scenario();
        results.Add((name, true, detail));
        Console.WriteLine($"PASS {detail}");
    }
    catch (Exception ex)
    {
        results.Add((name, false, ex.Message));
        Console.WriteLine($"FAIL {ex}");
    }
}

RecordingOptions Options(string name, RecordingTarget target, int fps = 30, bool systemAudio = false, bool microphone = false, bool preferHardware = true)
{
    string path = Path.Combine(outputDirectory, $"{name}.mp4");
    if (File.Exists(path))
    {
        File.Delete(path);
    }

    return new RecordingOptions
    {
        Target = target,
        OutputPath = path,
        FramesPerSecond = fps,
        CaptureSystemAudio = systemAudio,
        CaptureMicrophone = microphone,
        PreferHardwareEncoder = preferHardware,
        Log = message => Console.WriteLine($"   [recorder] {message}"),
    };
}

static async Task<(RecordingResult Result, ScreenRecorder Recorder)> RecordAsync(RecordingOptions options, TimeSpan duration)
{
    var startTime = Stopwatch.StartNew();
    var recorder = await ScreenRecorder.StartAsync(options);
    Console.WriteLine($"   StartAsync took {startTime.ElapsedMilliseconds} ms");
    Exception? failure = null;
    recorder.Failed += (_, ex) => failure = ex;
    await Task.Delay(duration);
    var result = await recorder.StopAsync();
    Check(failure is null, $"Failed raised: {failure}");
    return (result, recorder);
}

MediaInfo Validate(RecordingResult result, int expectedWidth, int expectedHeight, double expectedSeconds, bool expectAudio)
{
    Check(File.Exists(result.Path), $"{result.Path} does not exist");
    Check(result.FileSize > 0 && result.FileSize == new FileInfo(result.Path).Length, "FileSize does not match the file on disk");
    var info = MediaInspector.Inspect(result.Path);
    Check(info.Width == expectedWidth && info.Height == expectedHeight, $"video is {info.Width}x{info.Height}, expected {expectedWidth}x{expectedHeight}");
    Check(result.Width == info.Width && result.Height == info.Height, "RecordingResult size differs from the file");
    Check(Math.Abs(info.DurationSeconds - expectedSeconds) <= DurationTolerance, $"duration {info.DurationSeconds:F2}s, expected {expectedSeconds:F2}s ±{DurationTolerance}");
    Check(info.HasAudio == expectAudio, $"audio stream present = {info.HasAudio}, expected {expectAudio}");
    Check(result.HasAudio == expectAudio, $"RecordingResult.HasAudio = {result.HasAudio}, expected {expectAudio}");
    Check(info.VideoSamples > 0, "no video samples");
    return info;
}

static DecodedFrame DecodeAt(string path, double seconds) =>
    MediaInspector.DecodeFrame(path, seconds) ?? throw new InvalidOperationException($"no frame decoded at {seconds}s");

// The harness window is a mid blue (B=224, G=160, R=40) under a title bar: checks channel order and that frames are not flipped.
static void CheckWindowFrame(DecodedFrame frame, string label)
{
    var body = frame.Sample(0.5, 0.5);
    var titleBar = frame.Sample(0.5, 0.012, radius: 1);
    Check(IsBlue(body), $"{label}: window body should be blue (B≈224,G≈160,R≈40), got {body}");
    Check(!IsBlue(titleBar), $"{label}: top rows should be the title bar, got {titleBar} (frame flipped?)");
}

static bool IsBlue((int B, int G, int R) c) => c.B > 170 && c.G is > 110 and < 210 && c.R < 100;

static bool IsBlack((int B, int G, int R) c) => c.B < 25 && c.G < 25 && c.R < 25;

static string Describe(RecordingResult result, ScreenRecorder recorder)
{
    string warnings = recorder.Warnings.Count == 0 ? "" : $", warnings: [{string.Join(" / ", recorder.Warnings)}]";
    return $"{recorder.EncoderPath} encoder, result {result.Width}x{result.Height} {result.Duration.TotalSeconds:F2}s {result.FileSize / 1024} KB, dropped {recorder.FramesDropped}{warnings}";
}

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void Snapshot(Process process, out int handles, out long privateBytes)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    process.Refresh();
    handles = process.HandleCount;
    privateBytes = process.PrivateMemorySize64;
}

static async Task<nint> LaunchNotepadAsync(string file)
{
    File.WriteAllText(file, "Hotshot recording harness target window.\r\nThis file is safe to delete.\r\n");
    string fileName = Path.GetFileName(file);
    var before = NotepadWindows();
    try
    {
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{file}\"") { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   notepad.exe could not be started: {ex.Message}");
        return 0;
    }

    var stopwatch = Stopwatch.StartNew();
    while (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
    {
        await Task.Delay(100);
        foreach (var hwnd in NotepadWindows().Except(before))
        {
            // Wait until our file is the active tab so nothing else is visible in the recording.
            if (Native.GetWindowTitle(hwnd).Contains(fileName, StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(700);
                return hwnd;
            }
        }
    }

    foreach (var stray in NotepadWindows().Except(before))
    {
        Native.PostMessage(stray, Native.WM_CLOSE, 0, 0);
    }

    Console.WriteLine("   No new notepad window showing the harness file appeared; using a harness window instead.");
    return 0;
}

static HashSet<nint> NotepadWindows()
{
    var windows = new HashSet<nint>();
    nint hwnd = 0;
    while ((hwnd = Native.FindWindowEx(0, hwnd, "Notepad", null)) != 0)
    {
        if (Native.IsWindowVisible(hwnd))
        {
            windows.Add(hwnd);
        }
    }

    return windows;
}
