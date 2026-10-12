using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hotshot.Recording.Harness;

/// <summary>A plain solid-colour Win32 window on its own message-loop thread, used as a controllable capture target.</summary>
internal sealed unsafe class TestWindow : IDisposable
{
    private const string ClassName = "HotshotHarnessWindow";
    private static bool s_registered;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _created = new();

    public TestWindow(string title, int width, int height, nint dpiContext = 0)
    {
        _thread = new Thread(() =>
        {
            var previous = dpiContext == 0 ? 0 : Native.SetThreadDpiAwarenessContext(dpiContext);
            try { Run(title, width, height); }
            finally { if (previous != 0) Native.SetThreadDpiAwarenessContext(previous); }
        }) { IsBackground = true, Name = "Harness window" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_created.Wait(TimeSpan.FromSeconds(5)) || Handle == 0)
        {
            throw new InvalidOperationException("Could not create the test window.");
        }
    }

    public nint Handle { get; private set; }

    public void Resize(int width, int height) =>
        Native.SetWindowPos(Handle, 0, 0, 0, width, height, Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

    public void Close() => Native.PostMessage(Handle, Native.WM_CLOSE, 0, 0);

    public void KeepVisibleForPixelCheck() =>
        Native.SetWindowPos(Handle, -1, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

    private void Run(string title, int width, int height)
    {
        nint instance = Native.GetModuleHandle(null);
        if (!s_registered)
        {
            fixed (char* className = ClassName)
            {
                var windowClass = new Native.WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(Native.WNDCLASSEXW),
                    lpfnWndProc = &WndProc,
                    hInstance = instance,
                    hCursor = Native.LoadCursor(0, 32512), // IDC_ARROW
                    hbrBackground = Native.CreateSolidBrush(0x00E0A028), // BGR: a mid blue
                    lpszClassName = className,
                };
                Native.RegisterClassEx(&windowClass);
            }

            s_registered = true;
        }

        Handle = Native.CreateWindowEx(0, ClassName, title, Native.WS_OVERLAPPEDWINDOW | Native.WS_VISIBLE, 160, 160, width, height, 0, 0, instance, 0);
        _created.Set();
        if (Handle == 0)
        {
            return;
        }

        Native.MSG msg;
        while (Native.GetMessage(&msg, 0, 0, 0) > 0)
        {
            Native.TranslateMessage(&msg);
            Native.DispatchMessage(&msg);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == 0x02E0) // WM_DPICHANGED
        {
            var bounds = *(Native.RECT*)lParam;
            Native.SetWindowPos(hwnd, 0, bounds.Left, bounds.Top, bounds.Right - bounds.Left,
                bounds.Bottom - bounds.Top, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            return 0;
        }
        if (msg == Native.WM_DESTROY)
        {
            Native.PostQuitMessage(0);
            return 0;
        }

        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (Native.IsWindow(Handle))
        {
            Close();
        }

        _thread.Join(TimeSpan.FromSeconds(2));
        _created.Dispose();
    }
}
