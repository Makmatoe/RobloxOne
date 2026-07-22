using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SessionDock.Services;

namespace SessionDock;

public partial class App : Application
{
    private const string ProductionApplicationId = "RobloxOneLauncher";
    private readonly string _applicationId;
    private readonly RuntimeSmokeTestOptions? _runtimeSmokeTest;
    private SingleInstanceService? _singleInstance;
    public UiSoundService SoundService { get; private set; } = null!;
    public bool UiSoundsEnabled { get; set; } = true;

    public App()
        : this(ProductionApplicationId, runtimeSmokeTest: null)
    {
    }

    internal App(RuntimeSmokeTestOptions runtimeSmokeTest)
        : this(
            runtimeSmokeTest?.ApplicationId ??
                throw new ArgumentNullException(nameof(runtimeSmokeTest)),
            runtimeSmokeTest)
    {
    }

    private App(
        string applicationId,
        RuntimeSmokeTestOptions? runtimeSmokeTest)
    {
        _applicationId = applicationId;
        _runtimeSmokeTest = runtimeSmokeTest;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Production retains the original mutex name so renamed and older
        // copies cannot run against the same browser profiles at the same time.
        // A smoke run has both an isolated root and an isolated mutex.
        _singleInstance = new SingleInstanceService(_applicationId);
        if (!_singleInstance.IsPrimaryInstance)
        {
            _singleInstance.NotifyPrimaryInstance();
            Shutdown();
            return;
        }

        EventManager.RegisterClassHandler(
            typeof(Button),
            Button.ClickEvent,
            new RoutedEventHandler(Button_Click));
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(Window_Loaded),
            handledEventsToo: true);
        base.OnStartup(e);

        if (!ApplicationStartup.TryStart(
                () =>
                {
                    SoundService = new UiSoundService();
                    var mainWindow = new MainWindow();
                    MainWindow = mainWindow;
                    if (_runtimeSmokeTest is not null)
                    {
                        // Start WPF normally so Loaded, layout, and dispatcher
                        // behavior are exercised without flashing or activating
                        // a window on the maintainer's desktop.
                        mainWindow.ShowActivated = false;
                        mainWindow.ShowInTaskbar = false;
                        mainWindow.WindowStyle = WindowStyle.None;
                        mainWindow.Opacity = 0;
                        mainWindow.WindowStartupLocation =
                            WindowStartupLocation.Manual;
                        mainWindow.Left = SystemParameters.VirtualScreenLeft - 10000;
                        mainWindow.Top = SystemParameters.VirtualScreenTop - 10000;
                    }
                    mainWindow.Show();
                    if (_runtimeSmokeTest is not null)
                    {
                        _ = CompleteRuntimeSmokeTestAsync(
                            mainWindow,
                            _runtimeSmokeTest);
                    }
                },
                message => MessageBox.Show(
                    message,
                    "SessionDock cannot start",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error)))
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            SoundService?.Dispose();
            Shutdown(1);
            return;
        }

        _singleInstance.ListenForActivationRequests(() =>
            Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                ActivateExistingWindow));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        SoundService?.Dispose();
        base.OnExit(e);
    }

    private void Button_Click(object sender, RoutedEventArgs e) =>
        SoundService?.PlayUiClick(UiSoundsEnabled);

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_runtimeSmokeTest is null && sender is Window window)
            WindowLayoutService.FitToWorkArea(window);
    }

    private void ActivateExistingWindow()
    {
        var window = Windows.OfType<MainWindow>().FirstOrDefault() ?? MainWindow;
        if (window is null)
            return;

        if (!window.IsVisible)
            window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    private async Task CompleteRuntimeSmokeTestAsync(
        MainWindow mainWindow,
        RuntimeSmokeTestOptions options)
    {
        try
        {
            var startupFailure = await mainWindow.StartupCompletion;
            if (startupFailure is not null)
            {
                throw new InvalidOperationException(
                    "The isolated runtime smoke-test startup failed.",
                    startupFailure);
            }

            void HandleShutdownCompleted(Exception? shutdownFailure)
            {
                mainWindow.ShutdownCompleted -= HandleShutdownCompleted;
                if (shutdownFailure is not null)
                {
                    System.Diagnostics.Trace.WriteLine(
                        $"Isolated runtime smoke shutdown failed: {shutdownFailure.GetType().Name}.");
                    return;
                }

                try
                {
                    WriteRuntimeSmokeSuccessMarker(options.ResultPath);
                }
                catch (Exception exception)
                {
                    // A missing marker makes the outer smoke script fail.
                    System.Diagnostics.Trace.WriteLine(
                        $"Isolated runtime smoke result failed: {exception.GetType().Name}.");
                }
            }

            // This deliberately takes the normal Closing path so the smoke
            // validates bounded persistence and teardown before process exit.
            mainWindow.ShutdownCompleted += HandleShutdownCompleted;
            mainWindow.Close();
        }
        catch (Exception exception)
        {
            // Smoke mode converts every startup fault into a failed process;
            // it must never report a false success or wait for interaction.
            System.Diagnostics.Trace.WriteLine(
                $"Isolated runtime smoke failed: {exception.GetType().Name}.");
            Shutdown(1);
        }
    }

    private static void WriteRuntimeSmokeSuccessMarker(string resultPath)
    {
        var temporaryPath = resultPath + ".pending";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.Read))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write("SessionDock isolated runtime startup and shutdown completed.");
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, resultPath);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (
                LocalDataException.IsExpectedPersistenceFailure(exception))
            {
                System.Diagnostics.Trace.WriteLine(
                    $"Isolated runtime smoke temporary result cleanup failed: {exception.GetType().Name}.");
            }
        }
    }
}
