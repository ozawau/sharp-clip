using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SharpClip;

internal static class TrayIcon
{
    public const int CallbackMessage = Win32.WM_APP + 1;
    private const uint IconId = 1;
    private static IntPtr _hwnd;
    private static IntPtr _icon;
    private static bool _added;
    private static MenuRow[] _rows = [];

    public static bool MenuOpen { get; private set; }

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
        if (_icon != IntPtr.Zero)
        {
            Win32.DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
        _hwnd = IntPtr.Zero;
    }

    public static bool HitsIcon(int x, int y)
    {
        if (!_added || _hwnd == IntPtr.Zero) return false;
        var id = new Win32.NOTIFYICONIDENTIFIER
        {
            cbSize = Marshal.SizeOf<Win32.NOTIFYICONIDENTIFIER>(),
            hWnd = _hwnd,
            uID = IconId,
        };
        if (Win32.Shell_NotifyIconGetRect(ref id, out var rect) != 0) return false;
        return x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
    }

    public static void Handle(int mouseMessage, IntPtr hwnd)
    {
        if (mouseMessage == Win32.WM_LBUTTONUP)
        {
            App.Instance.ShowHistory();
            return;
        }
        if (mouseMessage != Win32.WM_RBUTTONUP) return;
        var recent = Library.Shared.Latest(5);
        var rows = new List<MenuRow>();
        if (recent.Count == 0)
        {
            rows.Add(new MenuRow(8, Strings.Get("tray_empty"), null, true, false));
        }
        else
        {
            for (var i = 0; i < recent.Count; i++)
            {
                var item = recent[i];
                var body = item.Display.Replace("\r", " ").Replace("\n", " ");
                if (body.Length > 42) body = body[..42] + "…";
                var sub = string.Join("  ", new[] { item.SourceApp, Age(item.CopiedAt) }.Where(part => !string.IsNullOrWhiteSpace(part)));
                rows.Add(new MenuRow(200 + i, body, sub, false, false));
            }
        }
        rows.Add(new MenuRow(1, Strings.Get("monitor"), null, false, SettingsStore.Current.Monitoring));
        rows.Add(new MenuRow(2, Strings.Get("history") + "    " + PanelHotkey(), null, false, false));
        var queueLabel = ClipQueue.IsActive ? Strings.Get("queue_end") : Strings.Get("queue_start");
        var queueKey = HotkeyText.Format(SettingsStore.Current.QueueControl, SettingsStore.Current.QueueShift, SettingsStore.Current.QueueAlt, SettingsStore.Current.QueueWin, SettingsStore.Current.QueueKey);
        if (SettingsStore.Current.QueueKey != 0) queueLabel += "    " + queueKey;
        rows.Add(new MenuRow(3, queueLabel, null, false, false));
        rows.Add(new MenuRow(4, Strings.Get("check_updates"), null, false, false));
        rows.Add(new MenuRow(5, Strings.Get("feedback"), null, false, false));
        rows.Add(new MenuRow(6, Strings.Get("settings"), null, false, false));
        rows.Add(new MenuRow(7, Strings.Get("quit"), null, false, false));
        _rows = rows.ToArray();

        var menu = Win32.CreatePopupMenu();
        var firstCommand = recent.Count == 0;
        foreach (var row in rows)
        {
            if (row.Id == 1 && !firstCommand)
            {
                Win32.AppendMenu(menu, Win32.MF_SEPARATOR, 0, null);
                firstCommand = true;
            }
            if (row.Id == 4) Win32.AppendMenu(menu, Win32.MF_SEPARATOR, 0, null);
            if (row.Id == 7) Win32.AppendMenu(menu, Win32.MF_SEPARATOR, 0, null);
            var flags = Win32.MF_OWNERDRAW | Win32.MF_STRING;
            if (row.Disabled) flags |= Win32.MF_GRAYED;
            if (row.Checked) flags |= Win32.MF_CHECKED;
            Win32.AppendMenu(menu, flags, (nuint)row.Id, row.Title);
        }
        Win32.GetCursorPos(out var point);
        Win32.SetForegroundWindow(hwnd);
        MenuOpen = true;
        try
        {
            var command = Win32.TrackPopupMenu(menu, Win32.TPM_RIGHTBUTTON | Win32.TPM_RETURNCMD, point.X, point.Y, 0, hwnd, IntPtr.Zero);
            Win32.PostMessage(hwnd, 0, IntPtr.Zero, IntPtr.Zero);
            if (command >= 200 && command - 200 < recent.Count)
            {
                ClipQueue.Stop();
                Paste(recent[command - 200], Win32.GetForegroundWindow(), SettingsStore.Current.AlwaysPlainText);
            }
            else
            {
                Command(command);
            }
        }
        finally
        {
            MenuOpen = false;
            Win32.DestroyMenu(menu);
        }
    }

    public static bool Measure(IntPtr lParam)
    {
        var measure = Marshal.PtrToStructure<Win32.MEASUREITEMSTRUCT>(lParam);
        var row = _rows.FirstOrDefault(item => item.Id == measure.itemID);
        if (row is null) return false;
        measure.itemWidth = 280;
        measure.itemHeight = (uint)(row.Sub is null ? 28 : 44);
        Marshal.StructureToPtr(measure, lParam, false);
        return true;
    }

