using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SessionDock.Services;

internal sealed class AppThemeService : IDisposable
{
    private const string DarkThemeSource =
        "/SessionDock;component/Themes/DarkTheme.xaml";
    private const string LightThemeSource =
        "/SessionDock;component/Themes/LightTheme.xaml";
    private const string HighContrastThemeSource =
        "/SessionDock;component/Themes/HighContrastTheme.xaml";

    private static readonly string[] ThemeFileNames =
    [
        "DarkTheme.xaml",
        "LightTheme.xaml",
        "HighContrastTheme.xaml"
    ];

    private readonly Application _application;
    private ResourceDictionary? _activePalette;
    private bool _disposed;

    internal AppThemeService(Application application)
    {
        _application = application ??
            throw new ArgumentNullException(nameof(application));
        _application.Dispatcher.VerifyAccess();
        _activePalette = FindThemeDictionary(
            _application.Resources.MergedDictionaries);
        SystemParameters.StaticPropertyChanged +=
            SystemParameters_StaticPropertyChanged;
        SystemEvents.UserPreferenceChanged +=
            SystemEvents_UserPreferenceChanged;
        ApplyEffectiveTheme();
    }

    internal bool UseLightThemePreference { get; private set; }

    internal bool IsHighContrastActive => SystemParameters.HighContrast;

    internal event EventHandler? ThemeChanged;

    internal void ApplyPreference(bool useLightTheme)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _application.Dispatcher.VerifyAccess();
        UseLightThemePreference = useLightTheme;
        ApplyEffectiveTheme();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        SystemParameters.StaticPropertyChanged -=
            SystemParameters_StaticPropertyChanged;
        SystemEvents.UserPreferenceChanged -=
            SystemEvents_UserPreferenceChanged;
        _disposed = true;
    }

    private void ApplyEffectiveTheme(bool forceReload = false)
    {
        _application.Dispatcher.VerifyAccess();
        var source = IsHighContrastActive
            ? HighContrastThemeSource
            : UseLightThemePreference
                ? LightThemeSource
                : DarkThemeSource;
        if (!forceReload && HasSource(_activePalette, source))
            return;

        var palette = new ResourceDictionary
        {
            Source = new Uri(source, UriKind.Relative)
        };
        var dictionaries = _application.Resources.MergedDictionaries;
        var index = _activePalette is null
            ? FindThemeDictionaryIndex(dictionaries)
            : dictionaries.IndexOf(_activePalette);
        if (index < 0)
            dictionaries.Insert(0, palette);
        else
            dictionaries[index] = palette;
        _activePalette = palette;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SystemParameters_StaticPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName) &&
            !e.PropertyName.Equals(
                nameof(SystemParameters.HighContrast),
                StringComparison.Ordinal))
        {
            return;
        }
        if (_disposed || _application.Dispatcher.HasShutdownStarted)
            return;

        if (_application.Dispatcher.CheckAccess())
        {
            ApplyEffectiveTheme();
            return;
        }

        _ = _application.Dispatcher.BeginInvoke(
            DispatcherPriority.Send,
            ApplyEffectiveTheme);
    }

    private void SystemEvents_UserPreferenceChanged(
        object sender,
        UserPreferenceChangedEventArgs e)
    {
        if (_disposed ||
            !IsHighContrastActive ||
            _application.Dispatcher.HasShutdownStarted)
        {
            return;
        }

        void ReloadHighContrastPalette() =>
            ApplyEffectiveTheme(forceReload: true);

        if (_application.Dispatcher.CheckAccess())
        {
            ReloadHighContrastPalette();
            return;
        }

        _ = _application.Dispatcher.BeginInvoke(
            DispatcherPriority.Send,
            ReloadHighContrastPalette);
    }

    private static ResourceDictionary? FindThemeDictionary(
        ICollection<ResourceDictionary> dictionaries)
    {
        var index = FindThemeDictionaryIndex(dictionaries);
        return index < 0 ? null : dictionaries.ElementAt(index);
    }

    private static int FindThemeDictionaryIndex(
        ICollection<ResourceDictionary> dictionaries)
    {
        var index = 0;
        foreach (var dictionary in dictionaries)
        {
            if (ThemeFileNames.Any(fileName =>
                    HasSource(dictionary, fileName)))
            {
                return index;
            }
            index++;
        }
        return -1;
    }

    private static bool HasSource(
        ResourceDictionary? dictionary,
        string expectedSuffix) =>
        dictionary?.Source?.OriginalString.EndsWith(
            expectedSuffix,
            StringComparison.OrdinalIgnoreCase) == true;
}
