using Velopack;

namespace AIMeter.App;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        // Must run first: during install, update and uninstall Velopack starts the app
        // with special arguments, runs these hooks, and exits.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => StartupRegistration.Apply(false))
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
