using Hotshot.Core.Updates;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Hotshot.Updates;

/// <summary>Static GitHub release feeds avoid REST quotas; downloads stay pinned if latest changes.</summary>
internal sealed class GithubReleaseSource : SimpleWebSource
{
    public GithubReleaseSource() : base(ReleaseDownloads.FeedBaseUrl, timeout: 2) { }

    public override Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry,
        string localFile, Action<int> progress, CancellationToken cancelToken)
    {
        var url = ReleaseDownloads.PackageUrl(releaseEntry.Version.ToString(), releaseEntry.FileName);
        return Downloader.DownloadFile(url, localFile, progress, timeout: Timeout, cancelToken: cancelToken);
    }
}
