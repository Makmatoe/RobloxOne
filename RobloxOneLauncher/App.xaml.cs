using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RobloxOneLauncher.Services;

namespace RobloxOneLauncher;

public partial class App : Application
{
    private SingleInstanceService? _singleInstance;
    public UiSoundService SoundService { get; } = new();
    public bool UiSoundsEnabled { get; set; } = true;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new SingleInstanceService("RobloxOneLauncher");
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
        _singleInstance.ListenForActivationRequests(() =>
            Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                ActivateExistingWindow));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        SoundService.Dispose();
        base.OnExit(e);
    }

    private void Button_Click(object sender, RoutedEventArgs e) =>
        SoundService.PlayUiClick(UiSoundsEnabled);

    private static void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
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
}
