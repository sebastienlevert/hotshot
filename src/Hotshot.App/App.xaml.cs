using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Hotshot;

public partial class App : Application
{
    private AppController? _controller;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Log.Error("Unhandled UI exception", e.Exception);
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Hotshot lives in the tray: closing the last window must not end the process.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        _controller = new AppController(DispatcherQueue.GetForCurrentThread());
        _controller.Start(Program.CommandLine);
    }
}
