namespace Hotshot.Core.Hotkeys;

/// <summary>Bidirectional mapping between Win32 virtual-key codes and stable names used in settings.</summary>
public static class KeyNames
{
    public const int PrintScreen = 0x2C;

    private static readonly Dictionary<int, string> NameByKey = new();
    private static readonly Dictionary<string, int> KeyByName = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> DisplayLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PrintScreen"] = "PrtScn",
        ["PageUp"] = "PgUp",
        ["PageDown"] = "PgDn",
        ["Delete"] = "Del",
        ["Insert"] = "Ins",
        ["Escape"] = "Esc",
    };

    static KeyNames()
    {
        for (var c = 'A'; c <= 'Z'; c++) Add(c, c.ToString());
        for (var d = 0; d <= 9; d++) Add('0' + d, d.ToString());
        for (var f = 1; f <= 24; f++) Add(0x70 + f - 1, $"F{f}");
        for (var n = 0; n <= 9; n++) Add(0x60 + n, $"NumPad{n}");

        Add(0x08, "Backspace");
        Add(0x09, "Tab");
        Add(0x0D, "Enter");
        Add(0x13, "Pause");
        Add(0x14, "CapsLock");
        Add(0x1B, "Escape");
        Add(0x20, "Space");
        Add(0x21, "PageUp");
        Add(0x22, "PageDown");
        Add(0x23, "End");
        Add(0x24, "Home");
        Add(0x25, "Left");
        Add(0x26, "Up");
        Add(0x27, "Right");
        Add(0x28, "Down");
        Add(PrintScreen, "PrintScreen");
        Add(0x2D, "Insert");
        Add(0x2E, "Delete");
        Add(0x6A, "Multiply");
        Add(0x6B, "Add");
        Add(0x6D, "Subtract");
        Add(0x6E, "Decimal");
        Add(0x6F, "Divide");
        Add(0x91, "ScrollLock");
        Add(0xBA, "Semicolon");
        Add(0xBB, "Plus");
        Add(0xBC, "Comma");
        Add(0xBD, "Minus");
        Add(0xBE, "Period");
        Add(0xBF, "Slash");
        Add(0xC0, "Backtick");
        Add(0xDB, "OpenBracket");
        Add(0xDC, "Backslash");
        Add(0xDD, "CloseBracket");
        Add(0xDE, "Quote");

        KeyByName["PrtScn"] = PrintScreen;
        KeyByName["PrtSc"] = PrintScreen;
        KeyByName["Snapshot"] = PrintScreen;
        KeyByName["Esc"] = 0x1B;
        KeyByName["Del"] = 0x2E;
        KeyByName["Return"] = 0x0D;
    }

    public static string GetName(int key) =>
        NameByKey.TryGetValue(key, out var name) ? name : $"0x{key:X2}";

    public static string GetDisplayLabel(string name) =>
        DisplayLabels.TryGetValue(name, out var label) ? label : name;

    public static bool IsKnown(int key) => NameByKey.ContainsKey(key);

    public static bool TryGetKey(string name, out int key)
    {
        if (KeyByName.TryGetValue(name, out key))
        {
            return true;
        }

        if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(name.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out key) &&
            key is > 0 and < 0xFF)
        {
            return true;
        }

        key = 0;
        return false;
    }

    /// <summary>True for keys that are only modifiers and cannot form a hotkey on their own.</summary>
    public static bool IsModifierKey(int key) =>
        key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    private static void Add(int key, string name)
    {
        NameByKey[key] = name;
        KeyByName[name] = key;
    }
}
