using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Hotshot.Capture;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using WinRT;

namespace Hotshot.Recording;

public sealed record WindowScreenshotResult(CapturedImage Image, string[] Warnings);

/// <summary>Captures one unobscured, physical-pixel window frame with Windows.Graphics.Capture.</summary>
/// <remarks>
/// Does not activate, move, or restore the target. Hidden, minimized, cloaked, closed, changing,
/// and known display-affinity-protected targets fail; there is no visible-desktop/GDI fallback.
/// Windows' capture border and protected-content enforcement remain enabled. Borderless
/// consent is not requested. Windows may blank other protected content. Output is an opaque
/// SDR rendition of a float16 scRGB frame; transparent window edges are matted onto black.
/// </remarks>
public static class WindowScreenshot
{
    private const long MaximumPixels = 64_000_000;
    private const int PollMilliseconds = 20;
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Captures a top-level HWND on a dedicated MTA thread. The first-frame/GPU-readback wait
    /// is limited to five seconds; cancellation and target changes are checked throughout.
    /// Completion is published only after native resources have been torn down.
    /// </summary>
    public static Task<WindowScreenshotResult> CaptureAsync(nint window, CancellationToken cancellationToken) =>
        CaptureAsync(window, includeCursor: false, cancellationToken);

    /// <summary>
    /// Captures a top-level HWND on a dedicated MTA thread. The first-frame/GPU-readback wait
    /// is limited to five seconds; cancellation and target changes are checked throughout.
    /// Completion is published only after native resources have been torn down.
    /// </summary>
    public static Task<WindowScreenshotResult> CaptureAsync(nint window, bool includeCursor = false, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<WindowScreenshotResult>(cancellationToken);
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return Task.FromException<WindowScreenshotResult>(
                new PlatformNotSupportedException("Window screenshots require Windows 10 version 2004 or later and Windows.Graphics.Capture."));
        }

        // Bind the request to the caller's target before handing it to another thread.
        WindowIdentity identity;
        try
        {
            identity = ReadIdentity(window);
        }
        catch (Exception exception)
        {
            return Task.FromException<WindowScreenshotResult>(exception);
        }

