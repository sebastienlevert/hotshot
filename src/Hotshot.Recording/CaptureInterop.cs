using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Hotshot.Recording;

/// <summary>Bridges Windows.Graphics.Capture (CsWinRT) and Vortice D3D11.</summary>
internal static unsafe class CaptureInterop
{
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    // IGraphicsCaptureItemInterop vtable: IUnknown (0-2), CreateForWindow (3), CreateForMonitor (4).
    private const int CreateForWindowSlot = 3;
    private const int CreateForMonitorSlot = 4;

    public static GraphicsCaptureItem CreateItemForMonitor(nint hmonitor) => CreateItem(hmonitor, CreateForMonitorSlot);

    public static GraphicsCaptureItem CreateItemForWindow(nint hwnd) => CreateItem(hwnd, CreateForWindowSlot);

    private static GraphicsCaptureItem CreateItem(nint handle, int slot)
    {
        using var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        Marshal.ThrowExceptionForHR(factory.TryAs(IID_IGraphicsCaptureItemInterop, out nint interop));
        try
        {
            Guid iid = IID_IGraphicsCaptureItem;
            nint itemPtr = 0;
            var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(void***)interop)[slot];
            Marshal.ThrowExceptionForHR(create(interop, handle, &iid, &itemPtr));
            try
            {
                return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
            }
            finally
            {
                Marshal.Release(itemPtr);
            }
        }
        finally
        {
            Marshal.Release(interop);
        }
    }

    public static IDirect3DDevice CreateDirect3DDevice(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(Native.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out nint inspectable));
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    /// <summary>Returns the frame's backing texture (caller disposes the wrapper; the frame still owns the surface).</summary>
    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        nint surfacePtr = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfacePtr, in IID_IDirect3DDxgiInterfaceAccess, out nint access));
            try
            {
                Guid iid = typeof(ID3D11Texture2D).GUID;
                nint texture = 0;
                // IDirect3DDxgiInterfaceAccess::GetInterface is the first method after IUnknown.
                var getInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(void***)access)[3];
                Marshal.ThrowExceptionForHR(getInterface(access, &iid, &texture));
                return new ID3D11Texture2D(texture);
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(surfacePtr);
        }
    }

    /// <summary>Creates a BGRA + video capable device on the default adapter, degrading to fewer flags and finally WARP.</summary>
    public static ID3D11Device CreateD3DDevice()
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        var attempts = new (DriverType Driver, DeviceCreationFlags Flags)[]
        {
            (DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport),
            (DriverType.Hardware, DeviceCreationFlags.BgraSupport),
            (DriverType.Warp, DeviceCreationFlags.BgraSupport),
        };

        Exception? last = null;
        foreach (var (driver, flags) in attempts)
        {
            var hr = D3D11.D3D11CreateDevice(0, driver, flags, levels, out ID3D11Device? device);
            if (hr.Success && device is not null)
            {
                using (var multithread = device.QueryInterfaceOrNull<ID3D11Multithread>())
                {
                    // Required: Media Foundation and the WGC callback thread use the device concurrently with our pumps.
                    multithread?.SetMultithreadProtected(true);
                }

                return device;
            }

            last = Marshal.GetExceptionForHR(hr.Code);
        }

        throw new InvalidOperationException("Unable to create a Direct3D 11 device.", last);
    }

    public static long GetAdapterLuid(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        var luid = adapter.Description.Luid;
        return ((long)luid.HighPart << 32) | luid.LowPart;
    }
}
