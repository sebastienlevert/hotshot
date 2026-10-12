using System.Net;
using System.Security.Cryptography;

namespace Hotshot.Core.Updates;

public readonly record struct ComponentProgress(string Stage, long Bytes = 0, long TotalBytes = 0)
{
    public int Percent => TotalBytes > 0 ? (int)Math.Clamp(Bytes * 100 / TotalBytes, 0, 100) : 0;
}

/// <summary>Component downloads use the same official, version-pinned release URLs as app updates.</summary>
public sealed class ReleaseAssetDownloader(HttpClient client)
{
    public static HttpClient CreateClient() => new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.None,
        AllowAutoRedirect = false,
        UseCookies = false,
    }) { Timeout = TimeSpan.FromMinutes(10) };

    public async Task<byte[]> ReadManifestAsync(string version, string assetName, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(version, assetName, cancellationToken);
        if (response.Content.Headers.ContentLength > CopilotComponentManifest.MaximumManifestBytes)
            throw new InvalidDataException("Component manifest exceeds the download limit.");
        using var output = new MemoryStream();
        await CopyAsync(response, output, CopilotComponentManifest.MaximumManifestBytes, null, cancellationToken);
        return output.ToArray();
    }

    public async Task DownloadAsync(CopilotComponentManifest manifest, string destination,
        IProgress<ComponentProgress>? progress, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(manifest.AppVersion, manifest.FileName, cancellationToken);
        if (response.Content.Headers.ContentLength is { } length && length != manifest.ArchiveBytes)
            throw new InvalidDataException("Component archive length differs from its manifest.");
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var copied = await CopyAsync(response, output, manifest.ArchiveBytes, progress, cancellationToken);
        if (copied != manifest.ArchiveBytes) throw new InvalidDataException("Component download is incomplete.");
        output.Position = 0;
        var digest = await SHA256.HashDataAsync(output, cancellationToken);
        if (!CopilotComponentManifest.HashMatches(digest, manifest.Sha256))
            throw new InvalidDataException("Component archive SHA256 verification failed.");
    }

    private async Task<HttpResponseMessage> GetAsync(string version, string assetName, CancellationToken cancellationToken)
    {
        var uri = new Uri(ReleaseDownloads.PackageUrl(version, assetName));
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!IsOfficialHttps(response.RequestMessage?.RequestUri))
            {
                response.Dispose();
                throw new HttpRequestException("The component download left the official HTTPS release hosts.");
            }
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new HttpRequestException("The component download redirect has no destination.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (!IsOfficialHttps(uri)) throw new HttpRequestException("Unsafe component download redirect.");
                continue;
            }
            if (response.IsSuccessStatusCode) return response;
            var status = response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"Official Copilot component download failed ({status}).");
        }
        throw new HttpRequestException("The component download exceeded its redirect limit.");
    }

    private static bool IsOfficialHttps(Uri? uri) => uri is not null && uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static async Task<long> CopyAsync(HttpResponseMessage response, Stream output, long maximum,
        IProgress<ComponentProgress>? progress, CancellationToken cancellationToken)
    {
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[81920];
        long bytes = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            bytes = checked(bytes + read);
            if (bytes > maximum) throw new InvalidDataException("Component download exceeded its byte limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            progress?.Report(new("Downloading", bytes, maximum));
        }
        return bytes;
    }
}
