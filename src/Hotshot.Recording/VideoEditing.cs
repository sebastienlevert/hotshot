using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Hotshot.Recording;

public static class VideoEditing
{
    public static async Task<Hotshot.Capture.CapturedImage> FrameAsync(string path, TimeSpan position)
    {
        var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(path));
        if (position < TimeSpan.Zero || position > clip.OriginalDuration) throw new ArgumentOutOfRangeException(nameof(position));
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        using var thumbnail = await composition.GetThumbnailAsync(position, 0, 0, VideoFramePrecision.NearestFrame);
        return await Hotshot.Capture.ImageCodec.DecodeAsync(thumbnail);
    }

    public static async Task TrimAsync(string source, string destination, TimeSpan start, TimeSpan end, CancellationToken token = default)
    {
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Trim output must not replace the original.");
        token.ThrowIfCancellationRequested();
        var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(source));
        if (start < TimeSpan.Zero || end <= start || end > clip.OriginalDuration) throw new ArgumentException("Trim times exceed the recording duration.");
        clip.TrimTimeFromStart = start;
        clip.TrimTimeFromEnd = clip.OriginalDuration - end;
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".mp4";
        try
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(Path.GetFullPath(temp))!);
            var file = await folder.CreateFileAsync(Path.GetFileName(temp), CreationCollisionOption.FailIfExists);
            var input = clip.GetVideoEncodingProperties();
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
            profile.Video.Width = input.Width;
            profile.Video.Height = input.Height;
            profile.Video.FrameRate.Numerator = input.FrameRate.Numerator;
            profile.Video.FrameRate.Denominator = input.FrameRate.Denominator;
            var result = await composition.RenderToFileAsync(file, MediaTrimmingPreference.Precise,
                profile).AsTask(token);
            if (result != TranscodeFailureReason.None) throw new IOException("Video trimming failed: " + result);
            token.ThrowIfCancellationRequested();
            File.Move(temp, destination, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
