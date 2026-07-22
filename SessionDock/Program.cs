using System.Windows;
using SessionDock.Services;
using Velopack;
using Velopack.Locators;

namespace SessionDock;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (!RuntimeSmokeTestOptions.TryParse(
                args,
                out var runtimeSmokeTest,
                out _))
        {
            Environment.ExitCode = 2;
            return;
        }

        var velopackArguments = runtimeSmokeTest is null ? args : [];
        VelopackApp.Build()
            .SetArgs(velopackArguments)
            .SetAutoApplyOnStartup(false)
            .Run();
        AppDataPaths.ConfigureProtectedInstallRoot(
            VelopackLocator.Current.RootAppDir);

        if (runtimeSmokeTest is not null)
        {
            try
            {
                AppDataPaths.ConfigureIsolatedRuntimeRoot(
                    runtimeSmokeTest.RootDirectory);
            }
            catch (Exception exception) when (
                LocalDataException.IsExpectedPersistenceFailure(exception) ||
                exception is ArgumentException)
            {
                Environment.ExitCode = 2;
                return;
            }
        }

        // GitHub-hosted Windows runners intentionally run elevated. The
        // strictly parsed smoke harness has already been confined to a fresh,
        // non-redirected Temp child and cannot resolve production user data,
        // so only that harness skips the production user-context admission
        // check. Every ordinary invocation retains the full policy.
        if (RequiresProductionSecurityContext(runtimeSmokeTest) &&
            !RuntimeSecurityPolicy.IsCurrentProcessSupported(out var reason))
        {
            MessageBox.Show(
                reason,
                "SessionDock cannot start",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Environment.ExitCode = 1;
            return;
        }

        var application = runtimeSmokeTest is null
            ? new App()
            : new App(runtimeSmokeTest);
        application.InitializeComponent();
        Environment.ExitCode = application.Run();
    }

    internal static bool RequiresProductionSecurityContext(
        RuntimeSmokeTestOptions? runtimeSmokeTest) =>
        runtimeSmokeTest is null;
}
