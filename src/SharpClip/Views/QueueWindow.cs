using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinRT.Interop;

namespace SharpClip;

internal sealed class QueueWindow : Window
{
    private readonly Border _root;
    private readonly TextBlock _title;
    private readonly Border _count;
    private readonly TextBlock _countText;
    private readonly Button _direction;
    private readonly StackPanel _rows;

    public QueueWindow()
    {
        WindowIcon.Apply(this);
        var chrome = new Grid { Padding = new Thickness(10, 8, 8, 8) };
        chrome.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        chrome.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _title = new TextBlock { FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(_title);
        _countText = new TextBlock { FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _count = new Border
        {
            MinWidth = 22,
            Height = 18,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(6, 0, 6, 0),
            CornerRadius = new CornerRadius(9),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _countText,
        };
        Grid.SetColumn(_count, 1);
        header.Children.Add(_count);
        _direction = new Button { Width = 28, Height = 28, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(0) };
        _direction.Click += (_, _) => ClipQueue.SetNewestFirst(!ClipQueue.PastesNewestFirst);
        Grid.SetColumn(_direction, 2);
        header.Children.Add(_direction);
        var close = new Button { Width = 28, Height = 28, Margin = new Thickness(2, 0, 0, 0), Padding = new Thickness(0), Content = "\uE8BB", FontFamily = new FontFamily("Segoe MDL2 Assets") };
        close.Click += (_, _) => ClipQueue.Stop();
        Grid.SetColumn(close, 3);
        header.Children.Add(close);
        chrome.Children.Add(header);

        _rows = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        Grid.SetRow(_rows, 1);
        chrome.Children.Add(_rows);

        _root = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(2),
            Child = chrome,
        };
        Content = _root;
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        Closed += (_, _) => { };
        AppWindow.Hide();
    }

    public void ShowQueue()
    {
        Update();
        var hwnd = WindowNative.GetWindowHandle(this);
        var style = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, style | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST);
        Place(hwnd);
        Win32.ShowWindow(hwnd, Win32.SW_SHOWNOACTIVATE);
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | Win32.SWP_NOACTIVATE);
    }

    public void HideQueue() => AppWindow.Hide();

    public void Update()
    {
        var dark = SettingsStore.RequestedTheme() == ElementTheme.Dark
            || (SettingsStore.RequestedTheme() == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);
        _root.RequestedTheme = SettingsStore.RequestedTheme();
        _root.Background = new SolidColorBrush(dark ? Color.FromArgb(0xF0, 0x2C, 0x2C, 0x2E) : Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF));
        _root.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0));
        _root.BorderThickness = new Thickness(1);
        _title.Text = Strings.Get("queue_title");
        _count.Background = new SolidColorBrush(dark ? Color.FromArgb(36, 255, 255, 255) : Color.FromArgb(24, 0, 0, 0));
        _countText.Text = ClipQueue.Count.ToString();
        _direction.Content = new TextBlock
        {
            Text = ClipQueue.PastesNewestFirst ? "\uE74A" : "\uE74B",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        ToolTipService.SetToolTip(_direction, Strings.Get(ClipQueue.PastesNewestFirst ? "queue_newest" : "queue_oldest"));

        _rows.Children.Clear();
        var order = ClipQueue.PasteOrder;
        if (order.Count == 0)
        {
            _rows.Children.Add(Row(Strings.Get("queue_empty"), muted: true));
            return;
        }
        foreach (var entry in order.Take(10))
        {
            var text = entry.Item.Display.Replace("\r", " ").Replace("\n", " ");
            _rows.Children.Add(Row(text, muted: false));
        }
    }

    private static FrameworkElement Row(string text, bool muted) => new TextBlock
    {
        Text = text,
        Height = 30,
        FontSize = 12,
        Opacity = muted ? 0.7 : 1,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap,
    };

    private void Place(IntPtr hwnd)
    {
        var dpi = Win32.GetDpiForWindow(hwnd);
        var scale = (dpi == 0 ? 96 : dpi) / 96.0;
        Win32.GetCursorPos(out var point);
        var monitor = Win32.MonitorFromPoint(point, Win32.MONITOR_DEFAULTTONEAREST);
        var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfo(monitor, ref info)) return;
        var rows = Math.Clamp(Math.Max(ClipQueue.Count, 1), 1, 10);
        var width = (int)Math.Round(300 * scale);
        var height = (int)Math.Round((44 + rows * 30) * scale);
        var margin = (int)Math.Round(12 * scale);
        var x = info.rcWork.Right - width - margin;
        var y = info.rcWork.Top + margin;
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
        var region = Win32.CreateRoundRectRgn(0, 0, width + 1, height + 1, (int)Math.Round(24 * scale), (int)Math.Round(24 * scale));
        Win32.SetWindowRgn(hwnd, region, true);
    }
}
