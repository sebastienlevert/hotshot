using System.Diagnostics;
using Hotshot.Capture;
using Hotshot.Core.History;
using Hotshot.Interop;

namespace Hotshot;

/// <summary>Actions available on a capture from the preview popup, history flyout and tray menu.</summary>
internal sealed class CaptureActions(AppServices app)
{
    /// <summary>Opens the annotation editor (wired once the editor is available).</summary>
    public Func<HistoryItem, Task>? EditHandler { get; set; }

    /// <summary>Converts a recording to GIF (wired once the converter is available).</summary>
    public Func<HistoryItem, Task>? ConvertToGifHandler { get; set; }

    public event Action<string>? Failed;

    public bool CanEdit(HistoryItem item) => !item.IsVideo && EditHandler is not null;

    public bool CanConvertToGif(HistoryItem item) => item.Kind == Core.CaptureKind.Recording && ConvertToGifHandler is not null;

    public async Task CopyAsync(HistoryItem item)
    {
        try
        {
            if (!File.Exists(item.Path))
            {
                Failed?.Invoke("The file no longer exists.");
                app.History.Prune();
                return;
            }

            bool ok;
            if (item.IsVideo)
            {
                ok = await ClipboardService.SetFilesAsync(app.MessageWindowHandle, [item.Path]);
            }
            else
            {
                var bytes = await File.ReadAllBytesAsync(item.Path);
                var image = await Task.Run(() => ImageCodec.LoadAsync(item.Path));
                ok = await ClipboardService.SetImageAsync(app.MessageWindowHandle, image, bytes);
            }

            if (!ok)
            {
                Failed?.Invoke("Could not access the clipboard.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Copy failed", ex);
            Failed?.Invoke($"Copy failed: {ex.Message}");
        }
    }

    public void CopyPath(HistoryItem item) => ClipboardService.SetText(app.MessageWindowHandle, item.Path);

    public void Open(HistoryItem item) => OpenPath(item.Path);

    public void ShowInFolder(HistoryItem item)
    {
        if (File.Exists(item.Path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true });
        }
        else
        {
            OpenFolder(Path.GetDirectoryName(item.Path)!);
        }
    }

    public Task EditAsync(HistoryItem item) => EditHandler?.Invoke(item) ?? Task.CompletedTask;

    public Task ConvertToGifAsync(HistoryItem item) => ConvertToGifHandler?.Invoke(item) ?? Task.CompletedTask;

    /// <summary>Moves the file to the Recycle Bin and removes it from history.</summary>
    public void Delete(HistoryItem item)
    {
        if (File.Exists(item.Path) && !item.IsTemporary && !Recycle(item.Path))
        {
            Failed?.Invoke("Could not delete the file.");
            return;
        }

        app.History.Remove(item.Id);
    }

    public void OpenSaveFolder()
    {
        var folder = app.Settings.General.SaveFolder;
        Directory.CreateDirectory(folder);
        OpenFolder(folder);
    }

    public static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {path}", ex);
        }
    }

    public static void OpenFolder(string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {folder}", ex);
        }
    }

    private static unsafe bool Recycle(string path)
    {
        var from = path + "\0\0";
        fixed (char* p = from)
        {
            var op = new SHFILEOPSTRUCTW
            {
                wFunc = Win32.FO_DELETE,
                pFrom = (nint)p,
                fFlags = Win32.FOF_ALLOWUNDO | Win32.FOF_NOCONFIRMATION | Win32.FOF_SILENT | Win32.FOF_NOERRORUI,
            };
            return Win32.SHFileOperation(&op) == 0 && op.fAnyOperationsAborted == 0;
        }
    }
}
