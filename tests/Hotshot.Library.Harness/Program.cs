using System.Diagnostics;
using Hotshot.Core.History;

var directory = Path.Combine(Path.GetTempPath(), "hotshot-library-benchmark-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var history = new HistoryStore(Path.Combine(directory, "history.json"), 100, true);
    var timer = Stopwatch.StartNew();
    history.AddRange(Enumerable.Range(0, 10000).Select(index => new HistoryItem
    {
        Id = "capture" + index, Path = Path.Combine(directory, $"screenshot-{index}.png"), Width = 100, Height = 100,
        CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-index), Project = "project" + index % 10,
        RecognizedText = $"Synthetic invoice evidence number {index}.",
    }));
    var persist = timer.ElapsedMilliseconds;
    var checkpoint = new FileInfo(history.CheckpointPath).Length;
    timer.Restart();
    if (history.Search("screenshot-9999.png").Count != 1) throw new InvalidDataException("Cold search is incorrect.");
    var cold = timer.ElapsedMilliseconds;
    timer.Restart();
    for (var i = 0; i < 100; i++)
        if (history.Search("screenshot-9999.png").Count != 1) throw new InvalidDataException("Warm search is incorrect.");
    var warm = timer.ElapsedMilliseconds;
    timer.Restart();
    history.UpdateOrganization("capture9999", true, ["baseline"]);
    var update = timer.ElapsedMilliseconds;
    var journal = new FileInfo(history.CheckpointPath + ".journal").Length;
    timer.Restart();
    var reloaded = new HistoryStore(history.CheckpointPath, 100, true);
    reloaded.Load();
    var restart = timer.ElapsedMilliseconds;
    if (reloaded.Items.Count != 10000 || !reloaded.Items.Single(item => item.Id == "capture9999").IsFavorite)
        throw new InvalidDataException("Journal restart lost data.");
    if (persist > 10000 || cold > 5000 || warm > 2000 || update > 2000 || journal > 10000)
        throw new InvalidDataException("The large-library benchmark exceeded required thresholds.");
    Console.WriteLine($"PASS 10,000 captures: bulk persistence={persist}ms; cold index/query={cold}ms; 100 warm searches={warm}ms; single update={update}ms; restart={restart}ms.");
    Console.WriteLine($"PASS checkpoint={checkpoint:N0} bytes; one-update journal={journal:N0} bytes (full checkpoint not rewritten).");
}
finally { Directory.Delete(directory, true); }
