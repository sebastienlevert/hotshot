using Hotshot.Interop;

namespace Hotshot.Shell;

internal sealed record TrayMenuItem(string Text, Action? Action = null, bool Enabled = true, bool Checked = false,
    IReadOnlyList<TrayMenuItem>? Children = null)
{
    public static TrayMenuItem Separator { get; } = new("-");

    public bool IsSeparator => Text == "-";
}

/// <summary>Shell_NotifyIcon wrapper (NOTIFYICON_VERSION_4) with a native popup menu.</summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private readonly MessageWindow _window;
    private readonly string _normalIconPath;
    private readonly string _recordingIconPath;
    private nint _normalIcon;
    private nint _recordingIcon;
    private bool _recording;
    private bool _added;
    private string _tooltip = "Hotshot";

    public TrayIcon(MessageWindow window, string normalIconPath, string recordingIconPath)
    {
        _window = window;
        _normalIconPath = normalIconPath;
        _recordingIconPath = recordingIconPath;
        LoadIcons();
        _window.TrayEvent += OnTrayEvent;
        _window.TaskbarCreated += () =>
        {
            _added = false;
            LoadIcons();
            Add();
        };
        EnableDarkMenus();
    }

    public event Action? LeftClick;
    public event Action? DoubleClick;
    public event Action<int, int>? ContextMenuRequested;
    public event Action? BalloonClicked;

    public void Add()
    {
        var data = CreateData(Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP);
        if (_added)
        {
            Win32.Shell_NotifyIcon(Win32.NIM_DELETE, &data);
        }

        _added = Win32.Shell_NotifyIcon(Win32.NIM_ADD, &data);
        data.uVersion = Win32.NOTIFYICON_VERSION_4;
        Win32.Shell_NotifyIcon(Win32.NIM_SETVERSION, &data);
        if (!_added)
        {
            Log.Warn("Shell_NotifyIcon(NIM_ADD) failed; will retry when the taskbar is recreated.");
        }
    }

    public void SetRecording(bool recording, string? tooltip = null)
    {
        _recording = recording;
        _tooltip = tooltip ?? "Hotshot";
        var data = CreateData(Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP);
        Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, &data);
    }

    public void SetTooltip(string tooltip)
    {
        _tooltip = tooltip;
        var data = CreateData(Win32.NIF_TIP | Win32.NIF_SHOWTIP);
        Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, &data);
    }

    public void ShowBalloon(string title, string text, bool isError = false)
    {
        var data = CreateData(Win32.NIF_INFO);
        Copy(title, data.szInfoTitle, 64);
        Copy(text, data.szInfo, 256);
        data.dwInfoFlags = isError ? Win32.NIIF_WARNING : Win32.NIIF_INFO;
        Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, &data);
    }

    /// <summary>Screen rectangle of the tray icon (or null when it lives in the overflow area / is unknown).</summary>
    public RECT? GetIconRect()
    {
        var id = new NOTIFYICONIDENTIFIER { cbSize = sizeof(NOTIFYICONIDENTIFIER), hWnd = _window.Handle, uID = IconId };
        RECT rect;
        return Win32.Shell_NotifyIconGetRect(&id, &rect) == 0 && rect.Width > 0 ? rect : null;
    }

    public void ShowMenu(IReadOnlyList<TrayMenuItem> items, int x, int y)
    {
        var actions = new Dictionary<int, Action>();
        var nextId = 1;
        var menu = Build(items, actions, ref nextId);
        try
        {
            // Required so the menu closes when the user clicks elsewhere.
            Win32.SetForegroundWindow(_window.Handle);
            var command = Win32.TrackPopupMenuEx(menu, Win32.TPM_RETURNCMD | Win32.TPM_RIGHTBUTTON | Win32.TPM_BOTTOMALIGN,
                x, y, _window.Handle, 0);
            Win32.PostMessage(_window.Handle, Win32.WM_NULL, 0, 0);
            if (command > 0 && actions.TryGetValue(command, out var action))
            {
                action();
            }
        }
        finally
        {
            Win32.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = CreateData(0);
            Win32.Shell_NotifyIcon(Win32.NIM_DELETE, &data);
            _added = false;
        }

        DestroyIcons();
    }

    private static nint Build(IReadOnlyList<TrayMenuItem> items, Dictionary<int, Action> actions, ref int nextId)
    {
        var menu = Win32.CreatePopupMenu();
        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                Win32.AppendMenu(menu, Win32.MF_SEPARATOR, 0, null);
                continue;
            }

            var flags = Win32.MF_STRING | (item.Enabled ? 0 : Win32.MF_GRAYED) | (item.Checked ? Win32.MF_CHECKED : 0);
            if (item.Children is { Count: > 0 } children)
            {
                var sub = Build(children, actions, ref nextId);
                Win32.AppendMenu(menu, flags | Win32.MF_POPUP, sub, item.Text);
                continue;
            }

            var id = nextId++;
            if (item.Action is not null)
            {
                actions[id] = item.Action;
            }

            Win32.AppendMenu(menu, flags, id, item.Text);
        }

        return menu;
    }

    private void OnTrayEvent(uint evt, int x, int y)
    {
        switch (evt)
        {
            case Win32.NIN_SELECT:
            case Win32.NIN_KEYSELECT:
                LeftClick?.Invoke();
                break;
            case Win32.WM_LBUTTONDBLCLK:
                DoubleClick?.Invoke();
                break;
            case Win32.WM_CONTEXTMENU:
                ContextMenuRequested?.Invoke(x, y);
                break;
            case Win32.NIN_BALLOONUSERCLICK:
                BalloonClicked?.Invoke();
                break;
        }
    }

    private NOTIFYICONDATAW CreateData(uint flags)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = sizeof(NOTIFYICONDATAW),
            hWnd = _window.Handle,
            uID = IconId,
            uFlags = flags,
            uCallbackMessage = MessageWindow.TrayCallbackMessage,
            hIcon = _recording ? _recordingIcon : _normalIcon,
        };
        Copy(_tooltip, data.szTip, 128);
        return data;
    }

    private static void Copy(string value, char* destination, int capacity)
    {
        var length = Math.Min(value.Length, capacity - 1);
        value.AsSpan(0, length).CopyTo(new Span<char>(destination, capacity));
        destination[length] = '\0';
    }

    private void LoadIcons()
    {
        DestroyIcons();
        var size = Win32.GetSystemMetricsForDpi(Win32.SM_CXSMICON, Win32.GetDpiForSystem());
        _normalIcon = Win32.LoadImage(0, _normalIconPath, Win32.IMAGE_ICON, size, size, Win32.LR_LOADFROMFILE);
        _recordingIcon = Win32.LoadImage(0, _recordingIconPath, Win32.IMAGE_ICON, size, size, Win32.LR_LOADFROMFILE);
        if (_normalIcon == 0)
        {
            Log.Warn($"Could not load tray icon from {_normalIconPath}");
        }
    }

    private void DestroyIcons()
    {
        if (_normalIcon != 0)
        {
            Win32.DestroyIcon(_normalIcon);
        }

        if (_recordingIcon != 0)
        {
            Win32.DestroyIcon(_recordingIcon);
        }

        _normalIcon = _recordingIcon = 0;
    }

    private static void EnableDarkMenus()
    {
        // Undocumented but stable since 1903: uxtheme #135 SetPreferredAppMode(AllowDark), #136 FlushMenuThemes.
        try
        {
            var uxtheme = Win32.LoadLibrary("uxtheme.dll");
            if (uxtheme == 0)
            {
                return;
            }

            var setMode = Win32.GetProcAddress(uxtheme, 135);
            var flush = Win32.GetProcAddress(uxtheme, 136);
            if (setMode != 0)
            {
                ((delegate* unmanaged<int, int>)setMode)(1);
            }

            if (flush != 0)
            {
                ((delegate* unmanaged<void>)flush)();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Dark menus unavailable: {ex.Message}");
        }
    }
}
