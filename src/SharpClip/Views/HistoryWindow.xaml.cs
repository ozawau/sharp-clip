using System.Numerics;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace SharpClip;

public sealed partial class HistoryWindow : Window
{
    private static readonly Color[] TabColors =
    [
        C(0xF0, 0x4D, 0x4A),
        C(0xF8, 0x94, 0x29),
        C(0xF5, 0xC4, 0x1A),
        C(0x40, 0xBA, 0x66),
        C(0x21, 0xAD, 0xBF),
        C(0x3B, 0x71, 0xF3),
        C(0x73, 0x5C, 0xE6),
        C(0xB8, 0x5C, 0xDB),
        C(0xED, 0x5C, 0x9E),
    ];

    private static ImageSource? _checks;
    private QuickLookWindow? _quick;
    private readonly HashSet<Guid> _selected = [];
    private bool _lockMove;
    private IntPtr _prevProc;
    private WndProc? _wndProc;
    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private readonly HashSet<string> _types = [];
    private readonly DispatcherTimer _hint = new() { Interval = TimeSpan.FromMilliseconds(120) };

    private TextBox? _search;
    private Button? _typeButton;
    private Button? _dateButton;
    private Button? _clearFilters;
    private List<ClipItem> _items = [];
    private Guid? _board;
    private int _cursor = -1;
    private int _anchor;
    private string _date = "all";
    private IntPtr _previous;
    private IntPtr _previousFocus;
    private int _dialogs;
    private int _dragTick;
    private bool _cardDrag;
    private Point _pressAt;
    private Point _grab;
    private Border? _dragSource;
    private string _dragPayload = "";
    private readonly List<(Button Button, Guid Board)> _dropTabs = new();

    public bool IsDialogOpen => _dialogs > 0;
    private bool _shown;
    private bool _building;
    private bool _ctrlHint;
    private bool _suppressTap;
    private bool _clearing;
    private int _motionGen;
    private Storyboard? _motion;

    public HistoryWindow()
    {
        InitializeComponent();
        Title = "SharpClip";
        _hint.Tick += (_, _) =>
        {
            _hint.Stop();
            _ctrlHint = true;
            BuildTabs();
        };
        ApplyAppearance();
        Chrome();
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Hide();
    }

    public void Preheat()
    {
        PlaceAtBottom();
        BuildChrome();
        ApplyAppearance();
        AppWindow.Hide();
    }

    public void ForceClose()
    {
        _shown = false;
        OutsideClickMonitor.Stop();
        AppWindow.Hide();
        try
        {
            Close();
        }
        catch
        {
        }
    }

    public void OnDisplayChanged()
    {
        if (!_shown)
        {
            if (MissesEveryMonitor()) App.Instance.RecreateHistory();
            return;
        }
        HidePanel(restoreFocus: false, animate: false);
        Present();
    }

