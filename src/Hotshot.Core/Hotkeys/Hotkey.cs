namespace Hotshot.Core.Hotkeys;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Ctrl = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>Actions that can be bound to a global hotkey.</summary>
public enum HotkeyAction
{
    RegionScreenshot,
    MonitorScreenshot,
    AllMonitorsScreenshot,
    ToggleRecording,
    RecordGif,
    RepeatLastRegion,
    OpenHistory,
    OpenLastInEditor,
}

public static class HotkeyActionExtensions
{
    public static string DisplayName(this HotkeyAction action) => action switch
    {
        HotkeyAction.RegionScreenshot => "Capture region",
        HotkeyAction.MonitorScreenshot => "Capture monitor under cursor",
        HotkeyAction.AllMonitorsScreenshot => "Capture all monitors",
        HotkeyAction.ToggleRecording => "Start / stop recording",
        HotkeyAction.RecordGif => "Record as GIF",
        HotkeyAction.RepeatLastRegion => "Repeat last region",
        HotkeyAction.OpenHistory => "Open editor and history",
        HotkeyAction.OpenLastInEditor => "Open last capture in editor",
        _ => action.ToString(),
    };

    public static string Description(this HotkeyAction action) => action switch
    {
        HotkeyAction.RegionScreenshot => "Drag a region, or click a window to snap to it",
        HotkeyAction.MonitorScreenshot => "Instantly captures the monitor your mouse is on",
        HotkeyAction.AllMonitorsScreenshot => "Instantly captures every monitor as one image",
        HotkeyAction.ToggleRecording => "Pick a region, window or monitor; press again to stop",
        HotkeyAction.RecordGif => "Same as recording, but produces a GIF",
        HotkeyAction.RepeatLastRegion => "Re-captures the last selected region without the overlay",
        HotkeyAction.OpenHistory => "Opens the lightweight editor and capture history",
        HotkeyAction.OpenLastInEditor => "Opens the most recent screenshot in the editor",
        _ => string.Empty,
    };
}

/// <summary>A key combination expressed with Win32 virtual-key codes.</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, int Key)
{
    public static Hotkey None => default;

    public bool IsEmpty => Key == 0;

    public override string ToString()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyNames.GetName(Key));
        return string.Join('+', parts);
    }

    /// <summary>Human-friendly label, e.g. "Ctrl + PrtScn".</summary>
    public string ToDisplayString() =>
        IsEmpty ? "Not set" : string.Join(" + ", ToString().Split('+').Select(KeyNames.GetDisplayLabel));

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var modifiers = HotkeyModifiers.None;
        int key = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= HotkeyModifiers.Ctrl;
                    continue;
                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    continue;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    continue;
                case "win":
                case "windows":
                    modifiers |= HotkeyModifiers.Win;
                    continue;
            }

            if (key != 0 || !KeyNames.TryGetKey(raw, out key))
            {
                return false;
            }
        }

        if (key == 0)
        {
            return false;
        }

        hotkey = new Hotkey(modifiers, key);
        return true;
    }

    public static Hotkey Parse(string? text) =>
        TryParse(text, out var hotkey) ? hotkey : throw new FormatException($"Invalid hotkey '{text}'.");
}
