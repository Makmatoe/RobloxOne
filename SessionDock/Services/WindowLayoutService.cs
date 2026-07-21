using System.Windows;

namespace SessionDock.Services;

public static class WindowLayoutService
{
    private const double WorkAreaMargin = 16;

    public static void FitToWorkArea(Window window)
    {
        var workArea = SystemParameters.WorkArea;
        var availableWidth = Math.Max(1, workArea.Width - (WorkAreaMargin * 2));
        var availableHeight = Math.Max(1, workArea.Height - (WorkAreaMargin * 2));

        window.MinWidth = Math.Min(window.MinWidth, availableWidth);
        window.MinHeight = Math.Min(window.MinHeight, availableHeight);
        window.MaxWidth = Math.Min(window.MaxWidth, availableWidth);
        window.MaxHeight = Math.Min(window.MaxHeight, availableHeight);

        if (!double.IsNaN(window.Width))
            window.Width = Math.Min(window.Width, availableWidth);
        if (!double.IsNaN(window.Height))
            window.Height = Math.Min(window.Height, availableHeight);

        var width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        var height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
        if (!double.IsNaN(width) && width > 0)
        {
            window.Left = Math.Clamp(
                window.Left,
                workArea.Left + WorkAreaMargin,
                Math.Max(
                    workArea.Left + WorkAreaMargin,
                    workArea.Right - WorkAreaMargin - width));
        }
        if (!double.IsNaN(height) && height > 0)
        {
            window.Top = Math.Clamp(
                window.Top,
                workArea.Top + WorkAreaMargin,
                Math.Max(
                    workArea.Top + WorkAreaMargin,
                    workArea.Bottom - WorkAreaMargin - height));
        }
    }
}
