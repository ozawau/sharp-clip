using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace SharpClip;

public partial class App : Application
{
    private static Mutex? _instance;
    private static EventWaitHandle? _activate;
    private DispatcherQueue? _ui;
    private WindowMessageLoop? _loop;
    private HistoryWindow? _history;
    private SettingsWindow? _settings;

    public static App Instance { get; private set; } = null!;

    public App()
    {
        Instance = this;
        InitializeComponent();
        UnhandledException += (_, e) => e.Handled = true;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _instance = new Mutex(false, @"Local\SharpClip.SingleInstance");
        try
        {
            if (!_instance.WaitOne(0))
            {
                SignalRunningInstance();
                Exit();
                return;
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous process exited without releasing the mutex. We own it now.
        }

        _ui = DispatcherQueue.GetForCurrentThread();
        Library.Shared.Open();
        SettingsStore.Load();
        Strings.Apply(SettingsStore.Current.Language);

        _history = new HistoryWindow();
        _settings = new SettingsWindow();
        _history.HideOffscreen();
        _settings.HideOffscreen();
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SharpClip.Activate");
        ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => Instance.ActivatedFromAnotherLaunch(), null, Timeout.Infinite, false);

        _loop = new WindowMessageLoop();
        _loop.HotkeyPressed += OnHotkey;
        _loop.ClipboardChanged += OnClipboard;
        _loop.HotkeysApplied += () => RunOnUi(() => _settings?.RefreshHotkeyStatus());
        _loop.Start();
        ProtocolRegistration.Register();
        if (SettingsStore.Current.LaunchAtLogin)
        {
            StartupRegistration.SetEnabled(true);
        }

        var command = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(command))
        {
            HandleUrl(command);
        }
    }

    public void ShowHistory() => RunOnUi(() => _history?.Toggle());

    public bool PanelWantsKeys => _history?.IsShown == true;

    public bool DialogOpen => _history?.IsDialogOpen == true;

    public void RoutePanelKey(int vk) => RunOnUi(() => _history?.InjectKey(vk));

    private void ActivatedFromAnotherLaunch() => RunOnUi(() =>
    {
        if (!SettingsStore.Current.ShowTrayIcon) ShowSettings();
        else _history?.Present();
    });

    private static void SignalRunningInstance()
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(@"Local\SharpClip.Activate");
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
    }

    public void ShowSettings() => RunOnUi(() =>
    {
        _history?.DismissForSettings();
        _settings?.Present();
    });

    public void ApplyPreferences(bool refreshHistory)
    {
        RunOnUi(() =>
        {
            _settings?.ApplyAppearance();
            if (refreshHistory) _history?.RefreshChrome();
            else _history?.ApplyAppearance();
            _loop?.ApplyHotkeys(true);
        });
    }

    public void SuspendHotkeys() => _loop?.ApplyHotkeys(false);

    public void Quit() => RunOnUi(() =>
    {
        _loop?.Stop();
        TrayIcon.Remove();
        Exit();
    });

    private void OnHotkey(int id)
    {
        if (id == HotkeyIds.Panel) ShowHistory();
        else if (id == HotkeyIds.Queue) RunOnUi(() => ClipQueue.Toggle());
        else if (id == HotkeyIds.Clear) RunOnUi(() => _history?.RequestClear());
    }

    private void OnClipboard()
    {
        if (!SettingsStore.Current.Monitoring) return;
        RunOnUi(() =>
        {
            var captured = ClipboardCapture.Read();
            if (captured is null) return;
            var item = Library.Shared.Record(captured);
            if (item is not null && ClipQueue.IsActive)
            {
                ClipQueue.Push(item);
            }
            _history?.Reload();
            TrayIcon.Refresh();
        });
    }

    public void HandleUrl(string url)
    {
        if (!url.StartsWith("clipbara://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("sharpclip://", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var command = url.Split("://", 2)[1].Trim('/').ToLowerInvariant();
        switch (command)
        {
            case "open":
                RunOnUi(() => _history?.Present());
                break;
            case "toggle":
                ShowHistory();
                break;
            case "queue":
                RunOnUi(() => ClipQueue.Toggle());
                break;
        }
    }

    private void RunOnUi(Action action)
    {
        if (_ui is null || _ui.HasThreadAccess) action();
        else _ui.TryEnqueue(() => action());
    }
}
