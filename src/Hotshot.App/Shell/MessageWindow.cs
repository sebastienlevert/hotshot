using System.Runtime.InteropServices;
using Hotshot.Interop;

namespace Hotshot.Shell;

/// <summary>
/// Hidden top-level window that receives global hotkeys, tray icon callbacks, TaskbarCreated
/// and command lines forwarded from secondary instances (WM_COPYDATA).
/// </summary>
internal sealed unsafe class MessageWindow : NativeWindow
{
    public const string ClassName = "Hotshot.MessageWindow";
    public const uint TrayCallbackMessage = Win32.WM_APP + 1;
    public const nint CopyDataSignature = 0x48534854; // "HSHT"

    private readonly uint _taskbarCreatedMessage = Win32.RegisterWindowMessage("TaskbarCreated");

    public MessageWindow()
    {
        // A real (never shown) popup, not HWND_MESSAGE, so it receives the TaskbarCreated broadcast.
        CreateHandle(ClassName, Win32.WS_EX_TOOLWINDOW, Win32.WS_POPUP, 0, 0, 0, 0, "Hotshot");
    }

    public event Action<int>? HotkeyPressed;
    public event Action<string[]>? ArgumentsReceived;
    public event Action<uint, int, int>? TrayEvent;
    public event Action? TaskbarCreated;
    public event Action? SettingsChanged;

    protected override nint WndProc(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case Win32.WM_HOTKEY:
                HotkeyPressed?.Invoke((int)wParam);
                return 0;

            case TrayCallbackMessage:
                // NOTIFYICON_VERSION_4: LOWORD(lParam) = event, wParam = anchor coordinates.
                TrayEvent?.Invoke((uint)Win32.LoWord(lParam) & 0xFFFF, Win32.LoWord(wParam), Win32.HiWord(wParam));
                return 0;

            case Win32.WM_COPYDATA:
                var data = (COPYDATASTRUCT*)lParam;
                if (data->dwData == CopyDataSignature && data->cbData > 0 && data->lpData != 0)
                {
                    var text = new string((char*)data->lpData, 0, data->cbData / sizeof(char));
                    var args = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    PostToDispatcher(() => ArgumentsReceived?.Invoke(args));
                    return 1;
                }

                return 0;

            case Win32.WM_SETTINGCHANGE:
                SettingsChanged?.Invoke();
                break;
        }

        if (msg == _taskbarCreatedMessage && msg != 0)
        {
            TaskbarCreated?.Invoke();
            return 0;
        }

        return base.WndProc(msg, wParam, lParam);
    }

    /// <summary>Sends a command line to the running instance. Returns false if none was found.</summary>
    public static bool SendArguments(IReadOnlyList<string> args, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        nint target;
        while ((target = Win32.FindWindow(ClassName, null)) == 0)
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            Thread.Sleep(100);
        }

        Native.GetWindowThreadProcessId(target, out var pid);
        Win32.AllowSetForegroundWindow(pid);

        var payload = string.Join('\n', args.Count == 0 ? ["--activate"] : args);
        fixed (char* chars = payload)
        {
            var data = new COPYDATASTRUCT
            {
                dwData = CopyDataSignature,
                cbData = payload.Length * sizeof(char),
                lpData = (nint)chars,
            };
            Win32.SendMessage(target, Win32.WM_COPYDATA, 0, (nint)(&data));
        }

        return true;
    }

    private static void PostToDispatcher(Action action)
    {
        // Leave the SendMessage call quickly so the sender is not blocked while we open windows.
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (queue is null || !queue.TryEnqueue(() => action()))
        {
            action();
        }
    }
}
