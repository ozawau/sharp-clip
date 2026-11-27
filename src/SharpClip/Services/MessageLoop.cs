using System.Runtime.InteropServices;

namespace SharpClip;

/// <summary>
/// Hidden message window. Clipboard changes and the global hotkeys arrive here,
/// off the UI thread, and are forwarded to <see cref="App"/>.
/// </summary>
internal sealed class WindowMessageLoop
{
    private const string ClassName = "SharpClipMessageWindow";
    private Thread? _thread;
    private IntPtr _hwnd;
    private IntPtr _keyboardHook;
    private int _swallowed;
    private bool _captureTyping;
    private volatile bool _hotkeysOn = true;
    private readonly WndProc _proc;
    private readonly Win32.HookProc _keyboardProc;

    public event Action<int>? HotkeyPressed;
    public event Action? ClipboardChanged;
    public event Action? HotkeysApplied;

    public WindowMessageLoop()
    {
        _proc = Proc;
        _keyboardProc = OnKeyboard;
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "SharpClip.Messages" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Stop()
    {
        if (_hwnd != IntPtr.Zero)
        {
            Win32.PostQuitMessage(0);
        }
    }

    public void ApplyHotkeys(bool enabled)
    {
        _hotkeysOn = enabled;
        if (_hwnd == IntPtr.Zero) return;
        Win32.PostMessage(_hwnd, Win32.WM_APPLY_HOTKEYS, enabled ? new IntPtr(1) : IntPtr.Zero, IntPtr.Zero);
    }

    private void Run()
    {
        var wndClass = new Win32.WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            lpszClassName = ClassName,
            hInstance = Win32.GetModuleHandle(null),
        };
        Win32.RegisterClass(ref wndClass);
        // A normal hidden window, not HWND_MESSAGE. Message-only windows accept
        // RegisterHotKey but never receive WM_HOTKEY, so the shortcut looks dead.
        _hwnd = Win32.CreateWindowEx(
            Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE,
            ClassName, "SharpClip", 0,
            0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);
        Win32.AddClipboardFormatListener(_hwnd);
        _keyboardHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _keyboardProc, Win32.GetModuleHandle(null), 0);
        if (_keyboardHook == IntPtr.Zero) BindHotkeys();
        TrayIcon.Bind(_hwnd);
        TrayIcon.SetVisible(SettingsStore.Current.ShowTrayIcon);

