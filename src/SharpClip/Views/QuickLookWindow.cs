using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace SharpClip;

internal sealed class QuickLookWindow : Window
{
    public bool IsOpen { get; private set; }

    private readonly Grid _root = new();
    private readonly TextBox _text = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 14,
        BorderThickness = new Thickness(0),
        Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
    };
    private readonly TextBlock _stats = new() { FontSize = 12, Opacity = 0.8, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Image _image = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _imageHost = new();
    private readonly ScaleTransform _scale = new() { ScaleX = 1, ScaleY = 1 };
    private readonly TranslateTransform _pan = new();
    private ClipItem? _item;
    private bool _editing;
    private Action? _saved;
    private double _zoom = 1;
    private bool _dragging;
    private Windows.Foundation.Point _dragAt;
    private double _dragX;
    private double _dragY;

    public QuickLookWindow()
    {
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = true;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        Title = "SharpClip";
        var group = new TransformGroup();
        group.Children.Add(_scale);
        group.Children.Add(_pan);
        _image.RenderTransform = group;
        _imageHost.Children.Add(_image);
        _imageHost.PointerWheelChanged += OnWheel;
        _imageHost.DoubleTapped += (_, _) => ResetZoom();
        _imageHost.PointerPressed += OnPanStart;
        _imageHost.PointerMoved += OnPanMove;
        _imageHost.PointerReleased += OnPanEnd;
        _text.TextChanged += (_, _) => RefreshStats();
        _text.PreviewKeyDown += OnTextKey;
        var stack = new StackPanel { Padding = new Thickness(16) };
        stack.Children.Add(_text);
        stack.Children.Add(_stats);
        _root.Children.Add(_imageHost);
        _root.Children.Add(stack);
        _root.PreviewKeyDown += OnRootKey;
        _root.Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1C, 0x1C, 0x1E));
        Content = _root;
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            Dismiss();
        };
    }

    public void Present(Window owner, ClipItem item, bool edit, Action? saved)
    {
        _saved = saved;
        ShowItem(item, edit);
        var ownerHwnd = WindowNative.GetWindowHandle(owner);
        var dpi = ownerHwnd == IntPtr.Zero ? 96 : Win32.GetDpiForWindow(ownerHwnd);
        var scale = (dpi == 0 ? 96 : dpi) / 96.0;
        var width = (int)Math.Round(520 * scale);
        var height = (int)Math.Round(340 * scale);
        var panel = owner.AppWindow;
        var x = panel.Position.X + Math.Max(12, (panel.Size.Width - width) / 2);
        var y = panel.Position.Y - height - (int)Math.Round(12 * scale);
        if (y < 8) y = panel.Position.Y + (int)Math.Round(16 * scale);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        AppWindow.Show();
        IsOpen = true;
        Activate();
        if (_editing) _text.Focus(FocusState.Programmatic);
    }

    public void ShowItem(ClipItem item, bool edit)
    {
        _item = item;
        _editing = edit && item.ContentType is "plainText" or "richText" or "html" or "url";
        var image = item.ContentType == "image";
        _imageHost.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
        _text.Visibility = image ? Visibility.Collapsed : Visibility.Visible;
        _stats.Visibility = image ? Visibility.Collapsed : Visibility.Visible;
        if (image)
        {
            ResetZoom();
            _image.Source = Bitmap(item.Raw);
        }
        else
        {
            _text.IsReadOnly = !_editing;
            _text.Text = item.Text ?? "";
            RefreshStats();
        }
        var dark = SettingsStore.RequestedTheme() != ElementTheme.Light && (SettingsStore.RequestedTheme() == ElementTheme.Dark || Application.Current.RequestedTheme == ApplicationTheme.Dark);
        _root.Background = new SolidColorBrush(dark ? Color.FromArgb(0xF2, 0x1C, 0x1C, 0x1E) : Color.FromArgb(0xF8, 0xF7, 0xF7, 0xF9));
        _text.Foreground = new SolidColorBrush(dark ? Color.FromArgb(255, 0xE0, 0xE0, 0xE0) : Color.FromArgb(255, 0x33, 0x33, 0x33));
        _stats.Foreground = _text.Foreground;
    }

    public void Dismiss()
    {
        IsOpen = false;
        _editing = false;
        AppWindow.Hide();
    }

    private void OnRootKey(object sender, KeyRoutedEventArgs e)
    {
        if (_item?.ContentType == "image")
        {
            if (e.Key is VirtualKey.Add || (int)e.Key == 0xBB) { ZoomBy(1.1); e.Handled = true; return; }
            if (e.Key is VirtualKey.Subtract || (int)e.Key == 0xBD) { ZoomBy(1 / 1.1); e.Handled = true; return; }
            if (e.Key is VirtualKey.Number0 or VirtualKey.NumberPad0) { ResetZoom(); e.Handled = true; return; }
        }
        if (_editing) return;
        if (e.Key == VirtualKey.Escape || (e.Key == VirtualKey.Space && !PhysicalShift()))
        {
            Dismiss();
            e.Handled = true;
        }
    }

    private void OnTextKey(object sender, KeyRoutedEventArgs e)
    {
        if (!_editing)
        {
            if (e.Key == VirtualKey.Escape || e.Key == VirtualKey.Space)
            {
                Dismiss();
                e.Handled = true;
            }
            return;
        }
        if (e.Key == VirtualKey.Escape && !Composing())
        {
            Dismiss();
            e.Handled = true;
            return;
        }
        if (e.Key == VirtualKey.Enter && !PhysicalShift() && !Composing())
        {
            Save();
            e.Handled = true;
        }
    }

    private void Save()
    {
        if (_item is null) return;
        Library.Shared.UpdateText(_item.Id, _text.Text ?? "");
        _saved?.Invoke();
        Dismiss();
    }

    private void RefreshStats()
    {
        var text = _text.Text ?? "";
        var lines = text.Length == 0 ? 0 : text.Split('\n').Length;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        _stats.Text = string.Format(Strings.Get("stats"), text.Length, words, lines);
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(_imageHost).Properties.MouseWheelDelta;
        ZoomBy(delta > 0 ? 1.1 : 1 / 1.1);
        e.Handled = true;
    }

    private void ZoomBy(double factor)
    {
        _zoom = Math.Clamp(_zoom * factor, 0.2, 8);
        _scale.ScaleX = _zoom;
        _scale.ScaleY = _zoom;
    }

    private void ResetZoom()
    {
        _zoom = 1;
        _scale.ScaleX = 1;
        _scale.ScaleY = 1;
        _pan.X = 0;
        _pan.Y = 0;
    }

    private void OnPanStart(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        _dragAt = e.GetCurrentPoint(_imageHost).Position;
        _dragX = _pan.X;
        _dragY = _pan.Y;
        _imageHost.CapturePointer(e.Pointer);
    }

    private void OnPanMove(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        var point = e.GetCurrentPoint(_imageHost).Position;
        _pan.X = _dragX + point.X - _dragAt.X;
        _pan.Y = _dragY + point.Y - _dragAt.Y;
    }

    private void OnPanEnd(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        _imageHost.ReleasePointerCapture(e.Pointer);
    }

    private bool Composing()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var context = Win32.ImmGetContext(hwnd);
        if (context == IntPtr.Zero) return false;
        try
        {
            return Win32.ImmGetCompositionString(context, Win32.GCS_COMPSTR, null, 0) > 0;
        }
        finally
        {
            Win32.ImmReleaseContext(hwnd, context);
        }
    }

    private static bool PhysicalShift() => (Win32.GetAsyncKeyState(0x10) & 0x8000) != 0;

    private static BitmapImage? Bitmap(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.SetSource(stream.AsRandomAccessStream());
            return image;
        }
        catch
        {
            return null;
        }
    }
}
