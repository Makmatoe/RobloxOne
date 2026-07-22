using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SessionDock.Services;

public static class WindowLayoutService
{
    private const double WorkAreaMargin = 16;
    private const uint MonitorDefaultToNearest = 2;

    public static void FitToWorkArea(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!TryGetMonitorWorkArea(window, out var workArea))
            return;

        var availableWidth = Math.Max(1, workArea.Width - (WorkAreaMargin * 2));
        var availableHeight = Math.Max(1, workArea.Height - (WorkAreaMargin * 2));

        window.MinWidth = Math.Min(window.MinWidth, availableWidth);
        window.MinHeight = Math.Min(window.MinHeight, availableHeight);

        var width = GetCurrentDimension(window.ActualWidth, window.Width);
        var height = GetCurrentDimension(window.ActualHeight, window.Height);
        if (width > availableWidth)
        {
            DisableWidthSizeToContent(window);
            window.Width = availableWidth;
            width = availableWidth;
        }
        if (height > availableHeight)
        {
            DisableHeightSizeToContent(window);
            window.Height = availableHeight;
            height = availableHeight;
        }

        if (!IsFinitePositive(width) ||
            !IsFinitePositive(height) ||
            !double.IsFinite(window.Left) ||
            !double.IsFinite(window.Top))
        {
            return;
        }

        var fitted = CalculateFittedBounds(
            workArea,
            new Rect(window.Left, window.Top, width, height));
        window.Left = fitted.Left;
        window.Top = fitted.Top;
    }

    internal static Rect CalculateFittedBounds(
        Rect workArea,
        Rect windowBounds)
    {
        var availableWidth = Math.Max(
            1,
            workArea.Width - (WorkAreaMargin * 2));
        var availableHeight = Math.Max(
            1,
            workArea.Height - (WorkAreaMargin * 2));
        var width = Math.Min(windowBounds.Width, availableWidth);
        var height = Math.Min(windowBounds.Height, availableHeight);
        var minimumLeft = workArea.Left + WorkAreaMargin;
        var minimumTop = workArea.Top + WorkAreaMargin;
        var maximumLeft = Math.Max(
            minimumLeft,
            workArea.Right - WorkAreaMargin - width);
        var maximumTop = Math.Max(
            minimumTop,
            workArea.Bottom - WorkAreaMargin - height);

        return new Rect(
            Math.Clamp(windowBounds.Left, minimumLeft, maximumLeft),
            Math.Clamp(windowBounds.Top, minimumTop, maximumTop),
            width,
            height);
    }

    private static double GetCurrentDimension(
        double actualDimension,
        double requestedDimension) =>
        IsFinitePositive(actualDimension)
            ? actualDimension
            : requestedDimension;

    private static bool IsFinitePositive(double value) =>
        double.IsFinite(value) && value > 0;

    private static void DisableWidthSizeToContent(Window window)
    {
        window.SizeToContent = window.SizeToContent switch
        {
            SizeToContent.Width => SizeToContent.Manual,
            SizeToContent.WidthAndHeight => SizeToContent.Height,
            _ => window.SizeToContent
        };
    }

    private static void DisableHeightSizeToContent(Window window)
    {
        window.SizeToContent = window.SizeToContent switch
        {
            SizeToContent.Height => SizeToContent.Manual,
            SizeToContent.WidthAndHeight => SizeToContent.Width,
            _ => window.SizeToContent
        };
    }

    private static bool TryGetMonitorWorkArea(
        Window window,
        out Rect workArea)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            workArea = default;
            return false;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<MonitorInfo>()
        };
        if (monitor == IntPtr.Zero ||
            !GetMonitorInfo(monitor, ref monitorInfo))
        {
            workArea = default;
            return false;
        }

        var source = HwndSource.FromHwnd(handle);
        var transform = source?.CompositionTarget?.TransformFromDevice;
        if (transform is null)
        {
            workArea = default;
            return false;
        }

        var topLeft = transform.Value.Transform(new Point(
            monitorInfo.WorkArea.Left,
            monitorInfo.WorkArea.Top));
        var bottomRight = transform.Value.Transform(new Point(
            monitorInfo.WorkArea.Right,
            monitorInfo.WorkArea.Bottom));
        workArea = new Rect(topLeft, bottomRight);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal uint Size;
        internal NativeRect MonitorArea;
        internal NativeRect WorkArea;
        internal uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(
        IntPtr windowHandle,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        IntPtr monitorHandle,
        ref MonitorInfo monitorInfo);
}
