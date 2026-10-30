using System.Threading;
using System.Windows;

namespace AIMeter.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private AppController? _controller;
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\AIMeterWin.SingleInstance", out var first);
        if (!first)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            // Keep the tray app alive; a failed refresh or redraw shouldn't quit it.
            Log.Info($"unhandled: {args.Exception}");
            args.Handled = true;
        };

        Log.Info($"starting AIMeter {typeof(App).Assembly.GetName().Version}");
        _controller = new AppController();
        _tray = new TrayIcon(_controller);
        _tray.QuitRequested += Quit;
        _controller.Start();
    }

    private void Quit()
    {
        _tray?.Dispose();
        _controller?.Stop();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
