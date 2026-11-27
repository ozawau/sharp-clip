using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace SharpClip;

public sealed partial class SettingsWindow : Window
{
    private bool _loading = true;

    public SettingsWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 640));
        ApplyLabels();
        LoadValues();
        _loading = false;
    }

    public void HideOffscreen() => AppWindow.Hide();

    public void Present()
    {
        ApplyLabels();
        LoadValues();
        AppWindow.Show();
        Activate();
    }

    private void ApplyLabels()
    {
        Title = Strings.Get("settings");
        LanguageLabel.Text = Strings.Get("language");
        LimitLabel.Text = Strings.Get("limit");
        PasteSwitch.Header = Strings.Get("paste");
        PlainSwitch.Header = Strings.Get("plain");
        LoginSwitch.Header = Strings.Get("login");
        MonitorSwitch.Header = Strings.Get("monitor");
        ExcludeLabel.Text = Strings.Get("exclude");
        ExcludeButton.Content = Strings.Get("exclude");
        UnexcludeButton.Content = "−";
        ExportButton.Content = Strings.Get("export");
        ImportButton.Content = Strings.Get("import");
    }

    private void LoadValues()
    {
        _loading = true;
        var settings = SettingsStore.Current;
        LanguageBox.Items.Clear();
        foreach (var (code, name) in Strings.Languages)
        {
            LanguageBox.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        }
        LanguageBox.SelectedIndex = Math.Max(0, Strings.Languages.ToList().FindIndex(item => item.Code == settings.Language));

        LimitBox.Items.Clear();
        foreach (var limit in new[] { 100, 500, 1000, 5000, 0 })
        {
            LimitBox.Items.Add(new ComboBoxItem
            {
                Content = limit == 0 ? Strings.Get("unlimited") : limit.ToString(),
                Tag = limit,
            });
        }
        LimitBox.SelectedIndex = settings.HistoryLimit switch
        {
            100 => 0,
            1000 => 2,
            5000 => 3,
            0 => 4,
            _ => 1,
        };
        PasteSwitch.IsOn = settings.PasteIntoActiveApp;
        PlainSwitch.IsOn = settings.AlwaysPlainText;
        LoginSwitch.IsOn = settings.LaunchAtLogin;
        MonitorSwitch.IsOn = settings.Monitoring;
        ExcludeList.Items.Clear();
        foreach (var (process, name) in Library.Shared.Exclusions())
        {
            ExcludeList.Items.Add(new ListViewItem { Content = $"{name} ({process})", Tag = process });
        }
        _loading = false;
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageBox.SelectedItem is not ComboBoxItem item) return;
        SettingsStore.Current.Language = item.Tag as string ?? "";
        SettingsStore.Save();
        ApplyLabels();
    }

    private void OnLimitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LimitBox.SelectedItem is not ComboBoxItem item || item.Tag is not int limit) return;
        SettingsStore.Current.HistoryLimit = limit;
        SettingsStore.Save();
    }

    private void OnPasteToggled(object sender, RoutedEventArgs e) => SaveToggle(() => SettingsStore.Current.PasteIntoActiveApp = PasteSwitch.IsOn);
    private void OnPlainToggled(object sender, RoutedEventArgs e) => SaveToggle(() => SettingsStore.Current.AlwaysPlainText = PlainSwitch.IsOn);
    private void OnLoginToggled(object sender, RoutedEventArgs e) => SaveToggle(() => SettingsStore.Current.LaunchAtLogin = LoginSwitch.IsOn);
    private void OnMonitorToggled(object sender, RoutedEventArgs e) => SaveToggle(() => SettingsStore.Current.Monitoring = MonitorSwitch.IsOn);

    private void SaveToggle(Action apply)
    {
        if (_loading) return;
        apply();
        SettingsStore.Save();
    }

    private void OnExcludeForeground(object sender, RoutedEventArgs e)
    {
        var name = ClipboardCapture.ForegroundProcessName();
        if (string.IsNullOrWhiteSpace(name) || name.Equals("SharpClip.exe", StringComparison.OrdinalIgnoreCase)) return;
        Library.Shared.Exclude(name, Path.GetFileNameWithoutExtension(name));
        LoadValues();
    }

    private void OnUnexclude(object sender, RoutedEventArgs e)
    {
        if (ExcludeList.SelectedItem is not ListViewItem item || item.Tag is not string process) return;
        Library.Shared.Unexclude(process);
        LoadValues();
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();
        InitializePicker(picker);
        picker.SuggestedFileName = "SharpClip";
        picker.FileTypeChoices.Add("JSON", [".json"]);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        await FileIO.WriteBytesAsync(file, Library.Shared.ExportJson());
    }

    private async void OnImport(object sender, RoutedEventArgs e)
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
        Library.Shared.ImportJson(data);
    }

    private void InitializePicker(object picker)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        if (picker is FileSavePicker save) InitializeWithWindow.Initialize(save, hwnd);
        if (picker is FileOpenPicker open) InitializeWithWindow.Initialize(open, hwnd);
    }
}
