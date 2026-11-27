using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace SharpClip;

public sealed partial class HistoryWindow : Window
{
    private readonly HashSet<Guid> _selected = [];
    private Guid? _board;
    private string? _type;
    private IntPtr _previous;
    private bool _shown;

    public HistoryWindow()
    {
        InitializeComponent();
        SearchBox.PlaceholderText = Strings.Get("search");
        AppWindow.Resize(new SizeInt32(960, 280));
        ExtendsContentIntoTitleBar = true;
        AppWindow.IsShownInSwitchers = false;
    }

    public void HideOffscreen() => AppWindow.Hide();

    public void Toggle()
    {
        if (_shown) HidePanel();
        else Present();
    }

    public void Present()
    {
        _previous = Win32.GetForegroundWindow();
        ReloadBoards();
        Reload();
        PlaceAtBottom();
        AppWindow.Show();
        Activate();
        SearchBox.Focus(FocusState.Programmatic);
        _shown = true;
    }

    public void Reload() => Render(Library.Shared.Search(SearchBox.Text, _board, _type));

    private void HidePanel()
    {
        AppWindow.Hide();
        _shown = false;
        _selected.Clear();
        SearchBox.Text = "";
    }

    private void PlaceAtBottom()
    {
        Win32.GetCursorPos(out var point);
        var monitor = Win32.MonitorFromPoint(point, Win32.MONITOR_DEFAULTTONEAREST);
        var info = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfo(monitor, ref info)) return;
        var work = info.rcWork;
        var width = Math.Min(1100, (int)((work.Right - work.Left) * 0.9));
        var height = 280;
        var x = work.Left + ((work.Right - work.Left) - width) / 2;
        var y = work.Bottom - height - 12;
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void ReloadBoards()
    {
        BoardBar.Children.Clear();
        BoardBar.Children.Add(BoardButton(Strings.Get("history"), null));
        foreach (var board in Library.Shared.Boards())
        {
            BoardBar.Children.Add(BoardButton(board.Name, board.Id));
        }
    }

    private Button BoardButton(string name, Guid? id)
    {
        var button = new Button { Content = name, Margin = new Thickness(0, 0, 6, 0) };
        button.Click += (_, _) =>
        {
            _board = id;
            Reload();
        };
        return button;
    }

    private void Render(IReadOnlyList<ClipItem> items)
    {
        CardRow.Children.Clear();
        foreach (var item in items)
        {
            var card = new Button
            {
                Width = 200,
                Height = 120,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Top,
            };
            var body = new StackPanel { Spacing = 4 };
            body.Children.Add(new TextBlock
            {
                Text = item.Display,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 3,
            });
            body.Children.Add(new TextBlock
            {
                Text = item.SourceApp ?? item.ContentType,
                Opacity = 0.6,
                FontSize = 12,
            });
            card.Content = body;
            if (_selected.Contains(item.Id))
            {
                card.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
                card.BorderThickness = new Thickness(2);
            }
            card.Click += (_, args) => OnCardClick(item, args);
            CardRow.Children.Add(card);
        }
    }

    private void OnCardClick(ClipItem item, RoutedEventArgs _)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
        var modified = ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
            || shift.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (modified)
        {
            if (!_selected.Add(item.Id)) _selected.Remove(item.Id);
            Reload();
            return;
        }
        _selected.Clear();
        _selected.Add(item.Id);
        CommitPaste(plain: shift.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down));
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Reload();

    private void OnSearchKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            if (_selected.Count > 0) _selected.Clear();
            else if (!string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Text = "";
            else HidePanel();
            e.Handled = true;
            return;
        }
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            var shift = Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            CommitPaste(plain: shift || SettingsStore.Current.AlwaysPlainText);
            e.Handled = true;
        }
        if (e.Key == Windows.System.VirtualKey.Delete && SearchBox.FocusState == FocusState.Unfocused)
        {
            Library.Shared.DeleteClips(_selected);
            _selected.Clear();
            Reload();
            e.Handled = true;
        }
    }

    private void CommitPaste(bool plain)
    {
        var items = Library.Shared.Search(SearchBox.Text, _board, _type)
            .Where(item => _selected.Count == 0 || _selected.Contains(item.Id))
            .Take(_selected.Count == 0 ? 1 : int.MaxValue)
            .ToList();
        if (items.Count == 0) return;
        var text = string.Join(Environment.NewLine, items.Select(item => item.Text).Where(text => !string.IsNullOrEmpty(text)));
        var previous = _previous;
        HidePanel();
        var usePlain = plain || SettingsStore.Current.AlwaysPlainText;
        if (SettingsStore.Current.PasteIntoActiveApp)
        {
            ClipboardCapture.PasteIntoPreviousApp(previous, text, usePlain);
        }
        else
        {
            ClipboardCapture.WriteText(text, usePlain);
        }
    }

    private void OnSizeChanged(object sender, WindowSizeChangedEventArgs args) { }
}
