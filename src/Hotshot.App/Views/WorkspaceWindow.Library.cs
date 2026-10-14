using System.IO.Compression;
using System.Text.Json;
using Hotshot.Capture;
using Hotshot.Core;
using Hotshot.Core.History;
using Hotshot.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace Hotshot.Views;

internal sealed partial class WorkspaceWindow
{
    private CancellationToken OperationToken => _operationCancellation?.Token ?? CancellationToken.None;

    private async Task StoragePrivacyAsync()
    {
        if (!await ConfirmDiscardAsync()) return;
        var groups = await Task.Run(() => LibraryMaintenance.Inspect(_app.Paths, _app.History.Items), OperationToken);
        var all = new CheckBox { Content = "Also delete originals and editable annotations", IsChecked = false };
        var body = new StackPanel { Spacing = 10 };
        foreach (var group in groups) body.Children.Add(new TextBlock { Text = $"{group.Name}: {group.Files:N0} files / {group.Bytes / 1048576d:F1} MB", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(all);
        body.Children.Add(UiStyles.Description("Saved captures stay safe. Deleting originals permanently removes editable history. You'll confirm before anything is deleted."));
        if (await DialogAsync("Storage and privacy", body, "Preview cleanup") != ContentDialogResult.Primary) return;
        var includeAll = all.IsChecked == true;
        var reviewed = await Task.Run(() => LibraryMaintenance.PreviewCleanup(_app.Paths, _app.History.Items, includeAll), OperationToken);
        var list = string.Join("\n", reviewed.Take(100).Select(file => $"{file.Category}: {Path.GetFileName(file.Path)}"));
        if (reviewed.Length == 0) { await DialogAsync("Nothing to clean up", UiStyles.Description("No matching files were found."), "Close"); return; }
        if (await DialogAsync("Permanently remove reviewed files?", new ScrollViewer { Content = new TextBlock
            { Text = $"{reviewed.Length:N0} files / {reviewed.Sum(file => file.Bytes) / 1048576d:F1} MB\n{list}", TextWrapping = TextWrapping.Wrap }, MaxHeight = 400 },
            "Delete permanently") != ContentDialogResult.Primary) return;
        async Task Cleanup()
        {
            await Task.Run(() => LibraryMaintenance.DeleteReviewed(_app.Paths, reviewed, _app.History.Items.Select(item => item.Path)), OperationToken);
        }
        if (_app.Descriptions is { } descriptions) await descriptions.RunFileOperationAsync(Cleanup);
        else await Cleanup();
        if (_current is { } item && !item.IsVideo) await LoadAsync(item);
    }

    private async Task BackupLibraryAsync()
    {
        if (_editor.IsDirty) throw new InvalidOperationException("Save your edits before backing up the library.");
        var originals = new CheckBox { Content = "Include originals and editable annotations", IsChecked = false };
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(originals);
        body.Children.Add(UiStyles.Description("Save a backup on this device. Originals may reveal details you've hidden. No sign-ins or settings are included."));
        if (await DialogAsync("Back up library", body, "Choose folder") != ContentDialogResult.Primary) return;
        var picker = new FileSavePicker(AppWindow.Id) { SuggestedFileName = "Hotshot-library-" + DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) };
        picker.FileTypeChoices.Add("Hotshot library backup", [".zip"]);
        var chosen = await picker.PickSaveFileAsync();
        if (chosen is null) return;
        var include = originals.IsChecked == true;
        async Task Backup() => await Task.Run(() => LibraryMaintenance.BackupAsync(chosen.Path, _app.History, _app.Paths, include, OperationToken), OperationToken);
        if (_app.Descriptions is { } descriptions) await descriptions.RunFileOperationAsync(Backup);
        else await Backup();
    }

    private async Task RestoreLibraryAsync()
    {
        if (!await ConfirmDiscardAsync()) return;
        var chosen = await new FileOpenPicker(AppWindow.Id) { Title = "Choose a Hotshot backup", FileTypeFilter = { ".zip" } }.PickSingleFileAsync();
        if (chosen is null) return;
        var preview = await Task.Run(() => LibraryMaintenance.PreviewRestore(chosen.Path), OperationToken);
        var originals = new CheckBox { Content = "Restore originals and editable annotations", IsChecked = false, IsEnabled = preview.IncludesOriginals };
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(UiStyles.Description($"{preview.Captures:N0} items / {preview.Bytes / 1048576d:F1} MB. Restores to a new folder. Existing files stay unchanged; nothing is sent to Copilot."));
        body.Children.Add(originals);
        if (await DialogAsync("Restore backup?", body, "Restore") != ContentDialogResult.Primary) return;
        var include = originals.IsChecked == true;
        await Task.Run(() => LibraryMaintenance.RestoreAsync(chosen.Path, _app.Settings.General.SaveFolder, _app.History, _app.Paths, include, OperationToken), OperationToken);
        RenderHistory();
    }

    private async Task RelinkAsync()
    {
        if (_current is not { } item) return;
        if (File.Exists(item.Path)) throw new InvalidOperationException("This file isn't missing. Choose a missing file to locate.");
        var picker = new FileOpenPicker(AppWindow.Id) { Title = "Locate this moved capture" };
        picker.FileTypeFilter.Add(Path.GetExtension(item.Path));
        var chosen = await picker.PickSingleFileAsync();
        if (chosen is null) return;
        if (!item.IsVideo)
        {
            var size = await ImageCodec.GetSizeAsync(chosen.Path);
            if (size.Width != item.Width || size.Height != item.Height)
                throw new ArgumentException("The replacement dimensions do not match this capture.");
        }
        await Task.Run(() => _app.History.Relink(item.Id, chosen.Path, new FileInfo(chosen.Path).Length), OperationToken);
        await LoadAsync(item);
    }

    private async Task SetBaselineAsync()
    {
        if (_current is not { IsVideo: false } item || item.Project.Length == 0)
            throw new InvalidOperationException("Assign the selected screenshot to a project first.");
        if (_editor.IsDirty) throw new InvalidOperationException("Save edits before setting a baseline.");
        if (_app.Settings.Library.Baselines.Count >= 1000 && !_app.Settings.Library.Baselines.Any(entry => entry.Project.Equals(item.Project, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Up to 1000 project baselines are supported.");
        if (await DialogAsync("Set project baseline?", UiStyles.Description(
            $"{item.FileName} becomes the comparison baseline for {item.Project}. It will be kept as a favorite."), "Set baseline") != ContentDialogResult.Primary) return;
        _app.Settings.Library.Baselines.RemoveAll(baseline => baseline.Project.Equals(item.Project, StringComparison.OrdinalIgnoreCase));
        _app.Settings.Library.Baselines.Add(new ProjectBaseline(item.Project, item.Id));
        await Task.Run(() => _app.History.UpdateOrganization(item.Id, true, item.Tags));
        if (!await _app.SaveAsync()) throw new IOException("Baseline settings could not be saved.");
    }

    private async Task CompareBaselineAsync()
    {
        if (_current is not { IsVideo: false } after) return;
        var baseline = _app.Settings.Library.Baselines.FirstOrDefault(entry => entry.Project.Equals(after.Project, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Set a baseline for this project first.");
        var before = _app.History.Items.FirstOrDefault(item => item.Id == baseline.CaptureId)
            ?? throw new InvalidOperationException("The baseline capture was removed. Set a replacement.");
        if (_editor.IsDirty) throw new InvalidOperationException("Save your edits before comparing.");
        var first = await Task.Run(() => ImageCodec.LoadAsync(before.Path), OperationToken);
        var second = await Task.Run(() => ImageCodec.LoadAsync(after.Path), OperationToken);
        var comparison = await Task.Run(() => ImageComparison.CompareCancellable(first, second, new ComparisonOptions(), 8, OperationToken), OperationToken);
        var window = await ComparisonWindow.CreateAsync(first, second, comparison);
        _app.AuxiliaryWindowCount++;
        window.Closed += (_, _) => _app.AuxiliaryWindowCount--;
        window.Activate();
    }

    private async Task EvidenceBundleAsync()
    {
        var items = SelectedCaptures();
        if (items.Length is < 1 or > 20 || items.Any(item => item.IsVideo))
            throw new InvalidOperationException("Select 1-20 screenshots for a local evidence bundle.");
        if (_editor.IsDirty) throw new InvalidOperationException("Save redactions and edits before bundling saved evidence.");
        if (await DialogAsync("Package screenshots?", UiStyles.Description(
            "Saves a ZIP with edited screenshots and basic file details. No originals, extracted text, descriptions or sign-ins. Nothing is uploaded."), "Choose folder") != ContentDialogResult.Primary) return;
        var picker = new FileSavePicker(AppWindow.Id) { SuggestedFileName = "Hotshot-evidence-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) };
        picker.FileTypeChoices.Add("Local ZIP bundle", [".zip"]);
        var chosen = await picker.PickSaveFileAsync();
        if (chosen is null) return;
        var temp = chosen.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await Task.Run(async () =>
            {
                using var zip = ZipFile.Open(temp, ZipArchiveMode.Create);
                var entries = new List<Dictionary<string, string>>();
                for (var index = 0; index < items.Length; index++)
                {
                    OperationToken.ThrowIfCancellationRequested();
                    var item = items[index];
                    var image = await ImageCodec.LoadAsync(item.Path);
                    for (var pixel = 0; pixel < image.Pixels.Length; pixel += 4)
                    {
                        var alpha = image.Pixels[pixel + 3];
                        if (alpha == 255) continue;
                        var white = 255 * (255 - alpha);
                        for (var channel = 0; channel < 3; channel++) image.Pixels[pixel + channel] = (byte)((image.Pixels[pixel + channel] * alpha + white) / 255);
                        image.Pixels[pixel + 3] = 255;
                    }
                    var png = await ImageCodec.EncodePngAsync(image);
                    var name = $"capture-{index + 1}.png";
                    await using (var output = zip.CreateEntry(name, CompressionLevel.Fastest).Open()) await output.WriteAsync(png, OperationToken);
                    entries.Add(new() { ["file"] = name, ["width"] = image.Width.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["height"] = image.Height.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["createdAt"] = item.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture), ["project"] = item.Project });
                }
                await using var manifest = zip.CreateEntry("evidence.json").Open();
                await JsonSerializer.SerializeAsync(manifest, entries, EvidenceJsonOptions, OperationToken);
            }, OperationToken);
            OperationToken.ThrowIfCancellationRequested();
            File.Move(temp, chosen.Path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
