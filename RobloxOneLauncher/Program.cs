using System.Windows;
using RobloxOneLauncher.Services;
using Velopack;

namespace RobloxOneLauncher;

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
                "Roblox One cannot start",
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
