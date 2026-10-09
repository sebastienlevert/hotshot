using System.Text;
using Hotshot.Core;

namespace Hotshot;

/// <summary>Tiny append-only file logger (%AppData%\Hotshot\hotshot.log, rotated at 1 MB).</summary>
internal static class Log
{
    private const long MaxSize = 1024 * 1024;
    private static readonly Lock Gate = new();

    public static string FilePath { get; set; } = AppPaths.Default.LogFile;

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        System.Diagnostics.Debug.Write(line);
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxSize)
                {
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                }

                File.AppendAllText(FilePath, line, Encoding.UTF8);
            }
            catch (Exception)
            {
                // Logging must never take the app down.
            }
        }
    }
}
