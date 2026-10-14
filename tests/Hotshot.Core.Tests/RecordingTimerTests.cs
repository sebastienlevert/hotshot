using Hotshot.Core.Settings;

namespace Hotshot.Core.Tests;

public sealed class RecordingTimerTests
{
    private static readonly int[] ExpectedCountdown = [3, 2, 1, 0];

    [Fact]
    public void CountdownIncludesZeroButNoDelaySkipsTheCountdown()
    {
        Assert.Equal(ExpectedCountdown, RecordingTimerLayout.Countdown(3));
        Assert.Empty(RecordingTimerLayout.Countdown(0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public void InvalidCountdownIsRejected(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RecordingTimerLayout.Countdown(seconds));

    [Fact]
    public void TimerDefaultsAreMiniInTheTopRightAndOldSettingsKeepTheseDefaults()
    {
        var settings = SettingsStore.Deserialize("""{"recording":{"countdownSeconds":3}}""");
        Assert.Equal(RecordingTimerSize.Mini, settings.Recording.TimerSize);
        Assert.Equal(RecordingTimerPlacement.TopRight, settings.Recording.TimerPlacement);
    }

    [Fact]
    public void TimerPreferencesPersistAndRoamWithoutMicrophoneOrPermissionChanges()
    {
        var settings = new AppSettings();
        settings.Recording.TimerSize = RecordingTimerSize.Small;
        settings.Recording.TimerPlacement = RecordingTimerPlacement.BottomLeft;
        var restored = SettingsStore.Deserialize(SettingsStore.Serialize(settings));
        Assert.Equal(RecordingTimerSize.Small, restored.Recording.TimerSize);
        Assert.Equal(RecordingTimerPlacement.BottomLeft, restored.Recording.TimerPlacement);
        var device = new AppSettings();
        device.Recording.MicrophoneDeviceId = "local-microphone";
        var portable = PortableSettings.From(restored).ApplyTo(device);
        Assert.Equal(RecordingTimerSize.Small, portable.Recording.TimerSize);
        Assert.Equal(RecordingTimerPlacement.BottomLeft, portable.Recording.TimerPlacement);
        Assert.Equal("local-microphone", portable.Recording.MicrophoneDeviceId);
        Assert.False(portable.Agents.Enabled);
        Assert.False(portable.General.DescribeScreenshots);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void BothSizesFitAllCornersOnNegativeOriginWorkAreas(double scale)
    {
        foreach (var size in Enum.GetValues<RecordingTimerSize>())
        foreach (var corner in Enum.GetValues<RecordingTimerPlacement>())
        {
            var placed = RecordingTimerLayout.Place(-1920, -100, 1920, 1040, scale, size, corner);
            Assert.InRange(placed.X, -1920, -placed.Width);
            Assert.InRange(placed.Y, -100, 940 - placed.Height);
            Assert.Equal((int)Math.Round((size == RecordingTimerSize.Mini ? 96 : 140) * scale), placed.Width);
            Assert.Equal((int)Math.Round((size == RecordingTimerSize.Mini ? 32 : 48) * scale), placed.Height);
            Assert.Equal(-1920 + (corner is RecordingTimerPlacement.TopLeft or RecordingTimerPlacement.BottomLeft
                ? (int)Math.Round(12 * scale) : 1920 - placed.Width - (int)Math.Round(12 * scale)), placed.X);
            Assert.Equal(-100 + (corner is RecordingTimerPlacement.TopLeft or RecordingTimerPlacement.TopRight
                ? (int)Math.Round(12 * scale) : 1040 - placed.Height - (int)Math.Round(12 * scale)), placed.Y);
        }
    }

    [Fact]
    public void TinyWorkAreasDoNotClipTheTimer()
    {
        var bounds = RecordingTimerLayout.Place(10, 20, 40, 16, 2, RecordingTimerSize.Small, RecordingTimerPlacement.BottomRight);
        Assert.Equal(new RecordingTimerBounds(10, 20, 40, 16), bounds);
    }

    [Fact]
    public void ElapsedTimeDoesNotWrapAfterADay()
    {
        Assert.Equal("00:00:00", RecordingTimerLayout.Elapsed(TimeSpan.Zero));
        Assert.Equal("00:01:05", RecordingTimerLayout.Elapsed(TimeSpan.FromSeconds(65)));
        Assert.Equal("25:02:03", RecordingTimerLayout.Elapsed(new TimeSpan(1, 1, 2, 3)));
    }

    [Fact]
    public void UnknownPreferencesNormalizeButCannotBeSavedDirectly()
    {
        var settings = new AppSettings();
        settings.Recording.TimerSize = (RecordingTimerSize)99;
        settings.Recording.TimerPlacement = (RecordingTimerPlacement)99;
        Assert.NotEmpty(settings.Validate());
        settings.Normalize();
        Assert.Equal(RecordingTimerSize.Mini, settings.Recording.TimerSize);
        Assert.Equal(RecordingTimerPlacement.TopRight, settings.Recording.TimerPlacement);
    }
}
