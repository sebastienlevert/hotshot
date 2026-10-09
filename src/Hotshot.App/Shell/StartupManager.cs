using Hotshot.Interop;
using Microsoft.Win32;

namespace Hotshot.Shell;

/// <summary>Launch-at-login via HKCU\...\Run (works for the unpackaged, per-user install).</summary>
internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Hotshot";

    public static string Command => $"\"{Environment.ProcessPath}\" --background";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string value && value.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);
    }

    public static void Apply(bool enable)
    {
#if DEBUG
        // Never register a development build to start with Windows.
        Log.Info($"[debug] Start with Windows = {enable} (registry not modified in Debug builds)");
#else
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enable)
            {
                key.SetValue(ValueName, Command);
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not update the startup registration", ex);
        }
#endif
    }

    public static void Remove()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>Windows 11 can bind PrtScn to Snipping Tool, which swallows the key before RegisterHotKey sees it.</summary>
internal static class SnippingToolKey
{
    private const string KeyPath = @"Control Panel\Keyboard";
    private const string ValueName = "PrintScreenKeyForSnippingEnabled";

    public static bool IsPrintScreenTakenByWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        var value = key?.GetValue(ValueName);
        if (value is int number)
        {
            return number != 0;
        }

        // Absent value: on by default on Windows 11 since 2023.
        return Environment.OSVersion.Version.Build >= 22000;
    }

    public static bool Release()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            key.SetValue(ValueName, 0, RegistryValueKind.DWord);
            const nint HWND_BROADCAST = 0xFFFF;
            const uint SMTO_ABORTIFHUNG = 0x2;
            Win32.SendMessageTimeout(HWND_BROADCAST, Win32.WM_SETTINGCHANGE, 0, KeyPath, SMTO_ABORTIFHUNG, 1000, out _);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not release the Print Screen key", ex);
            return false;
        }
    }
}
