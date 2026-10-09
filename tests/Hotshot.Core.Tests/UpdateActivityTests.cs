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

    [Fact]
    public void FeedUsesStaticLatestReleaseAndPackagesUsePinnedVersion()
    {
        Assert.Equal("https://github.com/sebastienlevert/hotshot/releases/latest/download", ReleaseDownloads.FeedBaseUrl);
        Assert.Equal("https://github.com/sebastienlevert/hotshot/releases/download/v0.1.1/Hotshot-0.1.1-win-x64-full.nupkg",
            ReleaseDownloads.PackageUrl("0.1.1", "Hotshot-0.1.1-win-x64-full.nupkg"));
        Assert.DoesNotContain("/latest/", ReleaseDownloads.PackageUrl("0.1.1", "Hotshot-0.1.1-win-x64-full.nupkg"));
    }

    [Fact]
    public void PackageUrlsEscapeUntrustedPathComponents()
    {
        Assert.EndsWith("/a%2Fb%3Ffile.nupkg", ReleaseDownloads.PackageUrl("0.1.1", "a/b?file.nupkg"));
    }
}