        while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }

        if (_keyboardHook != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
        UnregisterAll();
        Win32.RemoveClipboardFormatListener(_hwnd);
        TrayIcon.Remove();
        Win32.DestroyWindow(_hwnd);
    }

    private IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32.WM_CLIPBOARDUPDATE:
                if (ClipboardCapture.ConsumeSkip()) return IntPtr.Zero;
                ClipboardChanged?.Invoke();
                return IntPtr.Zero;
            case Win32.WM_HOTKEY:
                HotkeyPressed?.Invoke(wParam.ToInt32());
                return IntPtr.Zero;
            case TrayIcon.CallbackMessage:
                TrayIcon.Handle(lParam.ToInt32(), hwnd);
                return IntPtr.Zero;
            case Win32.WM_COMMAND:
                TrayIcon.Command(wParam.ToInt32());
                return IntPtr.Zero;
            case Win32.WM_APPLY_HOTKEYS:
                if (_keyboardHook == IntPtr.Zero)
                {
                    if (wParam == IntPtr.Zero) UnregisterAll();
                    else BindHotkeys();
                }
                else
                {
                    HotkeyStatus.PanelFailed = false;
                    HotkeyStatus.QueueFailed = false;
                    HotkeyStatus.ClearFailed = false;
                }
                if (wParam != IntPtr.Zero)
                {
                    TrayIcon.SetVisible(SettingsStore.Current.ShowTrayIcon);
                    HotkeysApplied?.Invoke();
                }
                return IntPtr.Zero;
            case Win32.WM_DESTROY:
                Win32.PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void BindHotkeys()
    {
        UnregisterAll();
        var settings = SettingsStore.Current;
        HotkeyStatus.PanelFailed = !TryRegister(HotkeyIds.Panel, settings.PanelControl, settings.PanelShift, settings.PanelAlt, settings.PanelWin, settings.PanelKey);
        HotkeyStatus.QueueFailed = !TryRegister(HotkeyIds.Queue, settings.QueueControl, settings.QueueShift, settings.QueueAlt, settings.QueueWin, settings.QueueKey);
        HotkeyStatus.ClearFailed = !TryRegister(HotkeyIds.Clear, settings.ClearControl, settings.ClearShift, settings.ClearAlt, settings.ClearWin, settings.ClearKey);
    }

    private bool TryRegister(int id, bool ctrl, bool shift, bool alt, bool win, int vk)
    {
        if (vk == 0) return true;
        uint modifiers = Win32.MOD_NOREPEAT;
        if (ctrl) modifiers |= Win32.MOD_CONTROL;
        if (shift) modifiers |= Win32.MOD_SHIFT;
        if (alt) modifiers |= Win32.MOD_ALT;
        if (win) modifiers |= Win32.MOD_WIN;
        return Win32.RegisterHotKey(_hwnd, id, modifiers, (uint)vk);
    }

    private void UnregisterAll()
    {
        Win32.UnregisterHotKey(_hwnd, HotkeyIds.Panel);
        Win32.UnregisterHotKey(_hwnd, HotkeyIds.Queue);
        Win32.UnregisterHotKey(_hwnd, HotkeyIds.Clear);
    }

    /// <summary>
    /// Global shortcuts are watched here. RegisterHotKey reports success on a
    /// message-only window and then never delivers the key.
    /// </summary>
    private IntPtr OnKeyboard(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try
            {
                var info = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
                var vk = (int)info.vkCode;
                var message = wParam.ToInt32();
                var up = message is Win32.WM_KEYUP or Win32.WM_SYSKEYUP || (info.flags & 0x80) != 0;
                if (ClipboardCapture.SuppressCapture)
                    return Win32.CallNextHookEx(_keyboardHook, code, wParam, lParam);
                if (up)
                {
                    if (vk == _swallowed)
                    {
                        _swallowed = 0;
                        _captureTyping = false;
                        return (IntPtr)1;
                    }
                }
                else if (!IsModifier(vk))
                {
                    if (vk == _swallowed)
                    {
                        if (_captureTyping) App.Instance.RoutePanelKey(vk);
                        return (IntPtr)1;
                    }
                    if (_hotkeysOn)
                    {
                        var id = Match(vk, CtrlDown(), ShiftDown(), AltDown(info.flags), WinDown());
                        if (id != 0)
                        {
                            _swallowed = vk;
                            _captureTyping = false;
                            HotkeyPressed?.Invoke(id);
                            return (IntPtr)1;
                        }
                    }
                    // Enter never reaches the search box reliably, so paste and the other
                    // panel commands are taken here whenever the panel is open.
                    if (App.Instance.PanelWantsKeys && !App.Instance.DialogOpen && IsPanelCommand(vk))
                    {
                        var repeat = vk == _swallowed;
                        _swallowed = vk;
                        _captureTyping = vk is 0x25 or 0x27;
                        if (!repeat || _captureTyping) App.Instance.RoutePanelKey(vk);
                        return (IntPtr)1;
                    }
                    // The panel is a tool window opened from a global shortcut, so Windows
                    // often leaves the keyboard in the previous app. Swallow those keys and
                    // type them into the search box instead. Injected keys are our own
                    // Ctrl+V paste and must reach the target app.
                    var injected = (info.flags & 0x10) != 0;
                    if (!injected && !ClipboardCapture.SuppressCapture && App.Instance.PanelWantsKeys && !ForegroundIsUs())
                    {
                        _swallowed = vk;
                        _captureTyping = true;
                        App.Instance.RoutePanelKey(vk);
                        return (IntPtr)1;
                    }
                }
            }
            catch
            {
                // Keep the keyboard alive if a shortcut handler fails.
            }
        }
        return Win32.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private static bool ForegroundIsUs()
    {
        var foreground = Win32.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        Win32.GetWindowThreadProcessId(foreground, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    private static int Match(int vk, bool ctrl, bool shift, bool alt, bool win)
    {
        var settings = SettingsStore.Current;
        if (Same(settings.PanelKey, settings.PanelControl, settings.PanelShift, settings.PanelAlt, settings.PanelWin, vk, ctrl, shift, alt, win)) return HotkeyIds.Panel;
        if (Same(settings.QueueKey, settings.QueueControl, settings.QueueShift, settings.QueueAlt, settings.QueueWin, vk, ctrl, shift, alt, win)) return HotkeyIds.Queue;
        if (Same(settings.ClearKey, settings.ClearControl, settings.ClearShift, settings.ClearAlt, settings.ClearWin, vk, ctrl, shift, alt, win)) return HotkeyIds.Clear;
        return 0;
    }

    private static bool Same(int boundKey, bool boundCtrl, bool boundShift, bool boundAlt, bool boundWin, int vk, bool ctrl, bool shift, bool alt, bool win) =>
        boundKey != 0 && boundKey == vk && boundCtrl == ctrl && boundShift == shift && boundAlt == alt && boundWin == win;

    private static bool IsPanelCommand(int vk) => vk is 0x0D or 0x1B;

    private static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    private static bool CtrlDown() => Down(0x11) || Down(0xA2) || Down(0xA3);
    private static bool ShiftDown() => Down(0x10) || Down(0xA0) || Down(0xA1);
    private static bool AltDown(uint flags) => Down(0x12) || Down(0xA4) || Down(0xA5) || (flags & 0x20) != 0;
    private static bool WinDown() => Down(0x5B) || Down(0x5C);
    private static bool Down(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}

internal static class HotkeyStatus
{
    public static bool PanelFailed { get; set; }
    public static bool QueueFailed { get; set; }
    public static bool ClearFailed { get; set; }

    public static bool AnyFailed => PanelFailed || QueueFailed || ClearFailed;
}
