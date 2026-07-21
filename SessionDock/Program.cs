using System.Windows;
using SessionDock.Services;
using Velopack;

namespace SessionDock;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .SetArgs(args)
            .SetAutoApplyOnStartup(false)
            .Run();

        if (!RuntimeSecurityPolicy.IsCurrentProcessSupported(out var reason))
        {
            MessageBox.Show(
                reason,
                "SessionDock cannot start",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Environment.ExitCode = 1;
            return;
        }

        var application = new App();
        application.InitializeComponent();
        application.Run();
    }
}
