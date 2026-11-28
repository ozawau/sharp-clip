using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace SharpClip;

internal sealed class WelcomeWindow : Window
{
    private readonly StackPanel _body = new() { Spacing = 12 };
    private readonly TextBlock _title = new() { FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.Bold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detail = new() { FontSize = 14, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
    private readonly Button _back = new();
    private readonly Button _next = new();
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
    private int _step;

    public WelcomeWindow()
    {
        WindowIcon.Apply(this);
        var root = new Grid { Padding = new Thickness(28), RowSpacing = 16 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(_title);
        var middle = new StackPanel { Spacing = 16 };
        middle.Children.Add(_detail);
        middle.Children.Add(_body);
        Grid.SetRow(middle, 1);
        root.Children.Add(middle);
        var footer = new Grid();
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        footer.Children.Add(_dots);
        var buttons = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        _back.Click += (_, _) => { if (_step > 0) ShowStep(_step - 1); };
        _next.Click += (_, _) =>
        {
            if (_step < 2) ShowStep(_step + 1);
            else Finish();
        };
        Grid.SetColumn(_next, 1);
        _next.Margin = new Thickness(8, 0, 0, 0);
        _next.HorizontalAlignment = HorizontalAlignment.Stretch;
        buttons.Children.Add(_back);
        buttons.Children.Add(_next);
        Grid.SetRow(buttons, 1);
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            Finish();
        };
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }
        ShowStep(0);
    }

    public void Present()
    {
        ShowStep(_step);
        var hwnd = WindowNative.GetWindowHandle(this);
        var dpi = Win32.GetDpiForWindow(hwnd);
        var scale = (dpi == 0 ? 96 : dpi) / 96.0;
        var width = (int)Math.Round(520 * scale);
        var height = (int)Math.Round(460 * scale);
        Win32.GetCursorPos(out var point);
        var monitor = Win32.MonitorFromPoint(point, Win32.MONITOR_DEFAULTTONEAREST);
        var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        if (Win32.GetMonitorInfo(monitor, ref info))
        {
            var x = info.rcWork.Left + Math.Max(0, (info.rcWork.Right - info.rcWork.Left - width) / 2);
            var y = info.rcWork.Top + Math.Max(0, (info.rcWork.Bottom - info.rcWork.Top - height) / 2);
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
        }
        AppWindow.Show();
        Activate();
    }

    private void ShowStep(int step)
    {
        _step = step;
        Title = "SharpClip";
        _back.Content = Strings.Get("welcome_back");
        _back.Visibility = step == 0 ? Visibility.Collapsed : Visibility.Visible;
        _next.Content = step == 2 ? Strings.Get("welcome_start") : Strings.Get("welcome_continue");
        _body.Children.Clear();
        _dots.Children.Clear();
        for (var i = 0; i < 3; i++)
        {
            _dots.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = new SolidColorBrush(i == step ? Color.FromArgb(255, 0x3B, 0x71, 0xF3) : Color.FromArgb(60, 128, 128, 128)),
            });
        }
        switch (step)
        {
            case 0:
                _title.Text = Strings.Get("welcome_1_title");
                _detail.Text = Strings.Get("welcome_1_body");
                _body.Children.Add(Line(Strings.Get("welcome_history"), Strings.Get("welcome_1_body")));
                _body.Children.Add(Line(Strings.Get("welcome_boards"), Strings.Get("welcome_1_body")));
                _body.Children.Add(Line(Strings.Get("welcome_private"), Strings.Get("welcome_private_body")));
                break;
            case 1:
                _title.Text = Strings.Get("welcome_2_title");
                _detail.Text = Strings.Get("welcome_2_body");
                var hotkey = HotkeyText.Format(SettingsStore.Current.PanelControl, SettingsStore.Current.PanelShift, SettingsStore.Current.PanelAlt, SettingsStore.Current.PanelWin, SettingsStore.Current.PanelKey);
                var key = new TextBlock { Text = hotkey, FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
                _body.Children.Add(key);
                var change = new Button { Content = Strings.Get("shortcut_panel"), HorizontalAlignment = HorizontalAlignment.Center };
                change.Click += (_, _) =>
                {
                    App.Instance.ShowSettings();
                };
                _body.Children.Add(change);
                break;
            default:
                _title.Text = Strings.Get("welcome_3_title");
                _detail.Text = Strings.Get("welcome_3_body") + " " + HotkeyText.Format(SettingsStore.Current.PanelControl, SettingsStore.Current.PanelShift, SettingsStore.Current.PanelAlt, SettingsStore.Current.PanelWin, SettingsStore.Current.PanelKey);
                var import = new Button { Content = Strings.Get("welcome_import"), HorizontalAlignment = HorizontalAlignment.Left };
                import.Click += async (_, _) => await ImportAsync();
                _body.Children.Add(import);
                break;
        }
    }

    private static Border Line(string title, string body) => new()
    {
        Padding = new Thickness(12),
        CornerRadius = new CornerRadius(8),
        BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
        Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                new TextBlock { Text = body, Opacity = 0.7, TextWrapping = TextWrapping.Wrap },
            },
        },
    };

    private async Task ImportAsync()
    {
        try
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".json");
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            var bytes = await Windows.Storage.FileIO.ReadBufferAsync(file);
            var data = new byte[bytes.Length];
            using var reader = Windows.Storage.Streams.DataReader.FromBuffer(bytes);
            reader.ReadBytes(data);
            var summary = Library.Shared.ImportJson(data);
            var dialog = new ContentDialog
            {
                Title = Strings.Get("import"),
                Content = string.Format(Strings.Get("import_summary"), summary.Items, summary.Boards, summary.SkippedItems),
                CloseButtonText = Strings.Get("ok"),
                XamlRoot = ((FrameworkElement)Content).XamlRoot,
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                Title = Strings.Get("import"),
                Content = string.Format(Strings.Get("import_fail"), ex.Message),
                CloseButtonText = Strings.Get("ok"),
                XamlRoot = ((FrameworkElement)Content).XamlRoot,
            };
            await dialog.ShowAsync();
        }
    }

    private void Finish()
    {
        SettingsStore.Current.WelcomeCompleted = true;
        SettingsStore.Save();
        AppWindow.Closing -= Ignore;
        AppWindow.Hide();
    }

    private static void Ignore(AppWindow sender, AppWindowClosingEventArgs args) { }
}
