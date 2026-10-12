using System.Globalization;
using System.Text.Json;
using Hotshot.Recording;

namespace Hotshot.Recording.Harness;

internal static class ManagedChecks
{
    public static async Task RunAsync()
    {
        RunColorReferenceChecks();
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Check(WindowScreenshot.CaptureAsync(0, cancelled.Token).IsCanceled, "The compatibility overload did not preserve cancellation.");
            Check(WindowScreenshot.CaptureAsync(0, includeCursor: true, cancellationToken: cancelled.Token).IsCanceled,
                "The token-last overload did not preserve cancellation.");
        }

        string directory = Path.Combine(AppContext.BaseDirectory, "artifacts", "managed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string journal = Path.Combine(directory, "recovery.json");
            var pending = new PendingRecording(Path.Combine(directory, "recording.mp4"), 64, 48);
            await RecordingRecovery.BeginAsync(journal, pending);
            byte[] original = await File.ReadAllBytesAsync(journal);
            try
            {
                await RecordingRecovery.BeginAsync(journal, pending with { Width = 128 });
                throw new InvalidOperationException("An existing recovery journal was overwritten.");
            }
            catch (InvalidOperationException error) when (error.Message.StartsWith("An interrupted recording needs review.", StringComparison.Ordinal))
            {
            }

            byte[] after = await File.ReadAllBytesAsync(journal);
            Check(original.AsSpan().SequenceEqual(after), "Existing recovery journal changed.");
            CultureInfo previousCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
                RecordingRecovery.Dismiss(journal);
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
            }

            string archive = Directory.EnumerateFiles(directory, "recovery.json.archived-*").Single();
            Check(Path.GetFileName(archive).StartsWith("recovery.json.archived-" + DateTime.UtcNow.ToString("yyyy", CultureInfo.InvariantCulture),
                StringComparison.Ordinal), "Recovery archive identifiers use the current culture's calendar.");

            for (int i = 0; i < 20; i++)
            {
                string raceJournal = Path.Combine(directory, $"race-{i}.json");
                var first = new PendingRecording(Path.Combine(directory, "first.mp4"), 64, 48);
                var second = new PendingRecording(Path.Combine(directory, "second.mp4"), 128, 96);
                Task<bool>[] attempts = [TryBeginAsync(raceJournal, first), TryBeginAsync(raceJournal, second)];
                bool[] written = await Task.WhenAll(attempts);
                Check(written.Count(static success => success) == 1, "Concurrent recovery writes did not have exactly one winner.");
                using JsonDocument saved = JsonDocument.Parse(await File.ReadAllTextAsync(raceJournal));
                string? expectedPath = (written[0] ? first : second).Path;
                Check(saved.RootElement.GetProperty("Path").GetString() == expectedPath, "Recovery journal does not belong to the winning writer.");
            }

            Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Partial recovery journals were not cleaned up.");
            Console.WriteLine("PASS managed checks: cancellation overloads, recovery protection/concurrency/cleanup, invariant archive identifiers.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static void RunColorReferenceChecks()
    {
        WindowScreenshotChecks.RunToneMappingChecks();
        Half[] sdr = [(Half)0.18f, (Half)0.5f, (Half)1, (Half)0, (Half)0, (Half)0.5f, (Half)0, (Half)1];
        byte[] output = new byte[sdr.Length + 4];
        output.AsSpan().Fill(17);
        WindowScreenshotToneMapping.ConvertRow(sdr, output, toneMap: false);
        Check(output[0] == 255 && output[1] == 188 && output[2] == 118 && output[3] == 255,
            "The SDR reference row does not preserve transfer/channel order/opacity.");
        Check(output[4] == 0 && output[5] == 188 && output[6] == 0 && output[7] == 255 && output[8] == 17,
            "The row converter wrote outside its destination or changed the reference green patch.");
        Half[] hdr = [(Half)4, (Half)1, (Half)(-0.1f), (Half)1];
        var range = WindowScreenshotToneMapping.AnalyzeRow(hdr);
        Check(range.ExtendedRange && range.NegativeGamut, "The frame color diagnostic missed extended/negative components.");
        WindowScreenshotToneMapping.ConvertRow(hdr, output, range.ExtendedRange);
        Check(output[0] == 0 && output[1] == 188 && output[2] == 231 && output[3] == 255,
            "The extended scRGB reference row was not converted to the expected SDR rendition.");
        try
        {
            WindowScreenshotToneMapping.AnalyzeRow([(Half)0, Half.NaN, (Half)0, (Half)1]);
            throw new InvalidOperationException("Non-finite scRGB reference input was accepted.");
        }
        catch (InvalidDataException) { }
        try
        {
            WindowScreenshotToneMapping.ConvertRow(hdr, new byte[3], false);
            throw new InvalidOperationException("An undersized color destination was accepted.");
        }
        catch (ArgumentException) { }
        Console.WriteLine("PASS shared scRGB readback row: SDR/HDR reference patches, BGRA order, opacity, gamut and destination bounds.");
    }

    private static async Task<bool> TryBeginAsync(string journal, PendingRecording recording)
    {
        try
        {
            await RecordingRecovery.BeginAsync(journal, recording);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith("An interrupted recording needs review.", StringComparison.Ordinal))
        {
            return false;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
