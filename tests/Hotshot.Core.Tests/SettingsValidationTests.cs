using Hotshot.Core.Settings;

namespace Hotshot.Core.Tests;

public sealed class SettingsValidationTests
{
    [Fact]
    public void DefaultsAreValid() => Assert.Empty(new AppSettings().Validate());

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData(@"..\captures")]
    public void RejectsRelativeOrEmptySaveFolders(string folder)
    {
        var settings = new AppSettings();
        settings.General.SaveFolder = folder;
        Assert.Contains(settings.Validate(), e => e.Contains("absolute path"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{unknown}")]
    [InlineData("{counter:99}")]
    [InlineData("{yyyy")]
    public void RejectsInvalidPatterns(string pattern)
    {
        var settings = new AppSettings();
        settings.Naming.ScreenshotPattern = pattern;
        Assert.NotEmpty(settings.Validate());
    }

    [Fact]
    public void RejectsDuplicateAndInvalidHotkeys()
    {
        var settings = new AppSettings();
        settings.Hotkeys.RegionScreenshot = "Control+Alt+S";
        settings.Hotkeys.MonitorScreenshot = "Ctrl+Alt+S";
        settings.Hotkeys.RecordGif = "NotAKey";
        var errors = settings.Validate();
        Assert.Contains(errors, e => e.Contains("same shortcut"));
        Assert.Contains(errors, e => e.Contains("invalid shortcut"));
    }

    [Fact]
    public void AllowsUnboundOptionalHotkeys()
    {
        var settings = new AppSettings();
        settings.Hotkeys.RegionScreenshot = "";
        settings.Hotkeys.MonitorScreenshot = "";
        Assert.Empty(settings.Validate());
    }
}
