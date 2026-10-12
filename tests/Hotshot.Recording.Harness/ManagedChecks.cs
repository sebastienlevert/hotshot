using System.Globalization;
using System.Text.Json;
using Hotshot.Recording;

namespace Hotshot.Recording.Harness;

internal static class ManagedChecks
{
    public static async Task RunAsync()
    {
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