        var completion = new TaskCompletionSource<WindowScreenshotResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => Run(window, identity, completion, includeCursor, cancellationToken))
        {
            IsBackground = true,
            Name = "Hotshot window screenshot",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return completion.Task;
    }

    private static void Run(nint window, WindowIdentity identity,
        TaskCompletionSource<WindowScreenshotResult> completion, bool includeCursor, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Marshal.ThrowExceptionForHR(NativeWindowScreenshot.RoInitialize(1)); // RO_INIT_MULTITHREADED
            WindowScreenshotResult result;
            try
            {
                nint previousDpi = NativeWindowScreenshot.SetThreadDpiAwarenessContext(NativeWindowScreenshot.PerMonitorAwareV2);
                if (previousDpi == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot establish physical-pixel window capture coordinates.");
                }

                try
                {
                    result = CaptureCore(window, identity, includeCursor, cancellationToken);
                }
                finally
                {
                    NativeWindowScreenshot.SetThreadDpiAwarenessContext(previousDpi);
                }
            }
            finally
            {
                NativeWindowScreenshot.RoUninitialize();
            }

            cancellationToken.ThrowIfCancellationRequested();
            completion.TrySetResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            else
            {
                completion.TrySetException(exception);
            }
        }
    }

    private static WindowScreenshotResult CaptureCore(nint window, WindowIdentity identity, bool includeCursor, CancellationToken cancellationToken)
    {
        bool supported;
        try
        {
            supported = GraphicsCaptureSession.IsSupported();
        }
        catch (Exception exception)
        {
            throw new PlatformNotSupportedException("Windows.Graphics.Capture support could not be established; no desktop-capture fallback was used.", exception);
        }

        if (!supported)
        {
            throw new PlatformNotSupportedException("Windows.Graphics.Capture is unavailable on this system; no desktop-capture fallback was used.");
        }

        WindowState expected = ReadState(window, identity);
        using var device = CaptureInterop.CreateD3DDevice();
        using var context = device.ImmediateContext;
        var winrtDevice = CaptureInterop.CreateDirect3DDevice(device);
        GraphicsCaptureItem? item = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateState(window, expected);
            try
            {
                item = CaptureInterop.CreateItemForWindow(window);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException("Windows.Graphics.Capture could not open the target window. It may be closed, protected, or access-denied; no fallback was used.", exception);
            }

            var size = item.Size;
            ValidateDimensions(size.Width, size.Height);
            ValidateState(window, expected);

            // Always request float16: an 8-bit WGC pool can irreversibly clip HDR before readback.
            Direct3D11CaptureFramePool pool;
            try
            {
                pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    winrtDevice, DirectXPixelFormat.R16G16B16A16Float, 2, size);
            }
            catch (Exception exception)
            {
                throw new NotSupportedException("Float16 scRGB window capture is unavailable. An SDR-only fallback was not used because it could clip HDR content.", exception);
            }

            using var frameReady = new AutoResetEvent(false);
            using var targetClosed = new ManualResetEvent(false);
            var callbackGate = new object();
            bool callbacksActive = true;
            Windows.Foundation.TypedEventHandler<Direct3D11CaptureFramePool, object> arrived = (_, _) =>
            {
                lock (callbackGate)
                {
                    if (callbacksActive) frameReady.Set();
                }
            };
            Windows.Foundation.TypedEventHandler<GraphicsCaptureItem, object> closed = (_, _) =>
            {
                lock (callbackGate)
                {
                    if (callbacksActive) targetClosed.Set();
                }
            };
            GraphicsCaptureSession? session = null;
            try
            {
                pool.FrameArrived += arrived;
                item.Closed += closed;
                session = pool.CreateCaptureSession(item);
                session.IsCursorCaptureEnabled = includeCursor;
                // Deliberately do not set IsBorderRequired=false without permission-gated consent.
                CheckTarget(window, expected, item, size, targetClosed, cancellationToken);
                var elapsed = Stopwatch.StartNew();
                try
                {
                    session.StartCapture();
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException("Windows.Graphics.Capture could not start for the target window. Windows permissions and capture protection remain enforced; no fallback was used.", exception);
                }
                var waits = new WaitHandle[] { frameReady, targetClosed, cancellationToken.WaitHandle };

                while (true)
                {
                    CheckTarget(window, expected, item, size, targetClosed, cancellationToken);
                    ThrowIfTimedOut(elapsed);
                    var frame = pool.TryGetNextFrame();
                    if (frame is not null)
                    {
                        try
                        {
                            if (frame.ContentSize.Width != size.Width || frame.ContentSize.Height != size.Height)
                            {
                                throw TargetChanged("the captured frame size changed");
                            }

                            var surface = frame.Surface;
                            try
                            {
                                using var source = CaptureInterop.GetTexture(surface);
                                var result = ReadPixels(device, context, source, size, elapsed,
                                    () => CheckTarget(window, expected, item, size, targetClosed, cancellationToken),
                                    cancellationToken);
                                CheckTarget(window, expected, item, size, targetClosed, cancellationToken);
                                return result;
                            }
                            finally
                            {
                                ReleaseProjection(surface);
                            }
                        }
                        finally
                        {
                            CloseProjection(frame);
                        }
                    }

                    int remaining = Math.Max(1, (int)(FrameTimeout - elapsed.Elapsed).TotalMilliseconds);
                    WaitHandle.WaitAny(waits, Math.Min(PollMilliseconds, remaining));
                }
            }
            finally
            {
                // A late free-threaded callback may still run after unsubscription. Only event
                // signaling happens there; the gate prevents touching disposed wait handles.
                lock (callbackGate)
                {
                    callbacksActive = false;
                }

                try
                {
                    pool.FrameArrived -= arrived;
                }
                finally
                {
                    try
                    {
                        item.Closed -= closed;
                    }
                    finally
                    {
                        try
                        {
                            if (session is not null) CloseProjection(session);
                        }
                        finally
                        {
                            CloseProjection(pool);
                        }
                    }
                }
            }
        }
        finally
        {
            try
            {
                ReleaseProjection(item);
            }
            finally
            {
                try
                {
                    if (winrtDevice is IDisposable disposable) disposable.Dispose();
                }
                finally
                {
                    ReleaseProjection(winrtDevice);
                }
            }
        }
    }

    private static unsafe WindowScreenshotResult ReadPixels(ID3D11Device device, ID3D11DeviceContext context,
        ID3D11Texture2D source, SizeInt32 size, Stopwatch elapsed, Action checkTarget, CancellationToken cancellationToken)
    {
        var description = source.Description;
        if (description.Format != Format.R16G16B16A16_Float ||
            description.Width < (uint)size.Width || description.Height < (uint)size.Height ||
            description.SampleDescription.Count != 1)
        {
            throw new NotSupportedException("WGC returned an unexpected surface; float16 scRGB readback is required.");
        }

        var stagingDescription = new Texture2DDescription(Format.R16G16B16A16_Float,
            (uint)size.Width, (uint)size.Height, 1, 1, BindFlags.None, ResourceUsage.Staging,
            CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None);
        using var staging = device.CreateTexture2D(stagingDescription);
        context.CopySubresourceRegion(staging, 0, 0, 0, 0, source, 0, new Box(0, 0, 0, size.Width, size.Height, 1));

        MappedSubresource mapped;
        while (true)
        {
            checkTarget();
            ThrowIfTimedOut(elapsed);
            var status = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out mapped);
            if (status.Success) break;
            if (status.Code != NativeWindowScreenshot.WasStillDrawing)
            {
                status.CheckError();
            }

            cancellationToken.WaitHandle.WaitOne(PollMilliseconds);
        }

        try
        {
            int sourceRowBytes = checked(size.Width * 8);
            if (mapped.DataPointer == 0 || mapped.RowPitch < sourceRowBytes)
            {
                throw new InvalidDataException("WGC readback returned an invalid row pitch or pixel buffer.");
            }

            bool extendedRange = false;
            bool negativeGamut = false;
            for (int y = 0; y < size.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((y & 31) == 0) checkTarget();
                var row = new ReadOnlySpan<Half>((byte*)mapped.DataPointer + (long)y * mapped.RowPitch, checked(size.Width * 4));
                var range = WindowScreenshotToneMapping.AnalyzeRow(row);
                extendedRange |= range.ExtendedRange;
                negativeGamut |= range.NegativeGamut;
            }

            var pixels = new byte[checked(size.Width * size.Height * 4)];
            for (int y = 0; y < size.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((y & 31) == 0) checkTarget();
                var row = new ReadOnlySpan<Half>((byte*)mapped.DataPointer + (long)y * mapped.RowPitch, checked(size.Width * 4));
                int destination = checked(y * size.Width * 4);
                WindowScreenshotToneMapping.ConvertRow(row, pixels.AsSpan(destination, checked(size.Width * 4)), extendedRange);
            }

            List<string> warnings = [];
            if (extendedRange)
            {
                warnings.Add("Extended-range/HDR scRGB was tone-mapped with per-channel Reinhard x/(1+x) to 8-bit sRGB. Original HDR brightness and gamut are not preserved.");
            }

            if (negativeGamut)
            {
                warnings.Add("Out-of-sRGB-gamut negative color components were clipped in the SDR screenshot.");
            }

            return new WindowScreenshotResult(new CapturedImage(size.Width, size.Height, pixels), warnings.ToArray());
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    private static void CheckTarget(nint window, WindowState expected, GraphicsCaptureItem item, SizeInt32 size,
        WaitHandle targetClosed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (targetClosed.WaitOne(0))
        {
            throw new InvalidOperationException("The target window closed before the screenshot completed.");
        }

        ValidateState(window, expected);
        if (item.Size.Width != size.Width || item.Size.Height != size.Height)
        {
            throw TargetChanged("the capture item size changed");
        }
    }

    private static WindowIdentity ReadIdentity(nint window)
    {
        if (window == 0 || !NativeWindowScreenshot.IsWindow(window) ||
            NativeWindowScreenshot.GetAncestor(window, NativeWindowScreenshot.RootWindow) != window)
        {
            throw new ArgumentException("The screenshot target must be a valid top-level window.", nameof(window));
        }

        uint threadId = NativeWindowScreenshot.GetWindowThreadProcessId(window, out uint processId);
        nuint classAtom = NativeWindowScreenshot.GetClassLongPtr(window, NativeWindowScreenshot.ClassAtom);
        if (threadId == 0 || processId == 0 || classAtom == 0)
        {
            throw new InvalidOperationException("The target window closed while its identity was being checked.");
        }

        return new WindowIdentity(processId, threadId, classAtom);
    }

    private static WindowState ReadState(nint window, WindowIdentity identity)
    {
        if (ReadIdentity(window) != identity) throw TargetChanged("the HWND identity changed");
        if (!NativeWindowScreenshot.IsWindowVisible(window))
        {
            throw new InvalidOperationException("The target window is hidden; window screenshots do not show or activate it.");
        }

        if (NativeWindowScreenshot.IsIconic(window))
        {
            throw new InvalidOperationException("The target window is minimized; window screenshots do not restore it.");
        }

        if (NativeWindowScreenshot.DwmGetWindowAttributeUInt32(window, NativeWindowScreenshot.Cloaked, out uint cloaked, sizeof(uint)) >= 0 && cloaked != 0)
        {
            throw new InvalidOperationException("The target window is cloaked and cannot provide a current screenshot.");
        }

        if (NativeWindowScreenshot.GetWindowDisplayAffinity(window, out uint affinity) && affinity != 0)
        {
            throw new InvalidOperationException("The target window is protected from capture by Windows display affinity.");
        }

        if (!NativeWindowScreenshot.GetWindowRect(window, out var bounds))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not read the target window bounds.");
        }

        ValidateDimensions((long)bounds.Right - bounds.Left, (long)bounds.Bottom - bounds.Top);
        if (NativeWindowScreenshot.DwmGetWindowAttribute(window, NativeWindowScreenshot.ExtendedFrameBounds,
            out var visibleBounds, (uint)Marshal.SizeOf<NativeWindowScreenshot.Rect>()) < 0)
        {
            visibleBounds = bounds;
        }

        uint dpi = NativeWindowScreenshot.GetDpiForWindow(window);
        if (dpi == 0) throw TargetChanged("the target DPI became unavailable");
        return new WindowState(identity, bounds, visibleBounds, dpi);
    }

    private static void ValidateState(nint window, WindowState expected)
    {
        if (ReadState(window, expected.Identity) != expected)
        {
            throw TargetChanged("the window moved, resized, or changed DPI");
        }
    }

    private static void ValidateDimensions(long width, long height)
    {
        if (width <= 0 || height <= 0 || width > MaximumPixels || height > MaximumPixels ||
            checked(width * height) > MaximumPixels)
        {
            throw new InvalidOperationException($"The target dimensions {width}x{height} are invalid or exceed the 64-megapixel screenshot limit.");
        }
    }

    private static InvalidOperationException TargetChanged(string detail) =>
        new($"The screenshot target changed ({detail}); no potentially stale or mismatched frame was returned. Retry with a stable window.");

    private static void ThrowIfTimedOut(Stopwatch elapsed)
    {
        if (elapsed.Elapsed >= FrameTimeout)
        {
            throw new TimeoutException("No usable WGC window frame/readback arrived within five seconds; no desktop-capture fallback was used.");
        }
    }

    private static void ReleaseProjection(object? projection)
    {
        if (projection is IWinRTObject winrtObject)
        {
            winrtObject.NativeObject.Dispose();
        }
    }

    private static void CloseProjection(IDisposable projection)
    {
        try
        {
            projection.Dispose();
        }
        finally
        {
            ReleaseProjection(projection);
        }
    }

    private readonly record struct WindowIdentity(uint ProcessId, uint ThreadId, nuint ClassAtom);
    private readonly record struct WindowState(WindowIdentity Identity, NativeWindowScreenshot.Rect Bounds,
        NativeWindowScreenshot.Rect VisibleBounds, uint Dpi);
}
