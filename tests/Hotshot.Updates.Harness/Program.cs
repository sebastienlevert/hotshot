using Hotshot.Core.Updates;
using Hotshot.Updates;
using Velopack;
using Velopack.Logging;

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
foreach (var channel in new[] { "win-x64", "win-arm64" })
{
    var source = new GithubReleaseSource();
    var feed = await source.GetReleaseFeed(new NullVelopackLogger(), "Hotshot", channel);
    var latest = feed.Assets.Where(a => a.Type == VelopackAssetType.Full).MaxBy(a => a.Version)
        ?? throw new InvalidDataException($"No full package in {channel}.");
    Console.WriteLine($"PASS {channel}: static feed has {feed.Assets.Length} assets, latest {latest.Version}.");
    foreach (var asset in feed.Assets.Where(a => a.Version == latest.Version &&
                 a.Type is VelopackAssetType.Full or VelopackAssetType.Delta))
    {
        var url = ReleaseDownloads.PackageUrl(asset.Version.ToString(), asset.FileName);
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        Console.WriteLine($"PASS pinned {asset.Type}: {asset.FileName}.");
    }
}