    private bool MissesEveryMonitor()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero || !Win32.GetWindowRect(hwnd, out var rect)) return false;
        if (rect.Right <= rect.Left || rect.Bottom <= rect.Top) return false;
        return Win32.MonitorFromRect(ref rect, Win32.MONITOR_DEFAULTTONULL) == IntPtr.Zero;
    }

    public bool IsShown => _shown;

    public void HideOffscreen() => AppWindow.Hide();

    public void Toggle()
    {
        if (_shown && !AppWindow.IsVisible)
        {
            App.Instance.RecreateHistory().Present();
            return;
        }
        if (_shown) HidePanel();
        else Present();
    }

    public void Present()
    {
        var already = _shown;
        _motionGen++;
        _motion?.Stop();
        _motion = null;
        _previous = Win32.GetForegroundWindow();
        _previousFocus = FocusedControl(_previous);
        _selected.Clear();
        _cursor = -1;
        HidePreview();
        ApplyAppearance();
        BuildChrome();
        BuildTabs();
        Render();
        PlaceAtBottom();
        var height = Root.ActualHeight > 1 ? Root.ActualHeight : 360;
        SetPanelShadow(false);
        var motion = !already && SettingsStore.MotionAllowed();
        _lockMove = motion;
        if (motion) Slide.Y = height;
        _shown = true;
        AppWindow.Show();
        JoinCurrentDesktop(WindowNative.GetWindowHandle(this), _previous);
        LeaveSearchUnfocused();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, LeaveSearchUnfocused);
        if (motion)
        {
            var gen = _motionGen;
            _motion = CreateMotion(0, 250, EasingMode.EaseOut);
            _motion.Completed += (_, _) =>
            {
                if (gen != _motionGen) return;
                Slide.Y = 0;
                _lockMove = false;
                SetPanelShadow(true);
            };
            _motion.Begin();
        }
        else
        {
            Slide.Y = 0;
            _lockMove = false;
            SetPanelShadow(true);
        }
        var queue = DispatcherQueue;
        OutsideClickMonitor.Start((x, y) => queue.TryEnqueue(() => OnOutsideClick(x, y)));
    }

    public void RefreshChrome()
    {
        ApplyAppearance();
        if (!_shown) return;
        BuildChrome();
        BuildTabs();
        Render();
    }

    public void ApplyAppearance()
    {
        Root.RequestedTheme = SettingsStore.RequestedTheme();
        var dark = Root.ActualTheme == ElementTheme.Dark;
        try
        {
            SystemBackdrop = dark
                ? new MicaBackdrop { Kind = MicaKind.BaseAlt }
                : new DesktopAcrylicBackdrop();
            Root.Background = new SolidColorBrush(dark
                ? Color.FromArgb(0x72, 0x10, 0x10, 0x12)
                : Color.FromArgb(0x66, 0xF7, 0xF7, 0xF9));
        }
        catch
        {
            Root.Background = new SolidColorBrush(dark ? C(0x1C, 0x1C, 0x1E) : C(0xF3, 0xF3, 0xF5));
        }
    }

    public void RequestClear()
    {
        if (_shown) _ = ClearHistoryAsync();
    }

    public void DismissForSettings()
    {
        if (_shown) HidePanel(restoreFocus: false, animate: false);
    }

    public void Reload()
    {
        if (_shown) Render();
    }

    private void Chrome()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        var hwnd = WindowNative.GetWindowHandle(this);
        var style = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, style | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST);
        var corner = Win32.DWMWCP_ROUND;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, 4);
        InstallMoveLock(hwnd);
    }

    private void InstallMoveLock(IntPtr hwnd)
    {
        if (_prevProc != IntPtr.Zero || hwnd == IntPtr.Zero) return;
        _wndProc = PanelProc;
        _prevProc = Win32.SetWindowLongPtr(hwnd, Win32.GWLP_WNDPROC, System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_wndProc));
    }

    private IntPtr PanelProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Win32.WM_WINDOWPOSCHANGING && _lockMove && lParam != IntPtr.Zero)
        {
            var pos = System.Runtime.InteropServices.Marshal.PtrToStructure<Win32.WINDOWPOS>(lParam);
            pos.flags |= 0x0002;
            System.Runtime.InteropServices.Marshal.StructureToPtr(pos, lParam, false);
        }
        return Win32.CallWindowProc(_prevProc, hwnd, msg, wParam, lParam);
    }

    private static void JoinCurrentDesktop(IntPtr hwnd, IntPtr neighbor)
    {
        if (hwnd == IntPtr.Zero || neighbor == IntPtr.Zero) return;
        try
        {
            var manager = (IVirtualDesktopManager)new VirtualDesktopManager();
            if (manager.GetWindowDesktopId(neighbor, out var desktop) == 0 && desktop != Guid.Empty)
                manager.MoveWindowToDesktop(hwnd, ref desktop);
        }
        catch
        {
            // Public virtual-desktop API can move onto the current desktop only.
        }
    }

    private static IntPtr FocusedControl(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        var thread = Win32.GetWindowThreadProcessId(hwnd, out _);
        var info = new Win32.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.GUITHREADINFO>() };
        return Win32.GetGUIThreadInfo(thread, ref info) ? info.hwndFocus : IntPtr.Zero;
    }

    private bool Dark => Root.ActualTheme == ElementTheme.Dark;

    private void HidePanel(bool restoreFocus = true, bool animate = true)
    {
        if (!_shown) return;
        OutsideClickMonitor.Stop();
        var previous = _previous;
        if (!animate || !SettingsStore.MotionAllowed())
        {
            FinishHide(restoreFocus, previous);
            return;
        }

        _shown = false;
        _quick?.Dismiss();
        _lockMove = true;
        SetPanelShadow(false);
        _motionGen++;
        _motion?.Stop();
        var gen = _motionGen;
        var height = Root.ActualHeight > 1 ? Root.ActualHeight : 360;
        var motion = CreateMotion(-height, 200, EasingMode.EaseIn);
        _motion = motion;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(500);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (gen != _motionGen) return;
            FinishHide(restoreFocus, previous);
        };
        motion.Completed += (_, _) =>
        {
            if (gen != _motionGen) return;
            timer.Stop();
            FinishHide(restoreFocus, previous);
        };
        timer.Start();
        motion.Begin();
    }

    private void SetPanelShadow(bool on)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero) return;
        var policy = on ? Win32.DWMNCRP_ENABLED : Win32.DWMNCRP_DISABLED;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_NCRENDERING_POLICY, ref policy, 4);
    }

    private void FinishHide(bool restoreFocus, IntPtr previous)
    {
        _motion = null;
        _lockMove = false;
        _quick?.Dismiss();
        Slide.Y = 0;
        Root.Opacity = 1;
        AppWindow.Hide();
        _shown = false;
        _selected.Clear();
        _cursor = -1;
        HidePreview();
        if (_search is not null) _search.Text = "";
        if (restoreFocus && previous != IntPtr.Zero) Win32.SetForegroundWindow(previous);
    }

    private Storyboard CreateMotion(double toY, int milliseconds, EasingMode easing)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(milliseconds));
        var ease = new CubicEase { EasingMode = easing };
        var y = new DoubleAnimation { To = toY, Duration = duration, EasingFunction = ease, EnableDependentAnimation = true };
        Storyboard.SetTarget(y, Slide);
        Storyboard.SetTargetProperty(y, "Y");
        var board = new Storyboard();
        board.Children.Add(y);
        return board;
    }

    /// <summary>
    /// Same rule as Clipbara's global click monitor: a left press, right press,
    /// or left release whose pointer is outside the panel hides it. Clicks that
    /// land on this process (the panel, a menu, or a dialog) stay.
    /// </summary>
    private void OnOutsideClick(int x, int y)
    {
        if (!_shown || TrayIcon.HitsIcon(x, y) || IsPanelClick(x, y)) return;
        HidePanel(restoreFocus: false);
    }

    /// <summary>
    /// A click on the panel, its menus, or a dialog stays. Only a press that
    /// lands on another program dismisses, matching Clipbara's global monitor.
    /// </summary>
    private bool IsPanelClick(int x, int y)
    {
        var panel = WindowNative.GetWindowHandle(this);
        if (Win32.GetWindowRect(panel, out var rect)
            && x >= rect.Left && x < rect.Right
            && y >= rect.Top && y < rect.Bottom)
        {
            return true;
        }
        var hit = Win32.WindowFromPoint(new Win32.POINT { X = x, Y = y });
        Win32.GetWindowThreadProcessId(hit, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    private void PlaceAtBottom()
    {
        Win32.GetCursorPos(out var point);
        var monitor = Win32.MonitorFromPoint(point, Win32.MONITOR_DEFAULTTONEAREST);
        var info = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfo(monitor, ref info)) return;
        var hwnd = WindowNative.GetWindowHandle(this);
        var dpi = Win32.GetDpiForWindow(hwnd);
        var scale = (dpi == 0 ? 96 : dpi) / 96.0;
        var work = info.rcWork;
        var height = (int)Math.Round(360 * scale);
        AppWindow.MoveAndResize(new RectInt32(work.Left, work.Bottom - height, work.Right - work.Left, height));
    }

    private void BuildChrome()
    {
        var keptQuery = _search?.Text ?? "";
        _building = true;
        FilterBar.Children.Clear();
        SideBar.Children.Clear();
        ActionBar.Children.Clear();

        var searchHost = new Border
        {
            Width = 220,
            Height = 30,
            CornerRadius = new CornerRadius(8),
            Background = SearchBrush(),
            Padding = new Thickness(10, 0, 8, 0),
            BorderBrush = LineBrush(),
            BorderThickness = new Thickness(0.75),
        };
        var searchRow = new Grid();
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.Children.Add(Glyph("\uE721", 12, MutedBrush()));
        _search = new TextBox
        {
            PlaceholderText = Strings.Get("search"),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            FontSize = 13,
            MinHeight = 0,
            Padding = new Thickness(8, 5, 4, 0),
            VerticalAlignment = VerticalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Foreground = ActiveBrush(),
        };
        _search.Loaded += (_, _) => _search.MinHeight = 0;
        _search.Text = keptQuery;
        _search.TextChanged += OnSearchChanged;
        Grid.SetColumn(_search, 1);
        searchRow.Children.Add(_search);
        searchHost.Child = searchRow;
        FilterBar.Children.Add(searchHost);

        _typeButton = Pill(TypeLabel(), _types.Count > 0);
        _typeButton.MinWidth = 108;
        _typeButton.Flyout = TypeFlyout();
        FilterBar.Children.Add(_typeButton);

        _dateButton = Pill(DateLabel(), _date != "all");
        _dateButton.Flyout = DateFlyout();
        FilterBar.Children.Add(_dateButton);

        _clearFilters = IconButton("\uE71C");
        _clearFilters.Visibility = FiltersOn ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(_clearFilters, Strings.Get("clear_filters"));
        _clearFilters.Click += (_, _) =>
        {
            _types.Clear();
            _date = "all";
            BuildChrome();
            Render();
        };
        FilterBar.Children.Add(_clearFilters);

        var settings = IconButton("\uE713");
        ToolTipService.SetToolTip(settings, Strings.Get("settings"));
        settings.Click += (_, _) => App.Instance.ShowSettings();
        SideBar.Children.Add(settings);

        var more = IconButton("\uE712");
        more.Flyout = OptionsFlyout();
        ActionBar.Children.Add(more);

        var add = IconButton("\uE710");
        ToolTipService.SetToolTip(add, Strings.Get("new_board"));
        add.Click += async (_, _) => await CreateBoardAsync();
        ActionBar.Children.Add(add);

        if (_board is null)
        {
            var trash = IconButton("\uE74D");
            ToolTipService.SetToolTip(trash, Strings.Get("clear"));
            trash.Click += async (_, _) => await ClearHistoryAsync();
            ActionBar.Children.Add(trash);
        }

        _building = false;
    }

    private void BuildTabs()
    {
        TabBar.Children.Clear();
        _dropTabs.Clear();
        var boards = Library.Shared.Boards();
        var historyColor = ParseHex(SettingsStore.Current.HistoryTabColor) ?? TabColors[5];
        var history = MakeTab(Strings.Get("history"), historyColor, _board is null, 1, () => SelectBoard(null));
        var historyMenu = new MenuFlyout();
        historyMenu.Items.Add(Item(Strings.Get("change_color"), () => ShowColorPalette(history, historyColor, hex =>
        {
            SettingsStore.Current.HistoryTabColor = hex;
            SettingsStore.Save();
            if (ParseHex(hex) is Color picked) PaintTabDot(history, picked);
        })));
        history.ContextFlyout = historyMenu;
        TabBar.Children.Add(history);
        if (boards.Count > 0)
        {
            TabBar.Children.Add(new Border
            {
                Width = 1,
                Height = 18,
                Margin = new Thickness(4, 0, 4, 0),
                Background = LineBrush(),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        for (var i = 0; i < boards.Count; i++)
        {
            var board = boards[i];
            var number = i + 2 <= 9 ? i + 2 : (int?)null;
            var tab = MakeTab(board.Name, TabColors[i % TabColors.Length], _board == board.Id, number, () => SelectBoard(board.Id), board.Id);
            var menu = new MenuFlyout();
            menu.Items.Add(Item(Strings.Get("rename_board"), async () => await RenameBoardAsync(board)));
            var current = ParseHex(board.Color) ?? TabColors[i % TabColors.Length];
            var anchor = tab;
            var boardId = board.Id;
            menu.Items.Add(Item(Strings.Get("change_color"), () => ShowColorPalette(anchor, current, hex =>
            {
                Library.Shared.SetBoardColor(boardId, hex);
                if (ParseHex(hex) is Color picked) PaintTabDot(anchor, picked);
            })));
            menu.Items.Add(Item(Strings.Get("delete_board"), async () => await DeleteBoardAsync(board)));
            tab.ContextFlyout = menu;
            TabBar.Children.Add(tab);
        }
        if (boards.Count < 8)
        {
            var add = IconButton("\uE710");
            ToolTipService.SetToolTip(add, Strings.Get("new_board"));
            add.Click += async (_, _) => await CreateBoardAsync();
            TabBar.Children.Add(add);
        }
    }

    private void SelectBoard(Guid? id)
    {
        _board = id;
        _selected.Clear();
        _cursor = -1;
        HidePreview();
        BuildTabs();
        BuildChrome();
        Render();
    }

    private void SelectTabByIndex(int index)
    {
        if (index == 0)
        {
            SelectBoard(null);
            return;
        }
        var boards = Library.Shared.Boards();
        var at = index - 1;
        if (at >= 0 && at < boards.Count) SelectBoard(boards[at].Id);
    }

    private void Render()
    {
        var query = _search?.Text ?? "";
        var source = Library.Shared.Search(query, _board, null);
        _items = source.Where(Matches).ToList();
        EnsureSelection();
        CardRow.Children.Clear();

        var searching = !string.IsNullOrWhiteSpace(query) || FiltersOn;
        EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CardScroller.Visibility = _items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = searching ? Strings.Get("no_results") : Strings.Get("empty_title");
        EmptyBody.Text = searching ? Strings.Get("no_results_body") : Strings.Get("empty_body");
        EmptyTitle.Foreground = ActiveBrush();

        var cardH = 220.0;
        var cardW = 260.0;
        for (var i = 0; i < _items.Count; i++)
        {
            CardRow.Children.Add(MakeCard(_items[i], i, cardW, cardH));
        }
        if (_cursor >= 0 && _cursor < CardRow.Children.Count && CardRow.Children[_cursor] is FrameworkElement card)
        {
            card.StartBringIntoView();
        }
    }

    private bool Matches(ClipItem item)
    {
        if (_types.Count > 0 && !_types.Contains(item.ContentType)) return false;
        if (_date == "all") return true;
        var local = item.CopiedAt.ToLocalTime();
        var now = DateTimeOffset.Now;
        return _date switch
        {
            "today" => local.Date == now.Date,
            "week" => local.Date >= now.Date.AddDays(-6),
            "month" => local.Date >= now.Date.AddDays(-29),
            _ => true,
        };
    }

    private static DateTimeOffset StartOfWeek(DateTimeOffset now)
    {
        var diff = (7 + (int)now.DayOfWeek - (int)DayOfWeek.Monday) % 7;
        return now.Date.AddDays(-diff);
    }

    private UIElement MakeCard(ClipItem item, int index, double width, double height)
    {
        var selected = _selected.Contains(item.Id);
        var style = TypeStyle(item.ContentType, item.Text);
        var card = new Border
        {
            Width = width,
            Height = height,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(selected ? SelectedFill() : CardFill()),
            BorderBrush = new SolidColorBrush(selected ? C(0x3B, 0x71, 0xF3) : CardEdge()),
            BorderThickness = new Thickness(selected ? 2.25 : 0.75),
            Padding = new Thickness(10),
        };

        card.Child = CardContents(item, style);
        Lift(card, false, selected);

        card.SizeChanged += (_, e) =>
            card.CenterPoint = new Vector3((float)e.NewSize.Width / 2f, (float)e.NewSize.Height / 2f, 0);
        var host = new Grid
        {
            Width = width,
            Height = height + 24,
            Children = { card },
        };
        card.PointerEntered += (_, _) =>
        {
            Canvas.SetZIndex(host, 1);
            if (!PhysicalCtrl() && !PhysicalShift()) FocusCard(index);
            Lift(card, true, true);
            if (!_selected.Contains(item.Id))
            {
                card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x73, 0x3B, 0x71, 0xF3));
                card.BorderThickness = new Thickness(1.75);
            }
        };
        card.PointerExited += (_, _) =>
        {
            Canvas.SetZIndex(host, 0);
            var on = _selected.Contains(item.Id) || _cursor == index;
            Lift(card, false, on);
            card.BorderBrush = new SolidColorBrush(on ? C(0x3B, 0x71, 0xF3) : CardEdge());
            card.BorderThickness = new Thickness(on ? 2.25 : 0.75);
        };
        card.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) return;
            if (InsideButton(e.OriginalSource as DependencyObject)) return;
            var ids = _selected.Contains(item.Id) ? _selected.ToArray() : new[] { item.Id };
            _dragPayload = "sharpclip-clip\n" + string.Join('\n', ids);
            _pressAt = e.GetCurrentPoint(Root).Position;
            _cardDrag = false;
        };
        card.PointerMoved += (_, e) =>
        {
            if (_dragPayload.Length == 0) return;
            var point = e.GetCurrentPoint(Root).Position;
            if (!e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) return;
            if (!_cardDrag && Math.Abs(point.X - _pressAt.X) + Math.Abs(point.Y - _pressAt.Y) > 8)
            {
                _cardDrag = true;
                _dragTick = Environment.TickCount;
                card.CapturePointer(e.Pointer);
                BeginCardDrag(card, item, style, point);
            }
            if (_cardDrag)
            {
                MoveCardDrag(point);
                HighlightDrop(point);
            }
        };
        card.PointerReleased += (_, e) =>
        {
            if (!_cardDrag) return;
            var point = e.GetCurrentPoint(Root).Position;
            var payload = _dragPayload;
            _cardDrag = false;
            _dragPayload = "";
            _dragTick = Environment.TickCount;
            EndCardDrag();
            ClearDropHighlight();
            if (card.PointerCaptures.Any(pointer => pointer.PointerId == e.Pointer.PointerId))
                card.ReleasePointerCapture(e.Pointer);
            if (DropBoard(point) is Guid board) AcceptClipDrop(board, payload);
            e.Handled = true;
        };
        card.PointerCaptureLost += (_, _) =>
        {
            if (!_cardDrag && _dragSource is null) return;
            _cardDrag = false;
            _dragPayload = "";
            EndCardDrag();
            ClearDropHighlight();
        };
        card.Tapped += (_, e) =>
        {
            if (_suppressTap || Environment.TickCount - _dragTick < 500)
            {
                _suppressTap = false;
                e.Handled = true;
                return;
            }
            OnCard(index);
            e.Handled = true;
        };
        card.ContextFlyout = CardMenu(item);
        card.CanDrag = true;
        card.DragStarting += (_, args) =>
        {
            var package = args.Data;
            package.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            if (item.ContentType == "fileURL")
            {
                package.SetText(item.Text ?? "");
                return;
            }
            if (item.ContentType == "image" && item.Raw.Length > 0)
            {
                var stream = new MemoryStream(item.Raw);
                package.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(stream.AsRandomAccessStream()));
                return;
            }
            package.SetText(item.Text ?? item.Display);
        };
        return host;
    }

    private Border? CardBorder(int index)
    {
        if (index < 0 || index >= CardRow.Children.Count) return null;
        return CardRow.Children[index] is Grid host ? host.Children.OfType<Border>().FirstOrDefault() : CardRow.Children[index] as Border;
    }

    private void FocusCard(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        if (_cursor == index && _selected.Count == 1 && _selected.Contains(_items[index].Id)) return;
        _selected.Clear();
        _selected.Add(_items[index].Id);
        _cursor = index;
        _anchor = index;
        for (var i = 0; i < _items.Count; i++)
        {
            var border = CardBorder(i);
            if (border is null) continue;
            var on = i == index;
            border.Background = new SolidColorBrush(on ? SelectedFill() : CardFill());
            border.BorderBrush = new SolidColorBrush(on ? C(0x3B, 0x71, 0xF3) : CardEdge());
            border.BorderThickness = new Thickness(on ? 2.25 : 0.75);
            if (!on) Lift(border, false, false);
        }
    }

    private Grid CardContents(ClipItem item, TypeLook style)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(CardHeader(item, style));
        var body = CardBody(item);
        Grid.SetRow(body, 1);
        grid.Children.Add(body);
        var footer = CardFooter(item, style.Label);
        Grid.SetRow(footer, 2);
        grid.Children.Add(footer);
        return grid;
    }

    private void BeginCardDrag(Border card, ClipItem item, TypeLook style, Point point)
    {
        var origin = card.TransformToVisual(Root).TransformPoint(new Point(0, 0));
        _grab = new Point(point.X - origin.X, point.Y - origin.Y);
        _dragSource = card;
        card.Opacity = 0.32;
        card.Scale = Vector3.One;
        card.Translation = Vector3.Zero;

        var ghost = new Border
        {
            Width = card.Width,
            Height = card.Height,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(CardFill()),
            BorderBrush = new SolidColorBrush(C(0x3B, 0x71, 0xF3)),
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(10, 8, 10, 8),
            Opacity = 0.5,
            IsHitTestVisible = false,
            Child = CardContents(item, style),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform { ScaleX = 1, ScaleY = 1 },
        };
        var host = new Grid
        {
            Width = card.Width,
            Height = card.Height,
            IsHitTestVisible = false,
            Children = { ghost },
        };
        DragLayer.Children.Clear();
        DragLayer.Children.Add(host);
        Canvas.SetLeft(host, origin.X);
        Canvas.SetTop(host, origin.Y);
        MoveCardDrag(point);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scaleX = new DoubleAnimation { To = 0.94, Duration = TimeSpan.FromMilliseconds(160), EasingFunction = ease };
        var scaleY = new DoubleAnimation { To = 0.94, Duration = TimeSpan.FromMilliseconds(160), EasingFunction = ease };
        Storyboard.SetTarget(scaleX, ghost.RenderTransform);
        Storyboard.SetTarget(scaleY, ghost.RenderTransform);
        Storyboard.SetTargetProperty(scaleX, "ScaleX");
        Storyboard.SetTargetProperty(scaleY, "ScaleY");
        var board = new Storyboard();
        board.Children.Add(scaleX);
        board.Children.Add(scaleY);
        board.Begin();
    }

    private void MoveCardDrag(Point point)
    {
        if (DragLayer.Children.Count == 0) return;
        var host = DragLayer.Children[0];
        Canvas.SetLeft(host, point.X - _grab.X);
        Canvas.SetTop(host, point.Y - _grab.Y);
    }

    private void EndCardDrag()
    {
        if (_dragSource is not null)
        {
            _dragSource.Opacity = 1;
            _dragSource = null;
        }
        DragLayer.Children.Clear();
    }

    private FrameworkElement CardHeader(ClipItem item, TypeLook style)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(40, style.Tint.R, style.Tint.G, style.Tint.B)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 3, 7, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = style.Label,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(style.Tint),
            },
        };
        row.Children.Add(badge);
        var mark = AppMark(item.SourceBundle ?? item.SourceApp);
        mark.HorizontalAlignment = HorizontalAlignment.Right;
        mark.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(mark, 1);
        row.Children.Add(mark);
        return row;
    }

    private FrameworkElement CardBody(ClipItem item)
    {
        if (item.ContentType == "image")
        {
            var image = new Image
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var source = ImageFrom(item.Thumbnail ?? []);
            if (source is not null) image.Source = source;
            var layer = new Grid();
            layer.Children.Add(new Image { Source = Checkerboard(), Stretch = Stretch.Fill });
            layer.Children.Add(image);
            return new Border
            {
                CornerRadius = new CornerRadius(7),
                Margin = new Thickness(0, 2, 0, 6),
                Child = layer,
            };
        }

        if (item.ContentType == "color")
        {
            var hex = item.Text ?? "#808080";
            var stack = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = 72,
                Height = 72,
                RadiusX = 8,
                RadiusY = 8,
                Fill = new SolidColorBrush(ParseHex(hex) ?? C(0x80, 0x80, 0x80)),
            });
            stack.Children.Add(new TextBlock { Text = hex, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Foreground = BodyBrush() });
            return stack;
        }

        if (item.ContentType == "url")
        {
            var text = item.Text ?? "";
            var host = Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) ? uri.Host : text;
            var stack = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Top };
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            line.Children.Add(Glyph("\uE774", 14, new SolidColorBrush(C(0x3B, 0x71, 0xF3))));
            line.Children.Add(new TextBlock
            {
                Text = host,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = BodyBrush(),
            });
            stack.Children.Add(line);
            stack.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 3,
                Foreground = MutedBrush(),
            });
            return stack;
        }

        if (item.ContentType == "fileURL")
        {
            return Highlighted(item.Text ?? Strings.Get("file"), 4);
        }

        return Highlighted(item.Text ?? "", 4);
    }

    private FrameworkElement Highlighted(string text, int maxLines)
    {
        if (text.Length > 900) text = text[..900] + "...";
        var block = new Microsoft.UI.Xaml.Controls.RichTextBlock
        {
            FontSize = 12,
            MaxLines = maxLines,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = false,
            Foreground = BodyBrush(),
        };
        var paragraph = new Microsoft.UI.Xaml.Documents.Paragraph { LineHeight = 16 };
        var query = _search?.Text?.Trim() ?? "";
        if (query.Length == 0)
        {
            paragraph.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = text });
        }
        else
        {
            var rest = text;
            while (rest.Length > 0)
            {
                var at = rest.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                {
                    paragraph.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = rest });
                    break;
                }
                if (at > 0) paragraph.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = rest[..at] });
                paragraph.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
                {
                    Text = rest.Substring(at, query.Length),
                    Foreground = new SolidColorBrush(C(0x3B, 0x71, 0xF3)),
                });
                rest = rest[(at + query.Length)..];
            }
        }
        block.Blocks.Add(paragraph);
        return block;
    }

    private static ImageSource Checkerboard()
    {
        if (_checks is not null) return _checks;
        const int cells = 8;
        var bitmap = new WriteableBitmap(cells * 8, cells * 8);
        var pixels = new byte[cells * 8 * cells * 8 * 4];
        for (var y = 0; y < cells * 8; y++)
        {
            for (var x = 0; x < cells * 8; x++)
            {
                var light = ((x / 8) + (y / 8)) % 2 == 0;
                var i = (y * cells * 8 + x) * 4;
                pixels[i] = light ? (byte)255 : (byte)0xF5;
                pixels[i + 1] = light ? (byte)255 : (byte)0xF5;
                pixels[i + 2] = light ? (byte)255 : (byte)0xF5;
                pixels[i + 3] = 255;
            }
        }
        using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(pixels, 0, pixels.Length);
        bitmap.Invalidate();
        _checks = bitmap;
        return bitmap;
    }

    private FrameworkElement CardFooter(ClipItem item, string fallback)
    {
        var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var badge = new Border
        {
            Background = BadgeBrush(),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 4, 8, 4),
            Child = new TextBlock
            {
                Text = Age(item.CopiedAt),
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                Foreground = MutedBrush(),
            },
        };
        row.Children.Add(badge);
        var more = new Button
        {
            Content = Glyph("\uE712", 12, MutedBrush()),
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            Width = 24,
            Height = 18,
            Padding = new Thickness(0),
        };
        more.Click += (_, _) =>
        {
            _suppressTap = true;
            CardMenu(item).ShowAt(more);
        };
        Grid.SetColumn(more, 2);
        row.Children.Add(more);
        return row;
    }

    private MenuFlyout CardMenu(ClipItem item)
    {
        var menu = new MenuFlyout();
        var plainOk = item.ContentType is "plainText" or "richText" or "html" or "url";
        if (plainOk)
        {
            menu.Items.Add(Item(Strings.Get("paste_fmt"), () => PasteOne(item, false)));
            menu.Items.Add(Item(Strings.Get("paste_plain"), () => PasteOne(item, true)));
        }
        else
        {
            menu.Items.Add(Item(Strings.Get("paste_one"), () => PasteOne(item, false)));
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item(Strings.Get("rename"), async () => await RenameClipAsync(item)));
        if (item.ContentType is "plainText" or "richText" or "html" or "url")
        {
            menu.Items.Add(Item(Strings.Get("edit"), () => OpenEditor(item)));
        }
        if (item.ContentType == "url" && Uri.TryCreate(item.Text?.Trim(), UriKind.Absolute, out var link))
        {
            var address = link.AbsoluteUri;
            menu.Items.Add(Item(Strings.Get("open_link"), () =>
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(address) { UseShellExecute = true });
            }));
        }
        var boards = Library.Shared.Boards();
        if (boards.Count > 0)
        {
            var move = new MenuFlyoutSubItem { Text = Strings.Get("move_board") };
            var add = new MenuFlyoutSubItem { Text = Strings.Get("add_board") };
            foreach (var board in boards)
            {
                var onBoard = Library.Shared.BoardContains(board.Id, item.Id);
                var addItem = new ToggleMenuFlyoutItem { Text = board.Name, IsChecked = onBoard, IsEnabled = !onBoard };
                var target = board.Id;
                addItem.Click += (_, _) =>
                {
                    if (!onBoard) Library.Shared.AddToBoard(target, item.Id);
                };
                add.Items.Add(addItem);
                if (board.Id != _board)
                {
                    move.Items.Add(Item(board.Name, () =>
                    {
                        if (_board is Guid current) Library.Shared.RemoveFromBoard(current, item.Id);
                        Library.Shared.AddToBoard(target, item.Id);
                        Render();
                    }));
                }
            }
            if (move.Items.Count > 0) menu.Items.Add(move);
            menu.Items.Add(add);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item(_board is null ? Strings.Get("delete_clip") : Strings.Get("remove_board"), () =>
        {
            if (_board is Guid boardId) Library.Shared.RemoveFromBoard(boardId, item.Id);
            else Library.Shared.DeleteClips([item.Id]);
            _selected.Remove(item.Id);
            Render();
        }));
        return menu;
    }

    private void OnCard(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        var item = _items[index];
        if (PhysicalCtrl())
        {
            if (_selected.Contains(item.Id))
            {
                if (_selected.Count > 1) _selected.Remove(item.Id);
            }
            else
            {
                _selected.Add(item.Id);
            }
            _anchor = index;
            _cursor = index;
            Render();
            return;
        }
        if (PhysicalShift())
        {
            var from = Math.Min(_anchor, index);
            var to = Math.Max(_anchor, index);
            _selected.Clear();
            for (var i = from; i <= to && i < _items.Count; i++) _selected.Add(_items[i].Id);
            _cursor = index;
            Render();
            return;
        }
        _selected.Clear();
        _selected.Add(item.Id);
        _cursor = index;
        _anchor = index;
        CommitPaste(ShiftDown());
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_building) return;
        _cursor = -1;
        _selected.Clear();
        Render();
    }

    private void OnPreviewKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl)
        {
            if (!_ctrlHint) _hint.Start();
            return;
        }

        if (CtrlDown() && !ShiftDown() && !AltDown() && (int)PhysicalKey(e) == 0xBC)
        {
            App.Instance.ShowSettings();
            e.Handled = true;
            return;
        }
        if (CtrlDown() && e.Key is >= VirtualKey.Number1 and <= VirtualKey.Number9)
        {
            SelectTabByIndex((int)e.Key - (int)VirtualKey.Number1);
            e.Handled = true;
            return;
        }
        if (CtrlDown() && e.Key == VirtualKey.E)
        {
            _ = EditSelectedAsync();
            e.Handled = true;
            return;
        }
        if (IsQuickLook(PhysicalKey(e)))
        {
            TogglePreview();
            e.Handled = true;
            return;
        }
        if (e.Key == VirtualKey.Tab)
        {
            e.Handled = true;
            if (SearchHasFocus()) CardRow.Focus(FocusState.Programmatic);
            else _search?.Focus(FocusState.Programmatic);
            return;
        }
        if (e.Key == VirtualKey.Back && !SearchHasFocus() && !PhysicalCtrl())
        {
            DeleteSelected();
            e.Handled = true;
            return;
        }
        if (e.Key is VirtualKey.Left or VirtualKey.Right)
        {
            MoveCursor(e.Key == VirtualKey.Right ? 1 : -1, ShiftDown() || PhysicalShift());
            e.Handled = true;
            return;
        }
        if (PhysicalKey(e) == VirtualKey.Enter)
        {
            if (_dialogs > 0) return;
            CommitPaste(ShiftDown());
            e.Handled = true;
            return;
        }
        if (e.Key == VirtualKey.Delete && !SearchHasFocus())
        {
            DeleteSelected();
            e.Handled = true;
            return;
        }
        if (PhysicalCtrl() && e.Key == VirtualKey.Z && !SearchHasFocus())
        {
            if (Library.Shared.Undo()) Render();
            e.Handled = true;
            return;
        }
        if (e.Key == VirtualKey.Escape)
        {
            if (_dialogs > 0) return;
            HandleEscape();
            e.Handled = true;
            return;
        }
        if (!SearchHasFocus() && !CtrlDown() && !AltDown() && !WinDown())
        {
            var typed = TextFor((int)PhysicalKey(e));
            if (!string.IsNullOrEmpty(typed))
            {
                InsertSearch(typed);
                e.Handled = true;
            }
        }
    }

    /// <summary>
    /// Keys that arrived while the panel was open but another app still owned the keyboard.
    /// </summary>
    public void InjectKey(int vk)
    {
        if (!_shown || IsModifierKey(vk)) return;
        try
        {
            AcceptKey(vk);
        }
        catch
        {
            // A failed key must not take down the clipboard manager.
        }
    }

    private void AcceptKey(int vk)
    {
        if (vk == 0x1B)
        {
            HandleEscape();
            return;
        }
        if (vk == 0x0D)
        {
            CommitPaste(PhysicalShift());
            return;
        }
        if (PhysicalCtrl() && !PhysicalShift() && !PhysicalAlt() && vk == 0xBC)
        {
            App.Instance.ShowSettings();
            return;
        }
        if (PhysicalCtrl() && vk is >= 0x31 and <= 0x39)
        {
            SelectTabByIndex(vk - 0x31);
            return;
        }
        if (PhysicalCtrl() && vk == 0x45)
        {
            _ = EditSelectedAsync();
            return;
        }
        if (vk is 0x25 or 0x27)
        {
            MoveCursor(vk == 0x27 ? 1 : -1, PhysicalShift());
            return;
        }
        if (vk == 0x2E && !SearchHasFocus())
        {
            DeleteSelected();
            return;
        }
        if (PhysicalCtrl() && vk == 0x5A && !SearchHasFocus())
        {
            if (Library.Shared.Undo()) Render();
            return;
        }
        if (IsInjectedQuickLook(vk))
        {
            TogglePreview();
            return;
        }
        if (vk == 0x09)
        {
            if (SearchHasFocus()) CardRow.Focus(FocusState.Programmatic);
            else _search?.Focus(FocusState.Programmatic);
            return;
        }
        if (vk == 0x08 && !SearchHasFocus() && !PhysicalCtrl())
        {
            DeleteSelected();
            return;
        }
        if (_search is null) return;
        if (PhysicalCtrl() && vk == 0x41)
        {
            _search.SelectAll();
            return;
        }
        if (vk == 0x08)
        {
            if (_search.SelectionLength > 0)
            {
                var start = _search.SelectionStart;
                _search.Text = _search.Text.Remove(start, _search.SelectionLength);
                _search.SelectionStart = start;
            }
            else if (_search.SelectionStart > 0)
            {
                var start = _search.SelectionStart - 1;
                _search.Text = _search.Text.Remove(start, 1);
                _search.SelectionStart = start;
            }
            return;
        }
        var text = TextFor(vk);
        if (string.IsNullOrEmpty(text)) return;
        InsertSearch(text);
    }

    private void LeaveSearchUnfocused()
    {
        if (!_shown) return;
        try
        {
            Root.Focus(FocusState.Programmatic);
        }
        catch
        {
            // The panel still receives keys through the keyboard hook.
        }
    }

    private void FocusSearch()
    {
        if (!_shown || _search is null) return;
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero) return;
            var foreground = Win32.GetForegroundWindow();
            var foreThread = foreground == IntPtr.Zero ? 0u : Win32.GetWindowThreadProcessId(foreground, out _);
            var thisThread = Win32.GetCurrentThreadId();
            var attached = false;
            try
            {
                if (foreThread != 0 && foreThread != thisThread)
                    attached = Win32.AttachThreadInput(thisThread, foreThread, true);
                Win32.BringWindowToTop(hwnd);
                Win32.SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) Win32.AttachThreadInput(thisThread, foreThread, false);
            }
            Activate();
            _search.Focus(FocusState.Programmatic);
        }
        catch
        {
            // The typed character is still written into the search box.
        }
    }

    private void InsertSearch(string text)
    {
        if (_search is null || text.Length == 0) return;
        var wasFocused = SearchHasFocus();
        FocusSearch();
        var current = _search.Text ?? "";
        var caret = wasFocused ? Math.Clamp(_search.SelectionStart, 0, current.Length) : current.Length;
        var length = wasFocused ? Math.Max(0, _search.SelectionLength) : 0;
        if (caret + length > current.Length) length = 0;
        _search.Text = string.Concat(current.AsSpan(0, caret), text, current.AsSpan(caret + length));
        _search.SelectionStart = caret + text.Length;
    }

    private static string? TextFor(int vk)
    {
        if (PhysicalCtrl() || PhysicalAlt() || PhysicalWin()) return null;
        var shift = PhysicalShift();
        if (vk is >= 0x41 and <= 0x5A)
            return (shift ? (char)vk : char.ToLowerInvariant((char)vk)).ToString();
        if (vk is >= 0x30 and <= 0x39)
        {
            if (!shift) return ((char)vk).ToString();
            return ")!@#$%^&*("[vk - 0x30].ToString();
        }
        if (vk == 0x20) return " ";
        return (vk, shift) switch
        {
            (0xBD, false) => "-",
            (0xBD, true) => "_",
            (0xBB, false) => "=",
            (0xBB, true) => "+",
            (0xDB, false) => "[",
            (0xDB, true) => "{",
            (0xDD, false) => "]",
            (0xDD, true) => "}",
            (0xDC, false) => "\\",
            (0xDC, true) => "|",
            (0xBA, false) => ";",
            (0xBA, true) => ":",
            (0xDE, false) => "'",
            (0xDE, true) => "\"",
            (0xBC, false) => ",",
            (0xBC, true) => "<",
            (0xBE, false) => ".",
            (0xBE, true) => ">",
            (0xBF, false) => "/",
            (0xBF, true) => "?",
            (0xC0, false) => "`",
            (0xC0, true) => "~",
            _ => null,
        };
    }

    private static bool IsInjectedQuickLook(int vk)
    {
        var configured = SettingsStore.Current.QuickLookKey;
        if (configured == 0) configured = 0x20;
        return vk == configured && !PhysicalCtrl() && !PhysicalShift() && !PhysicalAlt() && !PhysicalWin();
    }

    private static bool IsModifierKey(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    private static bool PhysicalCtrl() => Physical(0x11) || Physical(0xA2) || Physical(0xA3);
    private static bool PhysicalShift() => Physical(0x10) || Physical(0xA0) || Physical(0xA1);
    private static bool PhysicalAlt() => Physical(0x12) || Physical(0xA4) || Physical(0xA5);
    private static bool PhysicalWin() => Physical(0x5B) || Physical(0x5C);
    private static bool Physical(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;

    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!CtrlDown())
        {
            _hint.Stop();
            if (_ctrlHint)
            {
                _ctrlHint = false;
                BuildTabs();
            }
        }
    }

    private void EnsureSelection()
    {
        if (_items.Count == 0)
        {
            _cursor = -1;
            _selected.Clear();
            return;
        }
        _selected.RemoveWhere(id => _items.FindIndex(item => item.Id == id) < 0);
        if (_cursor < 0 || _cursor >= _items.Count)
            _cursor = _items.FindIndex(item => _selected.Contains(item.Id));
        if (_cursor < 0) _cursor = 0;
        if (_selected.Count == 0) _selected.Add(_items[_cursor].Id);
        if (_anchor < 0 || _anchor >= _items.Count) _anchor = _cursor;
    }

    private void MoveCursor(int delta, bool extend)
    {
        if (_items.Count == 0) return;
        var next = _cursor < 0 ? 0 : Math.Clamp(_cursor + delta, 0, _items.Count - 1);
        _cursor = next;
        if (extend)
        {
            var from = Math.Min(_anchor, next);
            var to = Math.Max(_anchor, next);
            _selected.Clear();
            for (var i = from; i <= to; i++) _selected.Add(_items[i].Id);
        }
        else
        {
            _selected.Clear();
            _selected.Add(_items[next].Id);
            _anchor = next;
        }
        if (_quick?.IsOpen == true) _quick.ShowItem(_items[next], false);
        else if (PreviewLayer.Visibility == Visibility.Visible) ShowPreview(_items[next]);
        Render();
    }

    private void DeleteSelected()
    {
        var ids = SelectedItems().Select(item => item.Id).ToList();
        if (ids.Count == 0 && _cursor >= 0 && _cursor < _items.Count) ids.Add(_items[_cursor].Id);
        if (ids.Count == 0) return;
        if (_board is Guid board)
        {
            foreach (var id in ids) Library.Shared.RemoveFromBoard(board, id);
        }
        else
        {
            Library.Shared.DeleteClips(ids);
        }
        _selected.Clear();
        _cursor = -1;
        Render();
    }

    private void CommitPaste(bool plain)
    {
        var items = SelectedItems();
        if (items.Count == 0 && _items.Count > 0) items = [_items[0]];
        if (items.Count == 0) return;
        Library.Shared.Touch(items.Select(item => item.Id).ToList());
        var previous = _previous;
        var focus = _previousFocus;
        var usePlain = SettingsStore.Current.AlwaysPlainText ^ plain;
        HidePanel(restoreFocus: true, animate: false);
        bool landed;
        if (items.Count == 1)
        {
            landed = ClipboardCapture.PasteItemInto(previous, focus, items[0], usePlain);
        }
        else
        {
            var text = string.Join(Environment.NewLine, items.Select(item => item.Text).Where(part => !string.IsNullOrEmpty(part)));
            landed = ClipboardCapture.PasteIntoPreviousApp(previous, focus, text, usePlain);
        }
        App.Instance.NotifyPaste(landed);
    }

    private bool SearchHasFocus() => _search is not null && _search.FocusState != FocusState.Unfocused;

    private void HandleEscape()
    {
        if (_quick?.IsOpen == true || PreviewLayer.Visibility == Visibility.Visible)
        {
            _quick?.Dismiss();
            HidePreview();
            return;
        }
        if (_selected.Count > 1)
        {
            var keep = _cursor >= 0 && _cursor < _items.Count ? _items[_cursor].Id : _selected.First();
            _selected.Clear();
            _selected.Add(keep);
            Render();
            return;
        }
        if (!string.IsNullOrEmpty(_search?.Text))
        {
            _search!.Text = "";
            return;
        }
        if (_board is not null)
        {
            SelectBoard(null);
            return;
        }
        HidePanel();
    }

    private void PasteOne(ClipItem item, bool plain)
    {
        _selected.Clear();
        _selected.Add(item.Id);
        _cursor = _items.FindIndex(candidate => candidate.Id == item.Id);
        CommitPaste(plain);
    }

    private List<ClipItem> SelectedItems()
    {
        if (_selected.Count == 0) return [];
        return _items.Where(item => _selected.Contains(item.Id)).ToList();
    }

    private void TogglePreview()
    {
        if (_quick?.IsOpen == true || PreviewLayer.Visibility == Visibility.Visible)
        {
            _quick?.Dismiss();
            HidePreview();
            return;
        }
        var item = _cursor >= 0 && _cursor < _items.Count ? _items[_cursor] : _items.FirstOrDefault();
        if (item is null) return;
        OpenLook(item, false);
    }

    private void OpenEditor(ClipItem item) => OpenLook(item, true);

    private void OpenLook(ClipItem item, bool edit)
    {
        if (edit && item.ContentType is not ("plainText" or "richText" or "html" or "url")) return;
        _quick ??= new QuickLookWindow();
        _quick.Present(this, item, edit, () =>
        {
            Render();
            TrayIcon.Refresh();
        });
    }

    private void ReorderTab(Guid id, double x)
    {
        var order = Library.Shared.Boards().Select(board => board.Id).ToList();
        var from = order.IndexOf(id);
        if (from < 0) return;
        double cursor = 0;
        var pin = 0;
        var target = order.Count;
        var skippedHistory = false;
        foreach (var child in TabBar.Children)
        {
            if (child is not FrameworkElement element || element.ActualWidth <= 0) continue;
            if (!skippedHistory)
            {
                cursor += element.ActualWidth + 6;
                skippedHistory = true;
                continue;
            }
            if (element is Button && pin < order.Count && x < cursor + element.ActualWidth / 2)
            {
                target = pin;
                break;
            }
            if (element is Button)
            {
                cursor += element.ActualWidth + 6;
                pin++;
                target = pin;
            }
        }
        order.RemoveAt(from);
        if (target > from) target--;
        target = Math.Clamp(target, 0, order.Count);
        order.Insert(target, id);
        Library.Shared.ReorderBoards(order);
        BuildTabs();
    }

    private void ShowPreview(ClipItem item)
    {
        PreviewCard.Background = new SolidColorBrush(CardFill());
        PreviewText.Foreground = BodyBrush();
        if (item.ContentType == "image")
        {
            var source = ImageFrom(item.Raw);
            PreviewImage.Source = source;
            PreviewImage.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
            PreviewText.Text = "";
        }
        else
        {
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewText.Text = item.Text ?? item.Display;
        }
        PreviewLayer.Visibility = Visibility.Visible;
    }

    private void HidePreview() => PreviewLayer.Visibility = Visibility.Collapsed;

    private void OnPreviewDismiss(object sender, TappedRoutedEventArgs e) => HidePreview();

    private void OnClipSize(object sender, SizeChangedEventArgs e)
    {
        ClipHost.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
        };
    }

    private void OnCardWheel(object sender, PointerRoutedEventArgs e)
    {
        if (CanScrollVertically(e.OriginalSource as DependencyObject)) return;
        var delta = e.GetCurrentPoint(CardScroller).Properties.MouseWheelDelta;
        CardScroller.ChangeView(CardScroller.HorizontalOffset - delta, null, null, true);
        e.Handled = true;
    }

    private async Task CreateBoardAsync()
    {
        var name = await PromptAsync(Strings.Get("new_board"), Strings.Get("name"), "");
        if (string.IsNullOrWhiteSpace(name)) return;
        var board = Library.Shared.AddBoard(name.Trim());
        SelectBoard(board.Id);
    }

    private async Task RenameBoardAsync(Pinboard board)
    {
        var name = await PromptAsync(Strings.Get("rename_board"), Strings.Get("name"), board.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        Library.Shared.RenameBoard(board.Id, name.Trim());
        BuildTabs();
    }

    private async Task DeleteBoardAsync(Pinboard board)
    {
        var dialog = new ContentDialog
        {
            Title = Strings.Get("delete_board"),
            Content = Strings.Get("delete_board_body"),
            PrimaryButtonText = Strings.Get("delete_board"),
            CloseButtonText = Strings.Get("cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot,
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        Library.Shared.DeleteBoard(board.Id);
        if (_board == board.Id) _board = null;
        BuildTabs();
        BuildChrome();
        Render();
    }

    private async Task ClearHistoryAsync()
    {
        if (_clearing) return;
        var count = Library.Shared.CountUnpinned();
        if (count == 0) return;
        _clearing = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = Strings.Get("clear_history"),
                Content = string.Format(Strings.Get("clear_history_body"), count),
                PrimaryButtonText = Strings.Get("clear"),
                CloseButtonText = Strings.Get("cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Root.XamlRoot,
            };
            if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
            Library.Shared.ClearUnpinned();
            _selected.Clear();
            Render();
        }
        finally
        {
            _clearing = false;
        }
    }

    private async Task RenameClipAsync(ClipItem item)
    {
        var name = await PromptAsync(Strings.Get("rename"), Strings.Get("name"), item.Title ?? "");
        if (name is null) return;
        Library.Shared.Rename(item.Id, name.Trim());
        Render();
    }

    private Task EditSelectedAsync()
    {
        var item = _cursor >= 0 && _cursor < _items.Count ? _items[_cursor] : SelectedItems().FirstOrDefault();
        if (item is not null) OpenEditor(item);
        return Task.CompletedTask;
    }

    private async Task EditClipAsync(ClipItem item)
    {
        if (item.Text is null) return;
        var box = new TextBox
        {
            Text = item.Text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 160,
        };
        var dialog = new ContentDialog
        {
            Title = Strings.Get("edit"),
            Content = box,
            PrimaryButtonText = Strings.Get("save"),
            CloseButtonText = Strings.Get("cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        Library.Shared.UpdateText(item.Id, box.Text);
        Render();
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        _dialogs++;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _dialogs--;
        }
    }

    private async Task<string?> PromptAsync(string title, string placeholder, string value)
    {
        var box = new TextBox { Text = value, PlaceholderText = placeholder };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = Strings.Get("save"),
            CloseButtonText = Strings.Get("cancel"),
            DefaultButton = ContentDialogButton.None,
            XamlRoot = Root.XamlRoot,
        };
        var commit = false;
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                commit = true;
                dialog.Hide();
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Escape)
            {
                dialog.Hide();
                e.Handled = true;
            }
        };
        dialog.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        _dialogs++;
        try
        {
            var choice = await dialog.ShowAsync();
            if (choice != ContentDialogResult.Primary && !commit) return null;
            return box.Text;
        }
        finally
        {
            _dialogs--;
        }
    }

    private MenuFlyout TypeFlyout()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Item(Strings.Get("all"), () =>
        {
            _types.Clear();
            BuildChrome();
            Render();
        }));
        foreach (var type in new[] { "plainText", "richText", "html", "image", "url", "fileURL", "color" })
        {
            var look = TypeStyle(type, null);
            var toggle = new ToggleMenuFlyoutItem { Text = look.Label, IsChecked = _types.Contains(type) };
            var captured = type;
            toggle.Click += (_, _) =>
            {
                if (!_types.Add(captured)) _types.Remove(captured);
                BuildChrome();
                Render();
            };
            menu.Items.Add(toggle);
        }
        if (_types.Count > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item(Strings.Get("clear_type"), () =>
            {
                _types.Clear();
                BuildChrome();
                Render();
            }));
        }
        return menu;
    }

    private MenuFlyout DateFlyout()
    {
        var menu = new MenuFlyout();
        foreach (var pair in new[] { ("all", "all"), ("today", "today"), ("week", "week"), ("month", "month") })
        {
            var option = new RadioMenuFlyoutItem
            {
                Text = Strings.Get(pair.Item2),
                GroupName = "date",
                IsChecked = _date == pair.Item1,
            };
            var captured = pair.Item1;
            option.Click += (_, _) =>
            {
                _date = captured;
                BuildChrome();
                Render();
            };
            menu.Items.Add(option);
        }
        return menu;
    }

    private MenuFlyout OptionsFlyout()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Item(Strings.Get("settings"), () => App.Instance.ShowSettings()));
        menu.Items.Add(Item(Strings.Get("quit"), () => App.Instance.Quit()));
        return menu;
    }

    private Button MakeTab(string label, Color dot, bool active, int? number, Action click, Guid? dropBoard = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (_ctrlHint && number is int n)
        {
            row.Children.Add(new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(3.5),
                BorderBrush = ActiveBrush(),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = n.ToString(),
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = ActiveBrush(),
                },
            });
        }
        else
        {
            var paint = dot;
            if (dropBoard is Guid colored)
            {
                var stored = Library.Shared.Boards().FirstOrDefault(board => board.Id == colored)?.Color;
                if (ParseHex(stored) is Color custom) paint = custom;
            }
            row.Children.Add(new Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = new SolidColorBrush(paint),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2.5, 0, 2.5, 0),
            });
        }
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13,
            FontWeight = active ? FontWeights.Medium : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var button = new Button
        {
            Content = row,
            Height = 30,
            Padding = new Thickness(10, 0, 10, 0),
            Background = active ? NavBrush() : new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            Foreground = active ? ActiveBrush() : InactiveBrush(),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
        };
        button.Click += (_, _) => click();
        if (dropBoard is Guid dragging)
        {
            Point? start = null;
            button.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) start = e.GetCurrentPoint(TabBar).Position;
            };
            button.PointerReleased += (_, e) =>
            {
                if (start is null) return;
                var end = e.GetCurrentPoint(TabBar).Position;
                if (Math.Abs(end.X - start.Value.X) > 12) ReorderTab(dragging, end.X);
                start = null;
            };
        }
        if (dropBoard is Guid boardId) _dropTabs.Add((button, boardId));
        return button;
    }

    private void ShowColorPalette(FrameworkElement anchor, Color current, Action<string> apply)
    {
        var picker = new ColorPicker
        {
            Color = current,
            IsAlphaEnabled = false,
            IsAlphaSliderVisible = false,
            IsAlphaTextInputVisible = false,
            IsMoreButtonVisible = true,
            IsColorSliderVisible = true,
            IsColorChannelTextInputVisible = true,
            IsHexInputVisible = true,
            MinWidth = 320,
        };
        picker.ColorChanged += (_, args) =>
        {
            var color = args.NewColor;
            apply($"#{color.R:X2}{color.G:X2}{color.B:X2}");
        };
        var flyout = new Flyout { Content = picker, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
        flyout.ShowAt(anchor);
    }

    private static void PaintTabDot(Button button, Color color)
    {
        if (button.Content is not StackPanel row) return;
        foreach (var child in row.Children)
        {
            if (child is Ellipse ellipse)
            {
                ellipse.Fill = new SolidColorBrush(color);
                return;
            }
        }
    }

    private Guid? DropBoard(Point point)
    {
        foreach (var (button, board) in _dropTabs)
        {
            if (button.ActualWidth <= 0) continue;
            var origin = button.TransformToVisual(Root).TransformPoint(new Point(0, 0));
            var bounds = new Rect(origin.X - 6, origin.Y - 6, button.ActualWidth + 12, button.ActualHeight + 12);
            if (bounds.Contains(point)) return board;
        }
        return null;
    }

    private void HighlightDrop(Point point)
    {
        var hot = DropBoard(point);
        foreach (var (button, board) in _dropTabs)
        {
            var on = hot == board;
            button.BorderBrush = new SolidColorBrush(C(0x3B, 0x71, 0xF3));
            button.BorderThickness = new Thickness(on ? 1.5 : 0);
        }
    }

    private void ClearDropHighlight()
    {
        foreach (var (button, _) in _dropTabs) button.BorderThickness = new Thickness(0);
    }

    private bool CanScrollVertically(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is ScrollViewer viewer
                && !ReferenceEquals(viewer, CardScroller)
                && viewer.VerticalScrollMode != ScrollMode.Disabled
                && viewer.ScrollableHeight > 1)
            {
                return true;
            }
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private static bool InsideButton(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is Button) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void AcceptClipDrop(Guid boardId, string payload)
    {
        if (!payload.StartsWith("sharpclip-clip", StringComparison.Ordinal)) return;
        var added = false;
        foreach (var line in payload.Split('\n').Skip(1))
        {
            if (!Guid.TryParse(line, out var id)) continue;
            Library.Shared.AddToBoard(boardId, id);
            added = true;
        }
        if (added && _board == boardId) Render();
    }

    private Button Pill(string text, bool active) => new()
    {
        Content = text,
        Height = 30,
        Padding = new Thickness(10, 0, 10, 0),
        FontSize = 13,
        Background = SearchBrush(),
        Foreground = active ? ActiveBrush() : InactiveBrush(),
        BorderBrush = LineBrush(),
        BorderThickness = new Thickness(0.75),
        CornerRadius = new CornerRadius(8),
    };

    private Button IconButton(string glyph) => new()
    {
        Content = Glyph(glyph, 13, InactiveBrush()),
        Width = 30,
        Height = 30,
        Padding = new Thickness(0),
        Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
        BorderThickness = new Thickness(0),
        CornerRadius = new CornerRadius(8),
    };

    private static TextBlock Glyph(string glyph, double size, Brush brush) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily("Segoe MDL2 Assets"),
        FontSize = size,
        Foreground = brush,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    private static FrameworkElement AppMark(string? source)
    {
        var name = string.IsNullOrWhiteSpace(source) ? "" : System.IO.Path.GetFileNameWithoutExtension(source);
        var icon = AppIcon(source);
        if (icon is not null)
        {
            var mark = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)),
                BorderThickness = new Thickness(0.5),
                Child = new Image { Source = icon, Stretch = Stretch.UniformToFill },
            };
            if (name.Length > 0) ToolTipService.SetToolTip(mark, name);
            return mark;
        }

        var letter = name.Length == 0 ? "?" : name[..1].ToUpperInvariant();
        var color = TabColors[Math.Abs(name.GetHashCode()) % TabColors.Length];
        return new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(color),
            Child = new TextBlock
            {
                Text = letter,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private static ImageSource? AppIcon(string? source)
    {
        var key = source ?? "";
        if (IconCache.TryGetValue(key, out var cached)) return cached;
        var path = ResolveExecutable(source);
        var icon = path is null ? null : LoadAppIcon(path);
        IconCache[key] = icon;
        return icon;
    }

    private static string? ResolveExecutable(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        var trimmed = source.Trim().Trim('"');
        if (System.IO.File.Exists(trimmed)) return trimmed;
        var name = System.IO.Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(name)) return null;
        foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        {
            using var key = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{name}");
            var value = (key?.GetValue(null) as string)?.Trim().Trim('"');
            if (!string.IsNullOrEmpty(value) && System.IO.File.Exists(value)) return value;
        }
        var system = System.IO.Path.Combine(Environment.SystemDirectory, name);
        if (System.IO.File.Exists(system)) return system;
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                var file = process.MainModule?.FileName;
                if (!string.IsNullOrEmpty(file) && System.IO.Path.GetFileName(file).Equals(name, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
            catch
            {
                // Processes we cannot inspect have no icon path.
            }
        }
        return null;
    }

    private static ImageSource? LoadAppIcon(string path)
    {
        var info = new Win32.SHFILEINFO();
        var ok = Win32.SHGetFileInfo(path, 0, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.SHFILEINFO>(), 0x100);
        if (ok == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            using var bitmap = System.Drawing.Bitmap.FromHicon(info.hIcon);
            var width = bitmap.Width;
            var height = bitmap.Height;
            var bytes = new byte[width * height * 4];
            var bits = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                System.Runtime.InteropServices.Marshal.Copy(bits.Scan0, bytes, 0, bytes.Length);
            }
            finally
            {
                bitmap.UnlockBits(bits);
            }
            var image = new WriteableBitmap(width, height);
            using (var stream = image.PixelBuffer.AsStream())
            {
                stream.Write(bytes, 0, bytes.Length);
            }
            image.Invalidate();
            return image;
        }
        catch
        {
            return null;
        }
        finally
        {
            Win32.DestroyIcon(info.hIcon);
        }
    }

    private static MenuFlyoutItem Item(string text, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => action();
        return item;
    }

    private bool FiltersOn => _types.Count > 0 || _date != "all";

    private string TypeLabel() => _types.Count switch
    {
        0 => Strings.Get("type"),
        1 => TypeStyle(_types.First(), null).Label,
        _ => string.Format(Strings.Get("type_n"), _types.Count),
    };

    private string DateLabel() => _date == "all" ? Strings.Get("date") : Strings.Get(_date);

    private static string Footer(ClipItem item, string fallback)
    {
        switch (item.ContentType)
        {
            case "url":
                return Strings.Get("url_badge");
            case "fileURL":
                return Strings.Get("file_badge");
            case "image":
                return $"{Math.Max(1, item.Raw.Length / 1024)} KB";
            case "color":
                return item.Text ?? fallback;
            default:
                var count = item.Text?.Length ?? 0;
                if (count >= 1000)
                {
                    return string.Format(Strings.Get("chars_k"), (count / 1000.0).ToString("0.0"));
                }
                return string.Format(Strings.Get("chars"), count);
        }
    }

    private static string Age(DateTimeOffset at)
    {
        var span = DateTimeOffset.Now - at;
        var local = at.ToLocalTime();
        var now = DateTimeOffset.Now;
        if (span.TotalMinutes < 1) return Strings.Get("now");
        if (span.TotalHours < 1) return string.Format(Strings.Get("mins"), Math.Max(1, (int)span.TotalMinutes));
        if (local.Date == now.Date) return string.Format(Strings.Get("today_at"), local.ToString("HH:mm"));
        if (local.Date == now.Date.AddDays(-1)) return Strings.Get("yesterday");
        return local.ToString("d");
    }

    private readonly record struct TypeLook(Color Tint, string Glyph, string Label);

    private static TypeLook TypeStyle(string type, string? text) => type switch
    {
        "image" => new(C(0x2B, 0x3B, 0x95), "\uE91B", Strings.Get("image")),
        "url" => new(C(0x00, 0x96, 0x88), "\uE71B", Strings.Get("link")),
        "fileURL" => new(C(0xE5, 0x7E, 0x21), "\uE7C3", Strings.Get("file")),
        "color" => new(ParseHex(text) ?? C(0x80, 0x80, 0x80), "\uE790", Strings.Get("color")),
        "html" => new(C(0x3F, 0x63, 0xE2), "\uE8C1", Strings.Get("html")),
        "plainText" => new(C(0x3F, 0x63, 0xE2), "\uE8B7", Strings.Get("text")),
        "richText" => new(C(0x3F, 0x63, 0xE2), "\uE8A5", Strings.Get("rich")),
        "unknown" => new(C(0x3F, 0x63, 0xE2), "\uE9CE", Strings.Get("other")),
        _ => new(C(0x3F, 0x63, 0xE2), "\uE8B7", Strings.Get("text")),
    };

    private static Color? ParseHex(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var hex = text.Trim().TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var value)) return null;
        return Color.FromArgb(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    private static BitmapImage? ImageFrom(byte[] bytes)
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

    private static VirtualKey PhysicalKey(KeyRoutedEventArgs e) => e.Key == VirtualKey.None ? e.OriginalKey : e.Key;

    private static bool IsQuickLook(VirtualKey key)
    {
        var configured = SettingsStore.Current.QuickLookKey;
        if (configured == 0) configured = 0x20;
        return (int)key == configured && !CtrlDown() && !ShiftDown() && !AltDown() && !WinDown();
    }

    private static bool CtrlDown() => Down(VirtualKey.Control);
    private static bool ShiftDown() => Down(VirtualKey.Shift);
    private static bool AltDown() => Down(VirtualKey.Menu);
    private static bool WinDown() => Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows);

    private static bool Down(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private SolidColorBrush SearchBrush() => new(Dark ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0));
    private SolidColorBrush NavBrush() => new(Dark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(15, 0, 0, 0));
    private SolidColorBrush ActiveBrush() => new(Dark ? C(0xF2, 0xF2, 0xF2) : C(0x29, 0x29, 0x29));
    private SolidColorBrush InactiveBrush() => new(Dark ? C(0xBF, 0xBF, 0xBF) : C(0x61, 0x61, 0x61));
    private SolidColorBrush MutedBrush() => new(Dark ? C(0x9E, 0x9E, 0x9E) : C(0x66, 0x66, 0x66));
    private SolidColorBrush BodyBrush() => new(Dark ? C(0xE0, 0xE0, 0xE0) : C(0x33, 0x33, 0x33));
    private SolidColorBrush BadgeBrush() => new(Dark ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(15, 0, 0, 0));
    private SolidColorBrush LineBrush() => new(Dark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0));
    private Color CardFill() => Dark ? Color.FromArgb(29, 255, 255, 255) : Color.FromArgb(252, 255, 255, 255);
    private Color CardEdge() => Dark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0);
    private Color SelectedFill() => Dark ? C(0x1B, 0x20, 0x2A) : C(0xF6, 0xF9, 0xFF);

    private static Color C(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);

    private static void Lift(UIElement element, bool hover, bool selected)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;
            var width = element is FrameworkElement fe && fe.Width > 0 ? (float)fe.Width : 260f;
            var height = element is FrameworkElement box && box.Height > 0 ? (float)box.Height : 220f;
            visual.CenterPoint = new Vector3(width / 2f, height / 2f, 0);
            const float gap = 22f;
            var capped = Math.Min(1.10f, (width + gap * 2f) / width);
            var scale = compositor.CreateVector3KeyFrameAnimation();
            scale.Duration = TimeSpan.FromMilliseconds(150);
            scale.InsertKeyFrame(1f, hover ? new Vector3(capped, capped, 1f) : Vector3.One);
            visual.StartAnimation("Scale", scale);
            var move = compositor.CreateVector3KeyFrameAnimation();
            move.Duration = TimeSpan.FromMilliseconds(150);
            move.InsertKeyFrame(1f, new Vector3(0, 0, hover ? 32f : selected ? 16f : 0f));
            visual.StartAnimation("Translation", move);
            var shadow = compositor.CreateDropShadow();
            shadow.BlurRadius = hover ? 18f : selected ? 12f : 0f;
            shadow.Opacity = hover ? 0.28f : selected ? 0.24f : 0f;
            shadow.Color = Color.FromArgb(255, 0, 0, 0);
            var sprite = compositor.CreateSpriteVisual();
            sprite.Size = new Vector2(width, height);
            sprite.Shadow = shadow;
            ElementCompositionPreview.SetElementChildVisual(element, sprite);
        }
        catch
        {
            // A missing compositor still leaves the card usable.
        }
    }
}

