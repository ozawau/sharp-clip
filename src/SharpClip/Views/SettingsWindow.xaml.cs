using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace SharpClip;

public sealed partial class SettingsWindow : Window
{
    private bool _loading = true;
    private string? _recording;

    public SettingsWindow()
    {
        InitializeComponent();
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            if (_recording is not null) StopRecording();
            AppWindow.Hide();
        };
        Nav.SelectedItem = NavGeneral;
        ShowPage("general");
        ApplyLabels();
        LoadValues();
        ApplyAppearance();
    }

    public void HideOffscreen() => AppWindow.Hide();

    public void Present()
    {
        ApplyLabels();
        LoadValues();
        ApplyAppearance();
        Place();
        AppWindow.Show();
        Activate();
    }

    public void ApplyAppearance() => Root.RequestedTheme = SettingsStore.RequestedTheme();

    public void RefreshHotkeyStatus()
    {
        if (_recording is not null) return;
        ShortcutStatus.Text = HotkeyStatus.AnyFailed ? Strings.Get("shortcut_failed") : "";
    }

    private void Place()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var dpi = Win32.GetDpiForWindow(hwnd);
        var scale = (dpi == 0 ? 96 : dpi) / 96.0;
        var width = (int)Math.Round(540 * scale);
        var height = (int)Math.Round(560 * scale);
        Win32.GetCursorPos(out var point);
        var monitor = Win32.MonitorFromPoint(point, Win32.MONITOR_DEFAULTTONEAREST);
        var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfo(monitor, ref info))
        {
            AppWindow.Resize(new SizeInt32(width, height));
            return;
        }
        var work = info.rcWork;
        var x = work.Left + Math.Max(0, (work.Right - work.Left - width) / 2);
        var y = work.Top + Math.Max(0, (work.Bottom - work.Top - height) / 2);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void ApplyLabels()
    {
        _loading = true;
        Title = Strings.Get("settings");
        NavGeneral.Content = Strings.Get("general");
        NavAppearance.Content = Strings.Get("appearance");
        NavShortcuts.Content = Strings.Get("shortcuts");
        NavExclusions.Content = Strings.Get("exclusions");
        NavAbout.Content = Strings.Get("about");

        PastingHeader.Text = Strings.Get("pasting");
        PasteTitle.Text = Strings.Get("paste");
        PasteInfo.Text = Strings.Get("paste_info");
        PasteHelp.Text = Strings.Get("paste_help");
        PlainTitle.Text = Strings.Get("plain");
        PlainInfo.Text = Strings.Get("plain_info");
        PlainHelp.Text = Strings.Get("plain_help");
        LanguageLabel.Text = Strings.Get("language");
        LimitLabel.Text = Strings.Get("limit");
        LoginTitle.Text = Strings.Get("login");
        TrayTitle.Text = Strings.Get("tray");
        TrayHelp.Text = Strings.Get("tray_hidden");
        QuitButton.Content = Strings.Get("quit");
        BackupHeader.Text = Strings.Get("backup");
        ExportHelp.Text = Strings.Get("export_help");
        ImportHelp.Text = Strings.Get("import_help");
        ExportButton.Content = Strings.Get("export") + "…";
        ImportButton.Content = Strings.Get("import") + "…";

        ThemeLabel.Text = Strings.Get("theme");
        AnimateTitle.Text = Strings.Get("animate");
        AnimateHelp.Text = SettingsStore.SystemMotionEnabled()
            ? Strings.Get("animate_help")
            : Strings.Get("animate_reduced");
        AnimateSwitch.IsEnabled = SettingsStore.SystemMotionEnabled();

        PanelTitle.Text = Strings.Get("shortcut_panel");
        PanelHelp.Text = Strings.Get("shortcut_panel_info");
        QueueTitle.Text = Strings.Get("shortcut_queue");
        QueueInfo.Text = Strings.Get("shortcut_queue_info");
        QueueHelp.Text = Strings.Get("shortcut_queue_help");
        ClearTitle.Text = Strings.Get("shortcut_clear");
        QuickTitle.Text = Strings.Get("shortcut_quick");
        QuickInfo.Text = Strings.Get("shortcut_quick_info");
        QuickHelp.Text = Strings.Get("shortcut_quick_help");
        TabNavHeader.Text = Strings.Get("tab_nav");
        TabHistoryLabel.Text = Strings.Get("tab_history");
        TabBoardsLabel.Text = Strings.Get("tab_boards");
        TabNavHelp.Text = Strings.Get("tab_nav_help");

        ExcludeHelp.Text = Strings.Get("exclude_help");
        AboutVersion.Text = string.Format(Strings.Get("about_version"), VersionText());
        AboutBody.Text = Strings.Get("about_body");

        FillCombo(LanguageBox, Strings.Languages.Select(item => (item.Name, (object)item.Code)));
        FillCombo(LimitBox,
        [
            ("100", 100),
            ("500", 500),
            ("1000", 1000),
            ("5000", 5000),
            (Strings.Get("unlimited"), 0),
        ]);
        FillCombo(ThemeBox,
        [
            (Strings.Get("theme_system"), "system"),
            (Strings.Get("theme_light"), "light"),
            (Strings.Get("theme_dark"), "dark"),
        ]);
        RefreshShortcutLabels();
    }

    private void LoadValues()
    {
        _loading = true;
        var settings = SettingsStore.Current;
        SelectTag(LanguageBox, settings.Language);
        SelectTag(LimitBox, settings.HistoryLimit);
        SelectTag(ThemeBox, settings.Theme is "light" or "dark" ? settings.Theme : "system");
        PasteSwitch.IsOn = settings.PasteIntoActiveApp;
        PlainSwitch.IsOn = settings.AlwaysPlainText;
        LoginSwitch.IsOn = settings.LaunchAtLogin;
        TraySwitch.IsOn = settings.ShowTrayIcon;
        AnimateSwitch.IsOn = settings.AnimatePanel;
        UpdateTrayNote();
        ReloadExclusions();
        RefreshShortcutLabels();
        RefreshHotkeyStatus();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _loading = false);
    }

    private void OnNavChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag) ShowPage(tag);
    }

    private void ShowPage(string tag)
    {
        PageGeneral.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        PageAppearance.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        PageShortcuts.Visibility = tag == "shortcuts" ? Visibility.Visible : Visibility.Collapsed;
        PageExclusions.Visibility = tag == "exclusions" ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageBox.SelectedItem is not ComboBoxItem item) return;
        SettingsStore.Current.Language = item.Tag as string ?? "";
        SettingsStore.Save();
        ApplyLabels();
        LoadValues();
        App.Instance.ApplyPreferences(refreshHistory: true);
    }

    private void OnLimitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LimitBox.SelectedItem is not ComboBoxItem item || item.Tag is not int limit) return;
        SettingsStore.Current.HistoryLimit = limit;
        SettingsStore.Save();
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeBox.SelectedItem is not ComboBoxItem item || item.Tag is not string theme) return;
        SettingsStore.Current.Theme = theme;
        SettingsStore.Save();
        App.Instance.ApplyPreferences(refreshHistory: true);
    }

    private void OnPasteToggled(object sender, RoutedEventArgs e) => SaveToggle(() => SettingsStore.Current.PasteIntoActiveApp = PasteSwitch.IsOn);
    private void OnPlainToggled(object sender, RoutedEventArgs e) => SaveToggle(() => SettingsStore.Current.AlwaysPlainText = PlainSwitch.IsOn);
    private void OnLoginToggled(object sender, RoutedEventArgs e) => SaveToggle(() => SettingsStore.Current.LaunchAtLogin = LoginSwitch.IsOn);

    private void OnAnimateToggled(object sender, RoutedEventArgs e) => SaveToggle(() => SettingsStore.Current.AnimatePanel = AnimateSwitch.IsOn);

    private async void OnTrayToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsStore.Current.ShowTrayIcon = TraySwitch.IsOn;
        SettingsStore.Save();
        UpdateTrayNote();
        App.Instance.ApplyPreferences(refreshHistory: false);
        if (!TraySwitch.IsOn)
        {
            await AlertAsync(Strings.Get("tray_hidden_title"), Strings.Get("tray_hidden_body"));
        }
    }

    private void SaveToggle(Action apply)
    {
        if (_loading) return;
        apply();
        SettingsStore.Save();
    }

    private void UpdateTrayNote()
    {
        var hidden = !TraySwitch.IsOn;
        TrayHelp.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
        QuitButton.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnQuit(object sender, RoutedEventArgs e) => App.Instance.Quit();

    private void OnRecord(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id) return;
        if (_recording == id)
        {
            StopRecording();
            return;
        }
        if (_recording is not null) StopRecording();
        _recording = id;
        ShortcutStatus.Text = id == "quick" ? Strings.Get("press_key") : Strings.Get("press_shortcut");
        App.Instance.SuspendHotkeys();
        RefreshShortcutLabels();
        Root.Focus(FocusState.Programmatic);
    }

    private void OnUnset(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not Button button || button.Tag is not string id) return;
        if (_recording is not null) StopRecording();
        Assign(id, false, false, false, false, 0);
        SettingsStore.Save();
        App.Instance.ApplyPreferences(refreshHistory: false);
        RefreshShortcutLabels();
    }

    private void OnPreviewKey(object sender, KeyRoutedEventArgs e)
    {
        if (_recording is null) return;
        e.Handled = true;
        if (e.Key == VirtualKey.Escape)
        {
            StopRecording();
            return;
        }
        var key = e.Key == VirtualKey.None ? e.OriginalKey : e.Key;
        if (IsModifier(key) || key == VirtualKey.None) return;

        var vk = (int)key;
        if (_recording == "quick")
        {
            if (ModifiersDown() || !HotkeyText.IsQuickLookKey(vk))
            {
                ShortcutStatus.Text = Strings.Get("shortcut_reserved");
                return;
            }
            SettingsStore.Current.QuickLookKey = vk;
        }
        else
        {
            var (ctrl, shift, alt, win) = ReadModifiers();
            var function = vk is >= 0x70 and <= 0x87;
            if (!function && !ctrl && !alt && !win)
            {
                ShortcutStatus.Text = Strings.Get("shortcut_invalid");
                return;
            }
            if (Conflicts(_recording, ctrl, shift, alt, win, vk))
            {
                ShortcutStatus.Text = Strings.Get("shortcut_taken");
                return;
            }
            Assign(_recording, ctrl, shift, alt, win, vk);
        }

        SettingsStore.Save();
        _recording = null;
        ShortcutStatus.Text = "";
        App.Instance.ApplyPreferences(refreshHistory: false);
        RefreshShortcutLabels();
    }

    private void StopRecording()
    {
        if (_recording is null) return;
        _recording = null;
        ShortcutStatus.Text = "";
        App.Instance.ApplyPreferences(refreshHistory: false);
        RefreshShortcutLabels();
    }

    private void RefreshShortcutLabels()
    {
        var settings = SettingsStore.Current;
        PanelRecord.Content = _recording == "panel"
            ? Strings.Get("press_shortcut")
            : HotkeyText.Format(settings.PanelControl, settings.PanelShift, settings.PanelAlt, settings.PanelWin, settings.PanelKey);
        QueueRecord.Content = _recording == "queue"
            ? Strings.Get("press_shortcut")
            : HotkeyText.Format(settings.QueueControl, settings.QueueShift, settings.QueueAlt, settings.QueueWin, settings.QueueKey);
        ClearRecord.Content = _recording == "clear"
            ? Strings.Get("press_shortcut")
            : HotkeyText.Format(settings.ClearControl, settings.ClearShift, settings.ClearAlt, settings.ClearWin, settings.ClearKey);
        QuickRecord.Content = _recording == "quick"
            ? Strings.Get("press_key")
            : HotkeyText.Name(settings.QuickLookKey == 0 ? 0x20 : settings.QuickLookKey);
        PanelUnset.IsEnabled = settings.PanelKey != 0;
        QueueUnset.IsEnabled = settings.QueueKey != 0;
        ClearUnset.IsEnabled = settings.ClearKey != 0;
    }

    private static void Assign(string id, bool ctrl, bool shift, bool alt, bool win, int vk)
    {
        var settings = SettingsStore.Current;
        switch (id)
        {
            case "panel":
                settings.PanelControl = ctrl;
                settings.PanelShift = shift;
                settings.PanelAlt = alt;
                settings.PanelWin = win;
                settings.PanelKey = vk;
                break;
            case "queue":
                settings.QueueControl = ctrl;
                settings.QueueShift = shift;
                settings.QueueAlt = alt;
                settings.QueueWin = win;
                settings.QueueKey = vk;
                break;
            case "clear":
                settings.ClearControl = ctrl;
                settings.ClearShift = shift;
                settings.ClearAlt = alt;
                settings.ClearWin = win;
                settings.ClearKey = vk;
                break;
        }
    }

    private static bool Conflicts(string id, bool ctrl, bool shift, bool alt, bool win, int vk)
    {
        var settings = SettingsStore.Current;
        return Matches(id, "panel", settings.PanelControl, settings.PanelShift, settings.PanelAlt, settings.PanelWin, settings.PanelKey, ctrl, shift, alt, win, vk)
            || Matches(id, "queue", settings.QueueControl, settings.QueueShift, settings.QueueAlt, settings.QueueWin, settings.QueueKey, ctrl, shift, alt, win, vk)
            || Matches(id, "clear", settings.ClearControl, settings.ClearShift, settings.ClearAlt, settings.ClearWin, settings.ClearKey, ctrl, shift, alt, win, vk);
    }

    private static bool Matches(
        string current, string other,
        bool boundCtrl, bool boundShift, bool boundAlt, bool boundWin, int boundKey,
        bool ctrl, bool shift, bool alt, bool win, int vk) =>
        current != other
        && boundKey != 0
        && boundKey == vk
        && boundCtrl == ctrl
        && boundShift == shift
        && boundAlt == alt
        && boundWin == win;

    private void ReloadExclusions()
    {
        ExcludeList.Items.Clear();
        foreach (var (process, name) in Library.Shared.Exclusions())
        {
            ExcludeList.Items.Add(new ListViewItem { Content = $"{name}    {process}", Tag = process });
        }
        ExcludeRemove.IsEnabled = false;
    }

    private void OnExcludeSelection(object sender, SelectionChangedEventArgs e) =>
        ExcludeRemove.IsEnabled = ExcludeList.SelectedItem is ListViewItem;

    private async void OnExcludeAdd(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializePicker(picker);
        picker.FileTypeFilter.Add(".exe");
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        var file = await picker.PickSingleFileAsync();
        if (file is null || string.IsNullOrWhiteSpace(file.Name)) return;
        var description = "";
        if (!string.IsNullOrWhiteSpace(file.Path))
        {
            try
            {
                description = FileVersionInfo.GetVersionInfo(file.Path).FileDescription ?? "";
            }
            catch
            {
                description = "";
            }
        }
        var appName = string.IsNullOrWhiteSpace(description)
            ? Path.GetFileNameWithoutExtension(file.Name)
            : description.Trim();
        Library.Shared.Exclude(file.Name, appName);
        ReloadExclusions();
    }

    private void OnExcludeRemove(object sender, RoutedEventArgs e)
    {
        if (ExcludeList.SelectedItem is not ListViewItem item || item.Tag is not string process) return;
        Library.Shared.Unexclude(process);
        ReloadExclusions();
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker();
            InitializePicker(picker);
            picker.SuggestedFileName = "sharpclip-backup";
            picker.FileTypeChoices.Add("JSON", [".json"]);
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await FileIO.WriteBytesAsync(file, Library.Shared.ExportJson());
            await AlertAsync(Strings.Get("backup"), Strings.Get("export_ok"));
        }
        catch (Exception ex)
        {
            await AlertAsync(Strings.Get("backup"), string.Format(Strings.Get("export_fail"), ex.Message));
        }
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            InitializePicker(picker);
            picker.FileTypeFilter.Add(".json");
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            var bytes = await FileIO.ReadBufferAsync(file);
            var data = new byte[bytes.Length];
            using var reader = Windows.Storage.Streams.DataReader.FromBuffer(bytes);
            reader.ReadBytes(data);
            var (imported, skipped) = Library.Shared.ImportJson(data);
            App.Instance.ApplyPreferences(refreshHistory: true);
            await AlertAsync(Strings.Get("backup"), string.Format(Strings.Get("import_ok"), imported, skipped));
        }
        catch (Exception ex)
        {
            await AlertAsync(Strings.Get("backup"), string.Format(Strings.Get("import_fail"), ex.Message));
        }
    }

    private async Task AlertAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = Strings.Get("ok"),
            XamlRoot = Root.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private void InitializePicker(object picker)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        if (picker is FileSavePicker save) InitializeWithWindow.Initialize(save, hwnd);
        if (picker is FileOpenPicker open) InitializeWithWindow.Initialize(open, hwnd);
    }

    private static void FillCombo<T>(ComboBox box, IEnumerable<(string Label, T Tag)> items)
    {
        box.Items.Clear();
        foreach (var (label, tag) in items)
        {
            box.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        }
    }

    private static void SelectTag(ComboBox box, object tag)
    {
        for (var i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem item && Equals(item.Tag, tag))
            {
                box.SelectedIndex = i;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static bool IsModifier(VirtualKey key) => key is
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu or
        VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static bool ModifiersDown()
    {
        var (ctrl, shift, alt, win) = ReadModifiers();
        return ctrl || shift || alt || win;
    }

    private static (bool Ctrl, bool Shift, bool Alt, bool Win) ReadModifiers() =>
        (Down(0x11), Down(0x10), Down(0x12), Down(0x5B) || Down(0x5C));

    private static bool Down(int key) => (Win32.GetAsyncKeyState(key) & 0x8000) != 0;

    private static string VersionText()
    {
        var version = typeof(App).Assembly.GetName().Version;
        return version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }
}
