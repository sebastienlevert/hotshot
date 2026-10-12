using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Hotshot.ProductHarness;

internal static class Program
{
    internal static string OutputDirectory = "";
    internal static int ExitCode = 1;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--output" || !Path.IsPathFullyQualified(args[1]))
            throw new ArgumentException("Use --output <absolute task-owned media directory>.");
        OutputDirectory = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(OutputDirectory);
        Environment.SetEnvironmentVariable("HOTSHOT_SETTINGS_SYNC_MODE", "local");
        Console.WriteLine("Starting isolated WinUI product harness.");
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new DemoApplication();
        });
        return ExitCode;
    }
}

public sealed partial class DemoApplication : Application
{
    public DemoApplication()
    {
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, args) => Console.Error.WriteLine(args.Exception);
        InitializeComponent();
        DispatcherQueue.GetForCurrentThread().TryEnqueue(async () =>
        {
            try { await ProductMedia.RunAsync(Program.OutputDirectory); Program.ExitCode = 0; }
            catch (Exception exception) { Console.Error.WriteLine(exception); }
            finally { Exit(); }
        });
    }
}
