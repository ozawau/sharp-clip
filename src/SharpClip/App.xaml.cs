using System.Diagnostics;
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
    private WelcomeWindow? _welcome;

    public static App Instance { get; private set; } = null!;

    public App()
    {
        Instance = this;
        InitializeComponent();
        UnhandledException += (_, e) => e.Handled = true;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var foreground = Win32.GetForegroundWindow();
        _instance = new Mutex(false, @"Local\SharpClip.SingleInstance");
        try
        {
            if (!_instance.WaitOne(0))
            {
                SignalRunningInstance(Environment.GetCommandLineArgs().Skip(1).FirstOrDefault());
                Exit();
                return;
            }
        }
        catch (AbandonedMutexException)
        {
        }

        _ui = DispatcherQueue.GetForCurrentThread();
        Library.Shared.Open();
        SettingsStore.Load();
        Strings.Apply(SettingsStore.Current.Language);
        ProtocolRegistration.Register();
        if (SettingsStore.Current.LaunchAtLogin) StartupRegistration.SetEnabled(true);

        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SharpClip.Activate");
        ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => Instance.ActivatedFromAnotherLaunch(), null, Timeout.Infinite, false);

        _loop = new WindowMessageLoop();
        _loop.HotkeyPressed += OnHotkey;
        _loop.ClipboardChanged += OnClipboard;
        _loop.HotkeysApplied += () => RunOnUi(() => _settings?.RefreshHotkeyStatus());
        _loop.Start();

        var queue = _ui;
        queue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var timer = queue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(300);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                var before = Win32.GetForegroundWindow();
                EnsureHistory().Preheat();
                if (before != IntPtr.Zero) Win32.SetForegroundWindow(before);
                if (!SettingsStore.Current.WelcomeCompleted) ShowWelcome();
            };
            timer.Start();
        });

        if (foreground != IntPtr.Zero) Win32.SetForegroundWindow(foreground);
        var command = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(command)) HandleUrl(command);
    }

    public void ShowHistory() => RunOnUi(() => EnsureHistory().Toggle());

    public bool PanelWantsKeys => _history?.IsShown == true;

    public bool DialogOpen => _history?.IsDialogOpen == true;

    public void RoutePanelKey(int vk) => RunOnUi(() => _history?.InjectKey(vk));

    public void QueuePasted() => RunOnUi(() => ClipQueue.ConsumePasted());

    public void NotifyPaste(bool landed) => RunOnUi(() =>
    {
        if (landed) PasteToast.Show();
        else PasteToast.ShowFailed();
    });

    public void OnDisplayChanged() => RunOnUi(() => _history?.OnDisplayChanged());

    public void Restart()
    {
        RunOnUi(() =>
        {
            var path = Environment.ProcessPath;
            try
            {
                _instance?.ReleaseMutex();
            }
            catch
            {
            }
            if (!string.IsNullOrWhiteSpace(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            Quit();
        });
    }

    private void ActivatedFromAnotherLaunch() => RunOnUi(() =>
    {
        var command = TakePendingCommand();
        if (!string.IsNullOrWhiteSpace(command) && command != "toggle")
        {
            HandleUrl(command.Contains("://", StringComparison.Ordinal) ? command : "sharpclip://" + command);
            return;
        }
        EnsureHistory().Toggle();
    });

    private static void SignalRunningInstance(string? command)
    {
        try
        {
            var path = PendingPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.IsNullOrWhiteSpace(command) ? "toggle" : command);
            using var signal = EventWaitHandle.OpenExisting(@"Local\SharpClip.Activate");
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static string TakePendingCommand()
    {
        try
        {
            var path = PendingPath();
            if (!File.Exists(path)) return "";
            var text = File.ReadAllText(path).Trim();
            File.Delete(path);
            return text;
        }
        catch
        {
            return "";
        }
    }

    private static string PendingPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SharpClip",
        "pending-command.txt");

    public void ShowSettings() => RunOnUi(() =>
    {
        _history?.DismissForSettings();
        EnsureSettings().Present();
    });

    public void ShowWelcome() => RunOnUi(() =>
    {
        _welcome ??= new WelcomeWindow();
        _welcome.Present();
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
        ClipQueue.Stop();
        _loop?.Stop();
        TrayIcon.Remove();
        Exit();
    });

    private void OnHotkey(int id)
    {
        if (id == HotkeyIds.Panel) ShowHistory();
        else if (id == HotkeyIds.Queue) RunOnUi(() => ClipQueue.Toggle());
        else if (id == HotkeyIds.Clear) RunOnUi(() => EnsureHistory().RequestClear());
    }

    private void OnClipboard()
    {
        if (!SettingsStore.Current.Monitoring) return;
        RunOnUi(() =>
        {
            var captured = ClipboardCapture.Read();
            if (captured is null) return;
            var item = Library.Shared.Record(captured);
            if (item is null) return;
            if (item.ContentType == "image")
            {
                var id = item.Id;
                var raw = item.Raw;
                Task.Run(() =>
                {
                    var thumb = Images.ThumbnailPng(raw);
                    if (thumb is null) return;
                    Library.Shared.StoreThumbnail(id, thumb);
                    RunOnUi(() => _history?.Reload());
                });
            }
            if (ClipQueue.IsActive) ClipQueue.Push(item);
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
        var command = url.Split("://", 2)[1].Trim('/').Split('?', '#')[0].ToLowerInvariant();
        switch (command)
        {
            case "open":
                RunOnUi(() => EnsureHistory().Present());
                break;
            case "toggle":
                ShowHistory();
                break;
            case "queue":
                RunOnUi(() => ClipQueue.Toggle());
                break;
        }
    }

    public HistoryWindow RecreateHistory()
    {
        var old = _history;
        _history = new HistoryWindow();
        old?.ForceClose();
        return _history;
    }

    private HistoryWindow EnsureHistory()
    {
        _history ??= new HistoryWindow();
        return _history;
    }

    private SettingsWindow EnsureSettings()
    {
        _settings ??= new SettingsWindow();
        return _settings;
    }

    private void RunOnUi(Action action)
    {
        if (_ui is null || _ui.HasThreadAccess) action();
        else _ui.TryEnqueue(() => action());
    }
}
