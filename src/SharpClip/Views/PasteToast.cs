using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinRT.Interop;

namespace SharpClip;

internal static class PasteToast
{
    private static ToastWindow? _window;
    private static DispatcherTimer? _timer;

    public static void Show() => Show(Strings.Get("toast_pasted"), success: true);

    public static void ShowFailed() => Show(Strings.Get("paste_failed_title"), success: false);

    private static void Show(string text, bool success)
    {
        _window ??= new ToastWindow();
        _window.SetText(text, success);
        _window.ShowToast();
        _timer ??= new DispatcherTimer();
        _timer.Stop();
        _timer.Interval = TimeSpan.FromMilliseconds(1500);
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private static void OnTick(object? sender, object e)
    {
        _timer?.Stop();
        _window?.HideToast();
    }

    private sealed class ToastWindow : Window
    {
        private readonly TextBlock _text;
        private readonly TextBlock _mark;

        public ToastWindow()
        {
            _mark = new TextBlock
            {
                Text = "\uE73E",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _text = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(_mark);
            row.Children.Add(_text);
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1C, 0x1C, 0x1E)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8, 14, 8),
                Child = row,
            };
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsAlwaysOnTop = true;
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
            }
            AppWindow.IsShownInSwitchers = false;
            AppWindow.Hide();
        }

        public void SetText(string text, bool success)
        {
            _text.Text = text;
            _text.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            _mark.Foreground = new SolidColorBrush(success ? Color.FromArgb(255, 0x4C, 0xD9, 0x64) : Color.FromArgb(255, 0xFF, 0xB0, 0x20));
            _mark.Text = success ? "\uE73E" : "\uE7BA";
        }

        public void ShowToast()
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var style = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
            Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, style | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST);
            var dpi = Win32.GetDpiForWindow(hwnd);
            var scale = (dpi == 0 ? 96 : dpi) / 96.0;
            Win32.GetCursorPos(out var point);
            var monitor = Win32.MonitorFromPoint(point, Win32.MONITOR_DEFAULTTONEAREST);
            var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
            if (Win32.GetMonitorInfo(monitor, ref info))
            {
                var width = (int)Math.Round(220 * scale);
                var height = (int)Math.Round(40 * scale);
                var x = info.rcWork.Left + (info.rcWork.Right - info.rcWork.Left - width) / 2;
                var y = info.rcWork.Bottom - height - (int)Math.Round(28 * scale);
                AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
            }
            Win32.ShowWindow(hwnd, Win32.SW_SHOWNOACTIVATE);
        }

        public void HideToast() => AppWindow.Hide();
    }
}
