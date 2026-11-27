using System.Runtime.InteropServices;

namespace SharpClip;

internal static class TrayIcon
{
    public const int CallbackMessage = Win32.WM_APP + 1;
    private const uint IconId = 1;
    private static IntPtr _hwnd;
    private static bool _added;

    public static void Bind(IntPtr hwnd) => _hwnd = hwnd;

    public static void SetVisible(bool visible)
    {
        if (_hwnd == IntPtr.Zero || visible == _added) return;
        var data = Data();
        if (visible)
        {
            if (!Win32.Shell_NotifyIcon(Win32.NIM_ADD, ref data))
            {
                Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref data);
                _added = Win32.Shell_NotifyIcon(Win32.NIM_ADD, ref data);
            }
            else
            {
                _added = true;
            }
        }
        else
        {
            Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref data);
            _added = false;
        }
    }

    public static void Refresh()
    {
        if (!_added || _hwnd == IntPtr.Zero) return;
        var data = Data();
        Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref data);
    }

    public static void Remove()
    {
        if (_added && _hwnd != IntPtr.Zero)
        {
            var data = Data();
            Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref data);
            _added = false;
        }
        _hwnd = IntPtr.Zero;
    }

    public static void Handle(int mouseMessage, IntPtr hwnd)
    {
        if (mouseMessage == Win32.WM_LBUTTONUP)
        {
            App.Instance.ShowHistory();
            return;
        }
        if (mouseMessage != Win32.WM_RBUTTONUP) return;

        var menu = Win32.CreatePopupMenu();
        var recent = Library.Shared.Latest(5);
        for (var i = 0; i < recent.Count; i++)
        {
            var label = recent[i].Display.Replace("&", "&&");
            if (label.Length > 60) label = label[..60];
            Win32.AppendMenu(menu, Win32.MF_STRING, (nuint)(100 + i), label);
        }
        if (recent.Count > 0) Win32.AppendMenu(menu, Win32.MF_SEPARATOR, 0, null);
        Win32.AppendMenu(menu, Win32.MF_STRING, 1, Strings.Get("history"));
        Win32.AppendMenu(menu, Win32.MF_STRING, 2, Strings.Get("queue"));
        Win32.AppendMenu(menu, Win32.MF_STRING, 3, Strings.Get("settings"));
        Win32.AppendMenu(menu, Win32.MF_SEPARATOR, 0, null);
        Win32.AppendMenu(menu, Win32.MF_STRING, 4, Strings.Get("quit"));
        Win32.GetCursorPos(out var point);
        Win32.SetForegroundWindow(hwnd);
        var command = Win32.TrackPopupMenu(menu, Win32.TPM_RIGHTBUTTON | Win32.TPM_RETURNCMD, point.X, point.Y, 0, hwnd, IntPtr.Zero);
        Win32.DestroyMenu(menu);
        if (command >= 100)
        {
            var item = recent[command - 100];
            var previous = Win32.GetForegroundWindow();
            Paste(item, previous, SettingsStore.Current.AlwaysPlainText);
        }
        else
        {
            Command(command);
        }
    }

    public static void Command(int id)
    {
        switch (id)
        {
            case 1: App.Instance.ShowHistory(); break;
            case 2: ClipQueue.Toggle(); break;
            case 3: App.Instance.ShowSettings(); break;
            case 4: App.Instance.Quit(); break;
        }
    }

    public static void Paste(ClipItem item, IntPtr previous, bool plain)
    {
        if (SettingsStore.Current.PasteIntoActiveApp && previous != IntPtr.Zero)
        {
            ClipboardCapture.PasteItemInto(previous, item, plain);
        }
        else
        {
            ClipboardCapture.Place(item, plain);
        }
    }

    private static Win32.NOTIFYICONDATA Data() => new()
    {
        cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = Win32.NIF_MESSAGE | Win32.NIF_TIP,
        uCallbackMessage = CallbackMessage,
        szTip = "SharpClip",
    };
}
