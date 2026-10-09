using System.Runtime.InteropServices;
using Hotshot.Core.Updates;

namespace Hotshot.Core.Tests;

public sealed class UpdateActivityTests
{
    [Fact]
    public void IdleCanRestart() => Assert.True(new UpdateActivity().CanRestart);

    [Fact]
    public void ActiveWorkAlwaysBlocksRestart()
    {
        UpdateActivity[] activities =
        [
            new(Capturing: true), new(Recording: true), new(Converting: true),
            new(UnsavedEdits: true), new(EditorBusy: true), new(ActiveWindow: true),
            new(UnsavedSettings: true), new(Exiting: true),
        ];
        Assert.All(activities, activity => Assert.False(activity.CanRestart));
    }

    [Theory]
    [InlineData(Architecture.X64, "win-x64")]
    [InlineData(Architecture.Arm64, "win-arm64")]
    public void ChannelMatchesProcessArchitecture(Architecture architecture, string channel) =>
        Assert.Equal(channel, UpdateChannel.ForArchitecture(architecture));

    [Fact]
    public void UnsupportedArchitecturesAreExplicit() =>
        Assert.Throws<PlatformNotSupportedException>(() => UpdateChannel.ForArchitecture(Architecture.X86));
}
