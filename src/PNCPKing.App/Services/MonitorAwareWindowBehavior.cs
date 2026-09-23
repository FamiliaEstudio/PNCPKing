using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PNCPKing.App.Services;

/// <summary>Keeps a window inside the usable area of the monitor it is on.</summary>
public sealed class MonitorAwareWindowBehavior
{
    private const uint MonitorDefaultToNearest = 2;
    private const int WmDpiChanged = 0x02E0;
    private const int WmDisplayChange = 0x007E;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int EdgeMargin = 6;

    private readonly Window _window;
    private readonly double _minimumWidth;
    private readonly double _minimumHeight;
    private HwndSource? _source;
    private IntPtr _lastMonitor;
    private bool _initialPlacement = true;
    private bool _pending;
    private bool _fitting;

    private MonitorAwareWindowBehavior(Window window)
    {
        _window = window;
        _minimumWidth = window.MinWidth;
        _minimumHeight = window.MinHeight;
        window.SourceInitialized += OnSourceInitialized;
        window.Loaded += OnLoaded;
        window.LocationChanged += OnLocationChanged;
        window.StateChanged += OnStateChanged;
        window.Closed += OnClosed;
    }

    public static void Attach(Window window) => _ = new MonitorAwareWindowBehavior(window);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
        _source?.AddHook(WindowMessageHook);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window.Loaded -= OnLoaded;
        ScheduleFit();
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (_initialPlacement || _fitting || _window.WindowState != WindowState.Normal) return;
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle != IntPtr.Zero && MonitorFromWindow(handle, MonitorDefaultToNearest) != _lastMonitor)
            ScheduleFit();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (_window.WindowState == WindowState.Normal) ScheduleFit();
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Let WPF apply the suggested DPI rectangle before fitting the window again.
        if (message is WmDpiChanged or WmDisplayChange) ScheduleFit();
        return IntPtr.Zero;
    }

    private void ScheduleFit()
    {
        if (_pending || !_window.IsLoaded) return;
        _pending = true;
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _pending = false;
            FitToMonitor();
        }));
    }

    private void FitToMonitor()
    {
        if (_fitting || _window.WindowState != WindowState.Normal) return;
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero) return;

        var owner = _initialPlacement && _window.Owner is { } ownerWindow
            ? new WindowInteropHelper(ownerWindow).Handle : IntPtr.Zero;
        var targetHandle = owner != IntPtr.Zero ? owner : handle;
        var monitor = MonitorFromWindow(targetHandle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;

        _fitting = true;
        try
        {
            var area = info.WorkArea;
            var availableWidth = Math.Max(1, area.Right - area.Left - 2 * EdgeMargin);
            var availableHeight = Math.Max(1, area.Bottom - area.Top - 2 * EdgeMargin);
            var dpi = GetDpiForWindow(targetHandle);
            var scale = 96d / (dpi == 0 ? 96 : dpi);
            var maxWidth = availableWidth * scale;
            var maxHeight = availableHeight * scale;

            _window.MinWidth = Math.Min(_minimumWidth, maxWidth);
            _window.MinHeight = Math.Min(_minimumHeight, maxHeight);
            _window.Width = Math.Clamp(double.IsNaN(_window.Width) ? _window.ActualWidth : _window.Width,
                _window.MinWidth, maxWidth);
            _window.Height = Math.Clamp(double.IsNaN(_window.Height) ? _window.ActualHeight : _window.Height,
                _window.MinHeight, maxHeight);

            if (!GetWindowRect(handle, out var bounds)) return;
            var width = Math.Min(bounds.Right - bounds.Left, availableWidth);
            var height = Math.Min(bounds.Bottom - bounds.Top, availableHeight);
            if (owner != IntPtr.Zero && GetWindowRect(owner, out var ownerBounds))
            {
                bounds.Left = ownerBounds.Left + (ownerBounds.Right - ownerBounds.Left - width) / 2;
                bounds.Top = ownerBounds.Top + (ownerBounds.Bottom - ownerBounds.Top - height) / 2;
            }

            var left = Math.Clamp(bounds.Left, area.Left + EdgeMargin, area.Right - EdgeMargin - width);
            var top = Math.Clamp(bounds.Top, area.Top + EdgeMargin, area.Bottom - EdgeMargin - height);
            _ = SetWindowPos(handle, IntPtr.Zero, left, top, width, height, SwpNoZOrder | SwpNoActivate);
            _lastMonitor = monitor;
            _initialPlacement = false;
        }
        finally
        {
            _fitting = false;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _source?.RemoveHook(WindowMessageHook);
        _window.SourceInitialized -= OnSourceInitialized;
        _window.Loaded -= OnLoaded;
        _window.LocationChanged -= OnLocationChanged;
        _window.StateChanged -= OnStateChanged;
        _window.Closed -= OnClosed;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y,
        int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }
}
