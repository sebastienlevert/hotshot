using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Hotshot.ProductHarness;

internal static class Program
{
    internal static string OutputDirectory = "";
    internal static int ExitCode = 1;
    internal static bool SettingsHeaderOnly;

    [STAThread]
    public static int Main(string[] args)
    {
        // The referenced app module is the sole owner of Windows App SDK initialization.
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(AppServices).Module.ModuleHandle);
        if (args.Length is not (2 or 3) || args[0] != "--output" || !Path.IsPathFullyQualified(args[1]) ||
            args.Length == 3 && args[2] != "--settings-header-only")
            throw new ArgumentException("Use --output <absolute task-owned media directory> [--settings-header-only].");
        OutputDirectory = Path.GetFullPath(args[1]);
        SettingsHeaderOnly = args.Length == 3;
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
            try { await ProductMedia.RunAsync(Program.OutputDirectory, Program.SettingsHeaderOnly); Program.ExitCode = 0; }
            catch (Exception exception) { Console.Error.WriteLine(exception); }
            finally { Exit(); }
        });
    }
}
