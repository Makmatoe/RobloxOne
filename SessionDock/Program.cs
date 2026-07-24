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
#if SESSIONDOCK_SMOKE_HARNESS
        if (!RuntimeSmokeTestOptions.TryParse(
                args,
                out var runtimeSmokeTest,
                out _))
        {
            Environment.ExitCode = 2;
            return;
        }
        var velopackArguments = runtimeSmokeTest is null ? args : [];
#else
        var velopackArguments = args;
#endif
        VelopackApp.Build()
            .SetArgs(velopackArguments)
            .SetAutoApplyOnStartup(false)
            .Run();
        AppDataPaths.ConfigureProtectedInstallRoot(
            VelopackLocator.Current.RootAppDir);

#if SESSIONDOCK_SMOKE_HARNESS
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
#endif

#if SESSIONDOCK_SMOKE_HARNESS
        var requiresProductionSecurityContext = runtimeSmokeTest is null;
#else
        var requiresProductionSecurityContext =
            ProductionRuntimeAdmissionPolicy.RequiresAdmission(args);
#endif
        if (requiresProductionSecurityContext &&
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

#if SESSIONDOCK_SMOKE_HARNESS
        var application = runtimeSmokeTest is null
            ? new App()
            : new App(runtimeSmokeTest);
#else
        var application = new App();
#endif
        application.InitializeComponent();
        Environment.ExitCode = application.Run();
    }
}

internal static class ProductionRuntimeAdmissionPolicy
{
    internal static bool RequiresAdmission(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return true;
    }
}
