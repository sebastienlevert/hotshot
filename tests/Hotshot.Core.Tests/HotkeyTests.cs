using Hotshot.Core.Hotkeys;

namespace Hotshot.Core.Tests;

public sealed class HotkeyTests
{
    [Theory]
    [InlineData("PrintScreen", HotkeyModifiers.None, 0x2C)]
    [InlineData("Ctrl+PrintScreen", HotkeyModifiers.Ctrl, 0x2C)]
    [InlineData("ctrl + shift + prtscn", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, 0x2C)]
    [InlineData("Win+Alt+F12", HotkeyModifiers.Win | HotkeyModifiers.Alt, 0x7B)]
    [InlineData("Ctrl+Shift+2", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, 0x32)]
    [InlineData("Control+0x2C", HotkeyModifiers.Ctrl, 0x2C)]
    public void Parse_Valid(string text, HotkeyModifiers modifiers, int key)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(new Hotkey(modifiers, key), hotkey);
    }

    [Theory]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+Banana")]
    public void Parse_Invalid(string text)
    {
        Assert.False(Hotkey.TryParse(text, out _));
    }

    [Fact]
    public void Empty_IsValidAndEmpty()
    {
        Assert.True(Hotkey.TryParse("", out var hotkey));
        Assert.True(hotkey.IsEmpty);
        Assert.Equal("Not set", hotkey.ToDisplayString());
    }

    [Fact]
    public void ToString_RoundTrips_InCanonicalOrder()
    {
        var hotkey = Hotkey.Parse("shift+win+ctrl+alt+s");
        Assert.Equal("Ctrl+Alt+Shift+Win+S", hotkey.ToString());
        Assert.Equal(hotkey, Hotkey.Parse(hotkey.ToString()));
        Assert.Equal("Ctrl + Alt + Shift + Win + S", hotkey.ToDisplayString());
    }

    [Fact]
    public void DisplayString_UsesFriendlyLabels()
    {
        Assert.Equal("Ctrl + Shift + PrtScn", Hotkey.Parse("Ctrl+Shift+PrintScreen").ToDisplayString());
    }
}
