using System.Reflection;
using Hotshot.Capture;
using Hotshot.Recording;

namespace Hotshot.Recording.Harness;

/// <summary>
/// Callable checks for an isolated harness process. Captures only locally created TestWindow
/// instances, never the desktop or another application's windows; writes no media to disk.
/// The existing recording harness entry point deliberately remains unchanged.
/// </summary>
public static class WindowScreenshotChecks
{
    /// <summary>Runs pure conversion/limit checks, followed by synthetic native-window checks.</summary>
    public static async Task RunAsync(CancellationToken cancellationToken = default)
    {
        RunToneMappingChecks();
        RunDimensionChecks();

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await ExpectAsync<OperationCanceledException>(() => WindowScreenshot.CaptureAsync(0, cancelled.Token));
        }

        await ExpectAsync<ArgumentException>(() => WindowScreenshot.CaptureAsync(0, cancellationToken));

        using var target = new TestWindow("Hotshot WGC screenshot pixel target", 661, 501);
        await Task.Delay(300, cancellationToken);
        var bounds = PhysicalBounds(target.Handle);
        nint foreground = WindowScreenshotNative.GetForegroundWindow();
        var captured = await WindowScreenshot.CaptureAsync(target.Handle, cancellationToken);
        CheckImage(captured.Image);
        Check(Math.Abs(captured.Image.Width - (bounds.Right - bounds.Left)) <= 2 &&
            Math.Abs(captured.Image.Height - (bounds.Bottom - bounds.Top)) <= 2,
            $"Physical window dimensions mismatch: image {captured.Image.Width}x{captured.Image.Height}.");
        bool toneMapped = IsToneMapped(captured);
        CheckBlue(Sample(captured.Image, 0.5, 0.5), "center", toneMapped);
        CheckBlue(Sample(captured.Image, 0.2, 0.8), "lower-left", toneMapped);
        CheckBlue(Sample(captured.Image, 0.8, 0.8), "lower-right", toneMapped);
        CheckCaptionOrientation(captured.Image);
        Check(WindowScreenshotNative.GetForegroundWindow() == foreground, "Screenshot activated a window.");
        Check(SameBounds(bounds, PhysicalBounds(target.Handle)), "Screenshot moved or resized its target.");
        Console.WriteLine($"PASS WGC physical pixels, BGRA channel order, top-down rows, opacity, no activation: {captured.Image.Width}x{captured.Image.Height}");
        foreach (string warning in captured.Warnings) Console.WriteLine($"WGC warning: {warning}");
        using (var unaware = new TestWindow("Hotshot mixed DPI-awareness target", 500, 400, -1))
        {
            await Task.Delay(300, cancellationToken);
            var unawareBounds = PhysicalBounds(unaware.Handle);
            var pixels = await WindowScreenshot.CaptureAsync(unaware.Handle, cancellationToken);
            Check(Math.Abs(pixels.Image.Width - (unawareBounds.Right - unawareBounds.Left)) <= 2 &&
                Math.Abs(pixels.Image.Height - (unawareBounds.Bottom - unawareBounds.Top)) <= 2,
                "DPI-unaware HWND was captured in logical rather than physical pixels.");
            CheckBlue(Sample(pixels.Image, 0.5, 0.5), "DPI-unaware target", IsToneMapped(pixels));
            Console.WriteLine("PASS per-monitor-V2 and DPI-unaware HWNDs produce correct physical-pixel sizes on the same display.");
        }
        var monitors = Monitors.GetAll();
        Console.WriteLine($"Hardware probe: {monitors.Count} monitor(s), scale(s) {string.Join(", ", monitors.Select(monitor => monitor.Scale))}; process architecture {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}.");
        if (monitors.Count < 2 || monitors.Select(monitor => monitor.Scale).Distinct().Count() < 2)
            Console.WriteLine("SKIP physical mixed-DPI monitor movement: this desktop does not provide two different-scale displays.");
        if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.Arm64)
            Console.WriteLine("SKIP native ARM64 execution: this machine is not ARM64.");

        await CheckOcclusionAsync(target, cancellationToken);
        CheckStateChangeRejected(target);

        using (var cancellation = new CancellationTokenSource())
        {
            Task<WindowScreenshotResult> pending = WindowScreenshot.CaptureAsync(target.Handle, cancellation.Token);
            cancellation.Cancel();
            await ExpectAsync<OperationCanceledException>(() => pending);
            Console.WriteLine("PASS cancellation during initialization/frame wait");
        }

        if (WindowScreenshotNative.SetWindowDisplayAffinity(target.Handle, 0x11)) // WDA_EXCLUDEFROMCAPTURE
        {
            try
            {
                await ExpectAsync<InvalidOperationException>(() => WindowScreenshot.CaptureAsync(target.Handle, cancellationToken));
                Console.WriteLine("PASS protected synthetic window rejected");
            }
            finally
            {
                WindowScreenshotNative.SetWindowDisplayAffinity(target.Handle, 0);
            }
        }
        else
        {
            Console.WriteLine("SKIP display-affinity check: Windows did not allow it for the synthetic window.");
        }

        WindowScreenshotNative.ShowWindow(target.Handle, 0); // SW_HIDE; no activation.
        await ExpectAsync<InvalidOperationException>(() => WindowScreenshot.CaptureAsync(target.Handle, cancellationToken));
        Console.WriteLine("PASS hidden window rejected without restoring it");

        using (var minimized = new TestWindow("Hotshot WGC minimized target", 400, 300))
        {
            WindowScreenshotNative.ShowWindow(minimized.Handle, 7); // SW_SHOWMINNOACTIVE
            await Task.Delay(100, cancellationToken);
            await ExpectAsync<InvalidOperationException>(() => WindowScreenshot.CaptureAsync(minimized.Handle, cancellationToken));
            Console.WriteLine("PASS minimized window rejected without restoring it");
        }

        nint closedHandle;
        using (var closing = new TestWindow("Hotshot WGC closing target", 400, 300))
        {
            closedHandle = closing.Handle;
            var pending = WindowScreenshot.CaptureAsync(closing.Handle, cancellationToken);
            closing.Close();
            try
            {
                var result = await pending;
                // A frame completed before WM_CLOSE is also a valid outcome of this race.
                CheckBlue(Sample(result.Image, 0.5, 0.5), "closed-window race", IsToneMapped(result));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
            }

        }

        await ExpectAsync<ArgumentException>(() => WindowScreenshot.CaptureAsync(closedHandle, cancellationToken));
        Console.WriteLine("PASS target-close race and stale HWND cleanup");
    }

    /// <summary>Runs without WGC, a GPU, or any windows; useful as a standalone conversion check.</summary>
    public static void RunToneMappingChecks()
    {
        Type type = typeof(WindowScreenshot).Assembly.GetType("Hotshot.Recording.WindowScreenshotToneMapping", throwOnError: true)!;
        MethodInfo encode = type.GetMethod("EncodeSrgb", BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo encodeHalf = type.GetMethod("EncodeHalf", BindingFlags.NonPublic | BindingFlags.Static)!;
        (float Linear, bool Hdr, byte Expected)[] vectors =
        [
            (-1, false, 0),
            (0, false, 0),
            (0.0031308f, false, 10),
            (0.18f, false, 118),
            (0.5f, false, 188),
            (1, false, 255),
            (4, false, 255),
            (0, true, 0),
            (1, true, 188),
            (4, true, 231),
        ];
        foreach (var vector in vectors)
        {
            byte actual = (byte)Invoke(encode, vector.Linear, vector.Hdr)!;
            Check(actual == vector.Expected, $"sRGB({vector.Linear}, HDR={vector.Hdr}): expected {vector.Expected}, got {actual}.");
            byte fromHalf = (byte)Invoke(encodeHalf, (Half)vector.Linear, vector.Hdr)!;
            Check(Math.Abs(fromHalf - vector.Expected) <= 1, $"Float16 lookup conversion disagrees for {vector.Linear}.");
        }

        Expect<ArgumentOutOfRangeException>(() => Invoke(encode, float.NaN, false));
        Expect<ArgumentOutOfRangeException>(() => Invoke(encode, float.PositiveInfinity, true));
        Expect<ArgumentOutOfRangeException>(() => Invoke(encodeHalf, Half.NaN, false));
        Expect<ArgumentOutOfRangeException>(() => Invoke(encodeHalf, Half.PositiveInfinity, true));
        byte previous = 0;
        for (int i = 0; i <= 4096; i++)
        {
            byte next = (byte)Invoke(encode, i / 64f, true)!;
            Check(next >= previous, "Reinhard/sRGB conversion is not monotonic.");
            previous = next;
        }

        Console.WriteLine("PASS scRGB/sRGB transfer, full-frame Reinhard, float16 lookup, clipping, non-finite rejection, monotonicity");
    }

    public static void RunDimensionChecks()
    {
        MethodInfo validate = typeof(WindowScreenshot).GetMethod("ValidateDimensions", BindingFlags.NonPublic | BindingFlags.Static)!;
        Invoke(validate, 1L, 1L);
        Invoke(validate, 8000L, 8000L);
        Expect<InvalidOperationException>(() => Invoke(validate, 0L, 1L));
        Expect<InvalidOperationException>(() => Invoke(validate, -1L, -1L));
        Expect<InvalidOperationException>(() => Invoke(validate, 8001L, 8000L));
        Expect<InvalidOperationException>(() => Invoke(validate, long.MaxValue, long.MaxValue));
        Console.WriteLine("PASS checked 64-megapixel dimension boundary");
    }

    private static async Task CheckOcclusionAsync(TestWindow target, CancellationToken cancellationToken)
    {
        var bounds = PhysicalBounds(target.Handle);
        var center = new Native.POINT { X = (bounds.Left + bounds.Right) / 2, Y = (bounds.Top + bounds.Bottom) / 2 };
        var monitor = new Native.MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        nint monitorHandle = Native.MonitorFromPoint(center, Native.MONITOR_DEFAULTTOPRIMARY);
        if (!Native.GetMonitorInfo(monitorHandle, ref monitor) ||
            center.X < monitor.rcWork.Left || center.X >= monitor.rcWork.Right ||
            center.Y < monitor.rcWork.Top || center.Y >= monitor.rcWork.Bottom)
        {
            Console.WriteLine("SKIP occlusion check: synthetic target center is outside the monitor work area.");
            return;
        }

        using var occluder = new TestWindow("Hotshot WGC red synthetic occluder", 861, 701);
        await Task.Delay(300, cancellationToken);
        const uint NoSize = 0x0001;
        Check(Native.SetWindowPos(occluder.Handle, -1, 0, 0, 0, 0, NoSize | Native.SWP_NOMOVE | Native.SWP_NOACTIVATE),
            "Cannot place the synthetic occluder above the synthetic target.");
        var occluderBounds = PhysicalBounds(occluder.Handle);
        Check(center.X > occluderBounds.Left + 40 && center.X < occluderBounds.Right - 40 &&
            center.Y > occluderBounds.Top + 80 && center.Y < occluderBounds.Bottom - 40,
            "The synthetic occluder does not cover the target's center.");
        PaintRed(occluder.Handle);
        var red = await WindowScreenshot.CaptureAsync(occluder.Handle, cancellationToken);
        double redX = (double)(center.X - occluderBounds.Left) / red.Image.Width;
        double redY = (double)(center.Y - occluderBounds.Top) / red.Image.Height;
        CheckRed(Sample(red.Image, redX, redY), IsToneMapped(red));

        PaintRed(occluder.Handle);
        nint foreground = WindowScreenshotNative.GetForegroundWindow();
        var obscured = await WindowScreenshot.CaptureAsync(target.Handle, cancellationToken);
        CheckBlue(Sample(obscured.Image, 0.5, 0.5), "fully occluded target center", IsToneMapped(obscured));
        Check(WindowScreenshotNative.GetForegroundWindow() == foreground, "Occluded capture activated its target.");
        var stillRed = await WindowScreenshot.CaptureAsync(occluder.Handle, cancellationToken);
        CheckRed(Sample(stillRed.Image, redX, redY), IsToneMapped(stillRed));
        Console.WriteLine("PASS unobscured blue window under a red, harness-owned occluder; no desktop pixels captured");
    }

    private static void PaintRed(nint window)
    {
        Check(WindowScreenshotNative.GetClientRect(window, out var rect), "Cannot read synthetic occluder client bounds.");
        nint dc = WindowScreenshotNative.GetDC(window);
        nint brush = Native.CreateSolidBrush(0x002828E0); // COLORREF: R=224, G=40, B=40.
        try
        {
            Check(dc != 0 && brush != 0, "Cannot paint synthetic occluder.");
            Check(WindowScreenshotNative.FillRect(dc, in rect, brush) != 0, "Synthetic occluder paint failed.");
        }
        finally
        {
            if (brush != 0) WindowScreenshotNative.DeleteObject(brush);
            if (dc != 0) Check(WindowScreenshotNative.ReleaseDC(window, dc) != 0, "Cannot release synthetic occluder device context.");
        }

        System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(WindowScreenshotNative.DwmFlush());
    }

    private static void CheckStateChangeRejected(TestWindow target)
    {
        nint previous = WindowScreenshotNative.SetThreadDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        Check(previous != 0, "Cannot establish physical-pixel coordinates for the target-change check.");
        try
        {
            MethodInfo readIdentity = typeof(WindowScreenshot).GetMethod("ReadIdentity", BindingFlags.NonPublic | BindingFlags.Static)!;
            MethodInfo readState = typeof(WindowScreenshot).GetMethod("ReadState", BindingFlags.NonPublic | BindingFlags.Static)!;
            MethodInfo validate = typeof(WindowScreenshot).GetMethod("ValidateState", BindingFlags.NonPublic | BindingFlags.Static)!;
            object identity = Invoke(readIdentity, target.Handle)!;
            object state = Invoke(readState, target.Handle, identity)!;
            target.Resize(741, 561);
            Expect<InvalidOperationException>(() => Invoke(validate, target.Handle, state));
            Console.WriteLine("PASS changed target rejected instead of cropping/padding a stale frame");
        }
        finally
        {
            WindowScreenshotNative.SetThreadDpiAwarenessContext(previous);
        }
    }

    private static Native.RECT PhysicalBounds(nint window)
    {
        nint previous = WindowScreenshotNative.SetThreadDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        Check(previous != 0, "Cannot establish physical-pixel coordinates in the screenshot harness.");
        try
        {
            return Native.GetExtendedFrameBounds(window);
        }
        finally
        {
            WindowScreenshotNative.SetThreadDpiAwarenessContext(previous);
        }
    }

    private static bool SameBounds(Native.RECT left, Native.RECT right) =>
        left.Left == right.Left && left.Top == right.Top && left.Right == right.Right && left.Bottom == right.Bottom;

    private static void CheckImage(CapturedImage image)
    {
        Check(image.Width > 0 && image.Height > 0 && (long)image.Width * image.Height <= 64_000_000, "Invalid screenshot dimensions.");
        Check(image.Pixels.LongLength == (long)image.Width * image.Height * 4, "Screenshot contains row padding or missing pixels.");
        for (int i = 3; i < image.Pixels.Length; i += 4) Check(image.Pixels[i] == 255, "Screenshot is not opaque.");
    }

    private static (byte B, byte G, byte R) Sample(CapturedImage image, double x, double y)
    {
        int offset = (Math.Clamp((int)(image.Height * y), 0, image.Height - 1) * image.Width +
            Math.Clamp((int)(image.Width * x), 0, image.Width - 1)) * 4;
        return (image.Pixels[offset], image.Pixels[offset + 1], image.Pixels[offset + 2]);
    }

    private static void CheckCaptionOrientation(CapturedImage image)
    {
        bool captionDiffers = false;
        for (int y = 0; y < Math.Max(1, image.Height / 10); y++)
        {
            var pixel = Sample(image, 0.5, (double)y / image.Height);
            if (!IsBlue(pixel) && !IsToneMappedBlue(pixel)) captionDiffers = true;
        }

        Check(captionDiffers, "The title bar was not at the top of the captured image.");
    }

    private static bool IsBlue((byte B, byte G, byte R) pixel) =>
        Math.Abs(pixel.B - 224) <= 3 && Math.Abs(pixel.G - 160) <= 3 && Math.Abs(pixel.R - 40) <= 3;

    private static bool IsToneMappedBlue((byte B, byte G, byte R) pixel) =>
        pixel.B > pixel.G + 15 && pixel.G > pixel.R + 30;

    private static bool IsToneMapped(WindowScreenshotResult result) =>
        result.Warnings.Any(warning => warning.StartsWith("Extended-range/HDR", StringComparison.Ordinal));

    private static void CheckBlue((byte B, byte G, byte R) pixel, string location, bool toneMapped = false) =>
        Check(toneMapped ? IsToneMappedBlue(pixel) : IsBlue(pixel),
            $"{location}: expected synthetic blue (224,160,40 BGRA, tone-mapped={toneMapped}), got {pixel}.");

    private static void CheckRed((byte B, byte G, byte R) pixel, bool toneMapped) =>
        Check(toneMapped ? pixel.R > pixel.B + 30 && Math.Abs(pixel.B - pixel.G) <= 3 :
            Math.Abs(pixel.B - 40) <= 3 && Math.Abs(pixel.G - 40) <= 3 && Math.Abs(pixel.R - 224) <= 3,
            $"Expected synthetic red occluder (40,40,224 BGRA), got {pixel}.");

    private static object? Invoke(MethodInfo method, params object?[] arguments)
    {
        try
        {
            return method.Invoke(null, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static async Task ExpectAsync<T>(Func<Task<WindowScreenshotResult>> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