[System.Runtime.InteropServices.ComImport]
[System.Runtime.InteropServices.Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")]
internal class VirtualDesktopManager
{
}

[System.Runtime.InteropServices.ComImport]
[System.Runtime.InteropServices.Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
[System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVirtualDesktopManager
{
    [System.Runtime.InteropServices.PreserveSig]
    int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrent);
    [System.Runtime.InteropServices.PreserveSig]
    int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
    [System.Runtime.InteropServices.PreserveSig]
    int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
}

/// <summary>
/// Watches the mouse off the UI thread. WinUI stops its window timer once the
/// panel loses activation, so an outside click never reached the old timer.
/// </summary>
internal static class OutsideClickMonitor
{
    private static Thread? _thread;
    private static volatile bool _running;
    private static Action<int, int>? _handler;

    public static void Start(Action<int, int> handler)
    {
        Stop();
        _handler = handler;
        _running = true;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "SharpClipOutsideClick",
        };
        _thread.Start();
    }

    public static void Stop()
    {
        _running = false;
        _handler = null;
        _thread = null;
    }

    private static void Loop()
    {
        var wasDown = (Win32.GetAsyncKeyState(Win32.VK_LBUTTON) & 0x8000) != 0
            || (Win32.GetAsyncKeyState(Win32.VK_RBUTTON) & 0x8000) != 0;
        while (_running)
        {
            var down = (Win32.GetAsyncKeyState(Win32.VK_LBUTTON) & 0x8000) != 0
                || (Win32.GetAsyncKeyState(Win32.VK_RBUTTON) & 0x8000) != 0;
            if (down && !wasDown && Win32.GetCursorPos(out var point))
            {
                _handler?.Invoke(point.X, point.Y);
            }
            wasDown = down;
            Thread.Sleep(10);
        }
    }
}
