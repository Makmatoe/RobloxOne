using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SessionDock.Services;

internal static class NativeWindowFrameService
{
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeLegacy = 19;
    private const int BorderColor = 34;
    private const int CaptionColor = 35;
    private const int TextColor = 36;
    private const int DefaultDwmColor = -1;

    internal static void ApplyTheme(
        Window window,
        bool useLightTheme,
        bool isHighContrast)
    {
        ArgumentNullException.ThrowIfNull(window);

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            var useDarkCaption = !useLightTheme && !isHighContrast ? 1 : 0;
            if (SetWindowAttribute(
                    handle,
                    UseImmersiveDarkMode,
                    useDarkCaption) != 0)
            {
                _ = SetWindowAttribute(
                    handle,
                    UseImmersiveDarkModeLegacy,
                    useDarkCaption);
            }
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return;

        if (isHighContrast)
        {
            _ = SetWindowAttribute(handle, BorderColor, DefaultDwmColor);
            _ = SetWindowAttribute(handle, CaptionColor, DefaultDwmColor);
            _ = SetWindowAttribute(handle, TextColor, DefaultDwmColor);
            return;
        }

        if (TryGetColor(window, "StrokeBrush", out var border))
            _ = SetWindowAttribute(handle, BorderColor, ToColorRef(border));
        if (TryGetColor(window, "BackgroundBrush", out var caption))
            _ = SetWindowAttribute(handle, CaptionColor, ToColorRef(caption));
        if (TryGetColor(window, "TextBrush", out var text))
            _ = SetWindowAttribute(handle, TextColor, ToColorRef(text));
    }

    internal static int ToColorRef(Color color) =>
        color.R | (color.G << 8) | (color.B << 16);

    private static bool TryGetColor(
        FrameworkElement element,
        string resourceKey,
        out Color color)
    {
        if (element.TryFindResource(resourceKey) is SolidColorBrush brush)
        {
            color = brush.Color;
            return true;
        }

        color = default;
        return false;
    }

    private static int SetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        int value) =>
        DwmSetWindowAttribute(
            windowHandle,
            attribute,
            ref value,
            sizeof(int));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeValueSize);
}
