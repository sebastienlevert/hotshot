using Hotshot.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace Hotshot;

public static class Program
{
    private const string MutexName = @"Local\Hotshot.SingleInstance";

    internal static CommandLine CommandLine { get; private set; } = new(false, AppCommand.None);

    [STAThread]
    public static int Main(string[] args)
    {
        // Must run first: handles installer/uninstaller hooks and exits the process when invoked by them.
        VelopackApp.Build()
            .SetAutoApplyOnStartup(true)
            .OnAfterInstallFastCallback(_ => StartupManager.Apply(true))
            .OnBeforeUninstallFastCallback(_ => StartupManager.Remove())
            .Run();

        CommandLine = CommandLine.Parse(args);

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Forward the command line to the running instance and exit immediately.
            if (!MessageWindow.SendArguments(args, TimeSpan.FromSeconds(5)))
            {
                Log.Warn("Another instance holds the mutex but its window was not found.");
            }

            return 0;
        }

        if (CommandLine.Command == AppCommand.Exit)
        {
            return 0;
        }

        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });
        }
        catch (Exception ex)
        {
            Log.Error("Fatal error", ex);
            return 1;
        }
        finally
        {
            mutex.ReleaseMutex();
        }

        return 0;
    }
}
