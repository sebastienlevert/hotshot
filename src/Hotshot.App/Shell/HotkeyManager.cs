using System.Runtime.InteropServices;
using Hotshot.Core.Hotkeys;
using Hotshot.Core.Settings;
using Hotshot.Interop;

namespace Hotshot.Shell;

internal enum HotkeyState
{
    Unset,
    Registered,
    Conflict,
    Duplicate,
    Invalid,
    Paused,
}

internal sealed record HotkeyStatus(HotkeyAction Action, Hotkey Hotkey, HotkeyState State, string Message);

/// <summary>Registers global hotkeys with RegisterHotKey on the hidden message window.</summary>
internal sealed class HotkeyManager : IDisposable
{
    private readonly MessageWindow _window;
    private readonly Dictionary<int, HotkeyAction> _registered = [];
    private HotkeySettings _settings = new();
    private string? _bindingSignature;
    private int _suspendCount;

    public HotkeyManager(MessageWindow window)
    {
        _window = window;
        _window.HotkeyPressed += id =>
        {
            if (_registered.TryGetValue(id, out var action))
            {
                Pressed?.Invoke(action);
            }
        };
    }

    public event Action<HotkeyAction>? Pressed;
    public event Action? StatusChanged;

    public IReadOnlyDictionary<HotkeyAction, HotkeyStatus> Status { get; private set; } =
        new Dictionary<HotkeyAction, HotkeyStatus>();

    /// <summary>User-controlled "pause hotkeys" toggle from the tray menu.</summary>
    public bool IsPaused { get; private set; }

    public void Apply(HotkeySettings settings)
    {
        var signature = string.Join('\0', Enum.GetValues<HotkeyAction>().Select(settings.Get));
        _settings = settings;
        if (_bindingSignature == signature) return;
        _bindingSignature = signature;
        Reregister();
    }

    public void SetPaused(bool paused)
    {
        IsPaused = paused;
        Reregister();
    }

    /// <summary>Temporarily releases all hotkeys (e.g. while the settings page records a new shortcut).</summary>
    public IDisposable Suspend()
    {
        _suspendCount++;
        Reregister();
        return new Releaser(this);
    }

    public void Dispose() => UnregisterAll();

    private void Reregister()
    {
        UnregisterAll();
        var status = new Dictionary<HotkeyAction, HotkeyStatus>();
        var seen = new Dictionary<Hotkey, HotkeyAction>();
        var active = !IsPaused && _suspendCount == 0;

        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var text = _settings.Get(action);
            if (!Hotkey.TryParse(text, out var hotkey))
            {
                status[action] = new(action, default, HotkeyState.Invalid, $"\"{text}\" is not a valid shortcut");
                continue;
            }

            if (hotkey.IsEmpty)
            {
                status[action] = new(action, hotkey, HotkeyState.Unset, "Not set");
                continue;
            }

            if (seen.TryGetValue(hotkey, out var other))
            {
                status[action] = new(action, hotkey, HotkeyState.Duplicate, $"Already used by \"{other.DisplayName()}\"");
                continue;
            }

            seen[hotkey] = action;
            if (!active)
            {
                status[action] = new(action, hotkey, HotkeyState.Paused, IsPaused ? "Hotkeys are paused" : "Suspended");
                continue;
            }

            var id = (int)action + 1;
            if (Win32.RegisterHotKey(_window.Handle, id, (uint)hotkey.Modifiers | Win32.MOD_NOREPEAT, (uint)hotkey.Key))
            {
                _registered[id] = action;
                status[action] = new(action, hotkey, HotkeyState.Registered, "Active");
            }
            else
            {
                var error = Marshal.GetLastPInvokeError();
                Log.Warn($"RegisterHotKey({hotkey}) for {action} failed with {error}");
                status[action] = new(action, hotkey, HotkeyState.Conflict, "In use by another app or by Windows");
            }
        }

        Status = status;
        StatusChanged?.Invoke();
    }

    private void UnregisterAll()
    {
        foreach (var id in _registered.Keys)
        {
            Win32.UnregisterHotKey(_window.Handle, id);
        }

        _registered.Clear();
    }

    private sealed class Releaser(HotkeyManager owner) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
            {
                return;
            }

            _done = true;
            owner._suspendCount = Math.Max(0, owner._suspendCount - 1);
            owner.Reregister();
        }
    }
}
