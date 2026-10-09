using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Hotshot.Recording;

/// <summary>
/// Windows.Graphics.Capture session that keeps <see cref="Latest"/> (an output-sized BGRA texture) up to date
/// with the cropped content of the most recent frame. All immediate-context work happens under the shared GPU lock.
/// </summary>
internal sealed class FrameCapture : IDisposable
{
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
    private const int BufferCount = 2;

    private readonly ID3D11DeviceContext _context;
    private readonly object _gpuLock;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly GraphicsCaptureItem _item;
    private readonly ID3D11RenderTargetView _latestView;
    private readonly int _sourceX;
    private readonly int _sourceY;
    private readonly TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Windows.Foundation.TypedEventHandler<Direct3D11CaptureFramePool, object> _frameArrivedHandler;
    private readonly Windows.Foundation.TypedEventHandler<GraphicsCaptureItem, object> _closedHandler;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _poolSize;
    private bool _stopped;
    private bool _disposed;

    public FrameCapture(ID3D11Device device, ID3D11DeviceContext context, object gpuLock, IDirect3DDevice winrtDevice, GraphicsCaptureItem item, RectInt? crop)
    {
        _context = context;
        _gpuLock = gpuLock;
        _winrtDevice = winrtDevice;
        _item = item;

        var size = item.Size;
        if (size.Width < 2 || size.Height < 2)
        {
            throw new InvalidOperationException($"The capture target has an unusable size ({size.Width}x{size.Height}); is it minimized?");
        }

        var source = new RectInt(0, 0, size.Width, size.Height);
        if (crop is { } c)
        {
            int left = Math.Clamp(c.X, 0, size.Width);
            int top = Math.Clamp(c.Y, 0, size.Height);
            int right = Math.Clamp(c.X + c.Width, 0, size.Width);
            int bottom = Math.Clamp(c.Y + c.Height, 0, size.Height);
            source = new RectInt(left, top, right - left, bottom - top);
        }

        Width = source.Width & ~1;
        Height = source.Height & ~1;
        if (Width < 2 || Height < 2)
        {
            throw new ArgumentException($"The crop rectangle {crop} does not intersect the target ({size.Width}x{size.Height}).", nameof(crop));
        }

        _sourceX = source.X;
        _sourceY = source.Y;

        var description = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)Width, (uint)Height, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None);
        Latest = device.CreateTexture2D(description);
        _latestView = device.CreateRenderTargetView(Latest);
        lock (_gpuLock)
        {
            _context.ClearRenderTargetView(_latestView, new Color4(0, 0, 0, 1));
        }

        _frameArrivedHandler = OnFrameArrived;
        _closedHandler = OnItemClosed;
    }

    /// <summary>Output-sized copy of the latest captured content. Only touch under the GPU lock.</summary>
    public ID3D11Texture2D Latest { get; }

    public int Width { get; }
    public int Height { get; }
    public long FramesArrived { get; private set; }

    public Task FirstFrame => _firstFrame.Task;

    /// <summary>Raised (on a WGC thread) when the item is closed: window destroyed or monitor removed.</summary>
    public event Action? Closed;

    /// <summary>Raised (on a WGC thread) when processing a frame throws.</summary>
    public event Action<Exception>? Error;

    public void Start(bool captureCursor)
    {
        _poolSize = _item.Size;
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, PixelFormat, BufferCount, _poolSize);
        _framePool.FrameArrived += _frameArrivedHandler;
        _item.Closed += _closedHandler;

        _session = _framePool.CreateCaptureSession(_item);
        try
        {
            _session.IsCursorCaptureEnabled = captureCursor;
        }
        catch
        {
            // Cursor toggling requires 19041+; leave the default.
        }

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348) &&
            ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
        {
            try
            {
                _session.IsBorderRequired = false;
            }
            catch
            {
                // Borderless access not granted: the yellow border stays.
            }
        }

        _session.StartCapture();
    }

    /// <summary>Stops the WGC session. After this returns no frame callback touches GPU resources.</summary>
    public void Stop()
    {
        lock (_gpuLock)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
        }

        try
        {
            _item.Closed -= _closedHandler;
        }
        catch
        {
        }

        if (_framePool is not null)
        {
            try
            {
                _framePool.FrameArrived -= _frameArrivedHandler;
            }
            catch
            {
            }
        }

        _session?.Dispose();
        _session = null;
        _framePool?.Dispose();
        _framePool = null;
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args) => Closed?.Invoke();

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            var contentSize = frame.ContentSize;
            lock (_gpuLock)
            {
                if (_stopped)
                {
                    return;
                }

                using var texture = CaptureInterop.GetTexture(frame.Surface);
                CopyToLatest(texture, contentSize);
                FramesArrived++;
            }

            _firstFrame.TrySetResult();

            if (contentSize.Width > 0 && contentSize.Height > 0 &&
                (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height))
            {
                _poolSize = contentSize;
                lock (_gpuLock)
                {
                    if (!_stopped)
                    {
                        sender.Recreate(_winrtDevice, PixelFormat, BufferCount, contentSize);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            bool stopped;
            lock (_gpuLock)
            {
                stopped = _stopped;
            }

            if (!stopped)
            {
                Error?.Invoke(ex);
            }
        }
    }

    private void CopyToLatest(ID3D11Texture2D texture, SizeInt32 contentSize)
    {
        var description = texture.Description;
        int availableWidth = Math.Min(contentSize.Width, (int)description.Width);
        int availableHeight = Math.Min(contentSize.Height, (int)description.Height);
        int right = Math.Min(_sourceX + Width, availableWidth);
        int bottom = Math.Min(_sourceY + Height, availableHeight);
        int copyWidth = right - _sourceX;
        int copyHeight = bottom - _sourceY;

        // Content shrank (window resized / monitor mode change): keep it top-left aligned on black.
        if (copyWidth < Width || copyHeight < Height)
        {
            _context.ClearRenderTargetView(_latestView, new Color4(0, 0, 0, 1));
        }

        if (copyWidth > 0 && copyHeight > 0)
        {
            _context.CopySubresourceRegion(Latest, 0, 0, 0, 0, texture, 0, new Box(_sourceX, _sourceY, 0, right, bottom, 1));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        lock (_gpuLock)
        {
            _latestView.Dispose();
            Latest.Dispose();
        }
    }
}
