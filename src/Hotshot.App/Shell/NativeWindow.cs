using System.ComponentModel;
using System.Runtime.InteropServices;
using Hotshot.Interop;

namespace Hotshot.Shell;

/// <summary>Minimal Win32 window wrapper with a managed window procedure.</summary>
internal abstract unsafe class NativeWindow : IDisposable
{
    private const uint WM_NCDESTROY = 0x0082;
    private static readonly Dictionary<nint, NativeWindow> Live = [];
    private static readonly HashSet<string> RegisteredClasses = [];
    [ThreadStatic] private static NativeWindow? s_creating;

    public nint Handle { get; private set; }

    public bool IsCreated => Handle != 0;

    protected void CreateHandle(string className, uint exStyle, uint style, int x, int y, int width, int height,
        string? title = null, int cursorId = Win32.IDC_ARROW)
    {
        RegisterClass(className, cursorId);
        s_creating = this;
        try
        {
            var handle = Win32.CreateWindowEx(exStyle, className, title, style, x, y, width, height, 0, 0,
                Win32.GetModuleHandle(null), 0);
            if (handle == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"CreateWindowEx({className}) failed");
            }

            Handle = handle;
            Live[handle] = this;
        }
        finally
        {
            s_creating = null;
        }
    }

    protected virtual nint WndProc(uint msg, nint wParam, nint lParam) =>
        Win32.DefWindowProc(Handle, msg, wParam, lParam);

    protected virtual void OnDestroyed()
    {
    }

    public virtual void Dispose()
    {
        if (Handle != 0)
        {
            Win32.DestroyWindow(Handle);
        }

        GC.SuppressFinalize(this);
    }

    private static void RegisterClass(string className, int cursorId)
    {
        if (!RegisteredClasses.Add(className))
        {
            return;
        }

        fixed (char* name = className)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = sizeof(WNDCLASSEXW),
                lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&StaticWndProc,
                hInstance = Win32.GetModuleHandle(null),
                hCursor = Win32.LoadCursor(0, cursorId),
                lpszClassName = (nint)name,
            };

            if (Win32.RegisterClassEx(&wc) == 0)
            {
                const int ERROR_CLASS_ALREADY_EXISTS = 1410;
                var error = Marshal.GetLastPInvokeError();
                if (error != ERROR_CLASS_ALREADY_EXISTS)
                {
                    throw new Win32Exception(error, $"RegisterClassEx({className}) failed");
                }
            }
        }
    }

    [UnmanagedCallersOnly]
    private static nint StaticWndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (!Live.TryGetValue(hwnd, out var window))
        {
            window = s_creating;
            if (window is null)
            {
                return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
            }

            window.Handle = hwnd;
            Live[hwnd] = window;
        }

        try
        {
            return window.WndProc(msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            Log.Error($"Unhandled exception in window procedure (msg 0x{msg:X4})", ex);
            return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
        }
        finally
        {
            if (msg == WM_NCDESTROY)
            {
                Live.Remove(hwnd);
                window.Handle = 0;
                try
                {
                    window.OnDestroyed();
                }
                catch (Exception ex)
                {
                    Log.Error("OnDestroyed failed", ex);
                }
            }
        }
    }
}
