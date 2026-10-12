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

    public void Open(HistoryItem item)
    {
        if (!OpenPath(item.Path)) Failed?.Invoke("The capture could not be opened. See the diagnostic log.");
    }

    public void ShowInFolder(HistoryItem item)
    {
        try
        {
            if (File.Exists(item.Path))
            {
                using var process = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true });
            }
            else if (!OpenFolder(Path.GetDirectoryName(item.Path)!))
            {
                Failed?.Invoke("The capture folder could not be opened. See the diagnostic log.");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or ArgumentException)
        {
            Log.Error("Could not show the capture in its folder", ex);
            Failed?.Invoke($"The capture folder could not be opened: {ex.Message}");
        }
    }

    public Task EditAsync(HistoryItem item) => EditHandler?.Invoke(item) ?? Task.CompletedTask;

    public Task ConvertToGifAsync(HistoryItem item) => ConvertToGifHandler?.Invoke(item) ?? Task.CompletedTask;

    /// <summary>Moves the file to the Recycle Bin and removes it from history.</summary>
    public bool Delete(HistoryItem item)
    {
        if (File.Exists(item.Path) && !item.IsTemporary && !Recycle(item.Path))
        {
            Failed?.Invoke("Could not delete the file.");
            return false;
        }

        app.History.Remove(item.Id);
        return true;
    }

    public void OpenSaveFolder()
    {
        var folder = app.Settings.General.SaveFolder;
        Directory.CreateDirectory(folder);
        OpenFolder(folder);
    }

    public static bool OpenPath(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {path}", ex);
            return false;
        }
    }

    public static bool OpenFolder(string folder)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {folder}", ex);
            return false;
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