    public static bool Draw(IntPtr lParam)
    {
        var draw = Marshal.PtrToStructure<Win32.DRAWITEMSTRUCT>(lParam);
        var row = _rows.FirstOrDefault(item => item.Id == draw.itemID);
        if (row is null) return false;
        using var g = Graphics.FromHdc(draw.hDC);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = Rectangle.FromLTRB(draw.rcItem.Left, draw.rcItem.Top, draw.rcItem.Right, draw.rcItem.Bottom);
        var selected = (draw.itemState & Win32.ODS_SELECTED) != 0 && !row.Disabled;
        using var back = new SolidBrush(selected ? Color.FromArgb(0x3B, 0x71, 0xF3) : Color.White);
        g.FillRectangle(back, rect);
        using var title = new SolidBrush(row.Disabled ? Color.Gray : selected ? Color.White : Color.Black);
        using var font = new Font("Segoe UI", 9f);
        var text = row.Checked ? "\u2713  " + row.Title : row.Title;
        g.DrawString(text, font, title, new RectangleF(rect.Left + 12, rect.Top + (row.Sub is null ? 6 : 4), rect.Width - 20, 20));
        if (row.Sub is not null)
        {
            using var subFont = new Font("Segoe UI", 8f);
            using var sub = new SolidBrush(selected ? Color.FromArgb(230, 230, 230) : Color.DimGray);
            g.DrawString(row.Sub, subFont, sub, new RectangleF(rect.Left + 12, rect.Top + 22, rect.Width - 20, 18));
        }
        return true;
    }

    public static void Command(int id)
    {
        switch (id)
        {
            case 1:
                SettingsStore.Current.Monitoring = !SettingsStore.Current.Monitoring;
                SettingsStore.Save();
                break;
            case 2:
                App.Instance.ShowHistory();
                break;
            case 3:
                ClipQueue.Toggle();
                break;
            case 4:
                var version = typeof(App).Assembly.GetName().Version;
                var text = version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
                Win32.MessageBox(IntPtr.Zero, string.Format(Strings.Get("up_to_date"), text), "SharpClip", 0);
                break;
            case 5:
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mailto:?subject=SharpClip%20feedback") { UseShellExecute = true });
                break;
            case 6:
                App.Instance.ShowSettings();
                break;
            case 7:
                App.Instance.Quit();
                break;
        }
    }

    public static void Paste(ClipItem item, IntPtr previous, bool plain)
    {
        var pasted = SettingsStore.Current.PasteIntoActiveApp && previous != IntPtr.Zero
            ? ClipboardCapture.PasteItemInto(previous, item, plain)
            : CopyOnly(item, plain);
        App.Instance.NotifyPaste(pasted);
        Library.Shared.Touch([item.Id]);
    }

    private static bool CopyOnly(ClipItem item, bool plain)
    {
        ClipboardCapture.Place(item, plain);
        return !SettingsStore.Current.PasteIntoActiveApp;
    }

    private static string PanelHotkey() => HotkeyText.Format(
        SettingsStore.Current.PanelControl,
        SettingsStore.Current.PanelShift,
        SettingsStore.Current.PanelAlt,
        SettingsStore.Current.PanelWin,
        SettingsStore.Current.PanelKey);

    private static string Age(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        var now = DateTimeOffset.Now;
        var span = now - at;
        if (span.TotalMinutes < 1) return Strings.Get("now");
        if (span.TotalHours < 1) return string.Format(Strings.Get("mins"), Math.Max(1, (int)span.TotalMinutes));
        if (local.Date == now.Date) return string.Format(Strings.Get("today_at"), local.ToString("HH:mm"));
        if (local.Date == now.Date.AddDays(-1)) return Strings.Get("yesterday");
        return local.ToString("d");
    }

    private static Win32.NOTIFYICONDATA Data()
    {
        if (_icon == IntPtr.Zero) _icon = MakeIcon();
        return new Win32.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = Win32.NIF_MESSAGE | Win32.NIF_TIP | Win32.NIF_ICON,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = "SharpClip",
        };
    }

    private static IntPtr MakeIcon()
    {
        using var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.FromArgb(0x3B, 0x71, 0xF3));
            g.FillRoundedRectangle(brush, 4, 3, 24, 26, 4);
            using var clip = new SolidBrush(Color.White);
            g.FillRectangle(clip, 10, 2, 12, 6);
            g.FillRectangle(clip, 8, 12, 16, 2);
            g.FillRectangle(clip, 8, 17, 12, 2);
        }
        return bitmap.GetHicon();
    }

    private static void FillRoundedRectangle(this Graphics graphics, Brush brush, float x, float y, float width, float height, float radius)
    {
        using var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + width - d, y, d, d, 270, 90);
        path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
        path.AddArc(x, y + height - d, d, d, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }

    private sealed record MenuRow(int Id, string Title, string? Sub, bool Disabled, bool Checked);
}
