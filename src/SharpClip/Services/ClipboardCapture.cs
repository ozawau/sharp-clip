using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SharpClip;

internal sealed record CapturedClip(string ContentType, byte[] Raw, string? Text, string? SourceApp);

internal static class ClipboardCapture
{
    private static int _skip;
    internal static volatile bool SuppressCapture;

    public static void SkipNext() => Interlocked.Increment(ref _skip);

    public static bool ConsumeSkip()
    {
        return Interlocked.Exchange(ref _skip, 0) > 0;
    }

    public static CapturedClip? Read()
    {
        if (IsExcludedForeground()) return null;
        if (!Win32.OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            var source = ForegroundImagePath();
            if (Win32.IsClipboardFormatAvailable(Win32.CF_HDROP))
            {
                var files = ReadFiles();
                if (files.Length > 0)
                {
                    var text = string.Join(Environment.NewLine, files);
                    return new CapturedClip("fileURL", Encoding.UTF8.GetBytes(text), text, source);
                }
            }

            var png = Win32.RegisterClipboardFormat("PNG");
            if (png != 0 && Win32.IsClipboardFormatAvailable(png))
            {
                var bytes = ReadBytes(png);
                if (bytes is { Length: > 0 })
                {
                    return new CapturedClip("image", bytes, null, source);
                }
            }

            var htmlFormat = Win32.RegisterClipboardFormat("HTML Format");
            if (htmlFormat != 0 && Win32.IsClipboardFormatAvailable(htmlFormat))
            {
                var html = ReadText(htmlFormat);
                if (!string.IsNullOrWhiteSpace(html))
                {
                    return new CapturedClip("html", Encoding.UTF8.GetBytes(html), StripHtmlFragment(html), source);
                }
            }

            if (Win32.IsClipboardFormatAvailable(Win32.CF_UNICODETEXT))
            {
                var text = ReadText(Win32.CF_UNICODETEXT);
                if (string.IsNullOrEmpty(text)) return null;
                var type = LooksLikeUrl(text) ? "url" : "plainText";
                return new CapturedClip(type, Encoding.UTF8.GetBytes(text), text, source);
            }
        }
        finally
        {
            Win32.CloseClipboard();
        }
        return null;
    }

    public static void Place(ClipItem item, bool plain)
    {
        if (!plain && item.ContentType == "image" && item.Raw.Length > 0)
        {
            WriteImage(item.Raw);
            return;
        }
        if (!plain && item.ContentType == "fileURL" && !string.IsNullOrWhiteSpace(item.Text))
        {
            var files = item.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (files.Length > 0 && files.All(File.Exists))
            {
                WriteFiles(files);
                return;
            }
        }
        WriteText(item.Text ?? "", plain);
    }

    public static void PasteItemInto(IntPtr previous, ClipItem item, bool plain) =>
        PasteItemInto(previous, IntPtr.Zero, item, plain);

    public static void PasteItemInto(IntPtr previous, IntPtr focus, ClipItem item, bool plain)
    {
        Place(item, plain);
        SendPaste(previous, focus);
    }

    public static void WriteText(string text, bool plain)
    {
        var payload = plain ? text.Trim() : text;
        var bytes = Encoding.Unicode.GetBytes(payload + "\0");
        var mem = Win32.GlobalAlloc(Win32.GMEM_MOVEABLE, (nuint)bytes.Length);
        var locked = Win32.GlobalLock(mem);
        Marshal.Copy(bytes, 0, locked, bytes.Length);
        Win32.GlobalUnlock(mem);
        SkipNext();
        if (!Win32.OpenClipboard(IntPtr.Zero)) return;
        try
        {
            Win32.EmptyClipboard();
            Win32.SetClipboardData(Win32.CF_UNICODETEXT, mem);
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }

    public static void PasteIntoPreviousApp(IntPtr previous, string text, bool plain) =>
        PasteIntoPreviousApp(previous, IntPtr.Zero, text, plain);

    public static void PasteIntoPreviousApp(IntPtr previous, IntPtr focus, string text, bool plain)
    {
        WriteText(text, plain);
        SendPaste(previous, focus);
    }

    private static void SendPaste(IntPtr previous, IntPtr focus)
    {
        SuppressCapture = true;
        try
        {
            var until = Environment.TickCount + 120;
            do
            {
                ForceForeground(previous, focus);
                if (previous == IntPtr.Zero || Win32.GetForegroundWindow() == previous) break;
                Thread.Sleep(15);
            }
            while (Environment.TickCount < until);

            // Shift is still held for Shift+Enter. Lift it so the target sees Ctrl+V,
            // not Ctrl+Shift+V, which is the panel shortcut and a different command.
            var shift = Down(0x10) || Down(0xA0) || Down(0xA1);
            var alt = Down(0x12) || Down(0xA4) || Down(0xA5);
            var win = Down(0x5B) || Down(0x5C);
            if (shift) Tap(0x10, true);
            if (alt) Tap(0x12, true);
            if (win) Tap(0x5B, true);
            Tap(Win32.VK_CONTROL, false);
            Tap(Win32.VK_V, false);
            Tap(Win32.VK_V, true);
            Tap(Win32.VK_CONTROL, true);
            if (win) Tap(0x5B, false);
            if (alt) Tap(0x12, false);
            if (shift) Tap(0x10, false);
        }
        finally
        {
            SuppressCapture = false;
        }
    }

    private static void WriteImage(byte[] bytes)
    {
        System.Drawing.Bitmap? bitmap = null;
        var hBitmap = IntPtr.Zero;
        try
        {
            bitmap = new System.Drawing.Bitmap(new MemoryStream(bytes));
            hBitmap = bitmap.GetHbitmap();
        }
        catch
        {
            bitmap?.Dispose();
            return;
        }

        var png = Win32.RegisterClipboardFormat("PNG");
        var pngMem = CopyToGlobal(bytes);
        SkipNext();
        if (!Win32.OpenClipboard(IntPtr.Zero))
        {
            if (hBitmap != IntPtr.Zero) Win32.DeleteObject(hBitmap);
            bitmap.Dispose();
            return;
        }
        try
        {
            Win32.EmptyClipboard();
            Win32.SetClipboardData(2, hBitmap);
            hBitmap = IntPtr.Zero;
            if (png != 0 && pngMem != IntPtr.Zero) Win32.SetClipboardData(png, pngMem);
        }
        finally
        {
            Win32.CloseClipboard();
            if (hBitmap != IntPtr.Zero) Win32.DeleteObject(hBitmap);
            bitmap.Dispose();
        }
    }

    private static void WriteFiles(string[] files)
    {
        var payload = string.Join('\0', files) + "\0\0";
        var text = Encoding.Unicode.GetBytes(payload);
        var bytes = new byte[20 + text.Length];
        BitConverter.GetBytes(20).CopyTo(bytes, 0);
        BitConverter.GetBytes(1).CopyTo(bytes, 16);
        text.CopyTo(bytes, 20);
        var mem = CopyToGlobal(bytes);
        SkipNext();
        if (!Win32.OpenClipboard(IntPtr.Zero) || mem == IntPtr.Zero) return;
        try
        {
            Win32.EmptyClipboard();
            Win32.SetClipboardData(Win32.CF_HDROP, mem);
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }

    private static IntPtr CopyToGlobal(byte[] bytes)
    {
        var mem = Win32.GlobalAlloc(Win32.GMEM_MOVEABLE, (nuint)bytes.Length);
        if (mem == IntPtr.Zero) return IntPtr.Zero;
        var locked = Win32.GlobalLock(mem);
        Marshal.Copy(bytes, 0, locked, bytes.Length);
        Win32.GlobalUnlock(mem);
        return mem;
    }

    public static string Hash(byte[] data)
    {
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }

    public static string? ForegroundProcessName()
    {
        var path = ForegroundImagePath();
        return string.IsNullOrEmpty(path) ? null : Path.GetFileName(path);
    }

    private static string? ForegroundImagePath()
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        var process = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[1024];
            var size = buffer.Length;
            if (!Win32.QueryFullProcessImageName(process, 0, buffer, ref size)) return null;
            return new string(buffer, 0, size);
        }
        finally
        {
            Win32.CloseHandle(process);
        }
    }

    private static bool IsExcludedForeground()
    {
        var name = ForegroundProcessName();
        return name is not null && Library.Shared.IsExcluded(name);
    }

    private static bool LooksLikeUrl(string text)
    {
        return Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static string? ReadText(uint format)
    {
        var bytes = ReadBytes(format);
        if (bytes is null) return null;
        if (format == Win32.CF_UNICODETEXT)
        {
            return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        }
        return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    }

    private static byte[]? ReadBytes(uint format)
    {
        var handle = Win32.GetClipboardData(format);
        if (handle == IntPtr.Zero) return null;
        var size = (int)Win32.GlobalSize(handle);
        if (size <= 0) return null;
        var locked = Win32.GlobalLock(handle);
        if (locked == IntPtr.Zero) return null;
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(locked, bytes, 0, size);
            return bytes;
        }
        finally
        {
            Win32.GlobalUnlock(handle);
        }
    }

    private static string[] ReadFiles()
    {
        var handle = Win32.GetClipboardData(Win32.CF_HDROP);
        if (handle == IntPtr.Zero) return [];
        var count = Win32.DragQueryFile(handle, 0xFFFFFFFF, null, 0);
        var files = new string[count];
        var buffer = new char[520];
        for (uint i = 0; i < count; i++)
        {
            var n = Win32.DragQueryFile(handle, i, buffer, (uint)buffer.Length);
            files[i] = new string(buffer, 0, (int)n);
        }
        return files;
    }

    private static string StripHtmlFragment(string html)
    {
        const string startMark = "StartFragment:";
        const string endMark = "EndFragment:";
        var start = IndexAfter(html, startMark);
        var end = IndexAfter(html, endMark);
        if (start < 0 || end < 0) return html;
        if (!int.TryParse(html.AsSpan(start, 10), out var startAt)) return html;
        if (!int.TryParse(html.AsSpan(end, 10), out var endAt)) return html;
        if (endAt <= startAt || endAt > html.Length) return html;
        return html[startAt..endAt];
    }

    private static int IndexAfter(string text, string mark)
    {
        var at = text.IndexOf(mark, StringComparison.Ordinal);
        return at < 0 ? -1 : at + mark.Length;
    }

    private static void ForceForeground(IntPtr hwnd, IntPtr focus)
    {
        if (hwnd == IntPtr.Zero) return;
        var foreground = Win32.GetForegroundWindow();
        var foreThread = foreground == IntPtr.Zero ? 0 : Win32.GetWindowThreadProcessId(foreground, out _);
        var targetThread = Win32.GetWindowThreadProcessId(hwnd, out _);
        var thisThread = Win32.GetCurrentThreadId();
        var attachFore = foreThread != 0 && foreThread != thisThread && Win32.AttachThreadInput(thisThread, foreThread, true);
        var attachTarget = targetThread != 0 && targetThread != thisThread && targetThread != foreThread && Win32.AttachThreadInput(thisThread, targetThread, true);
        try
        {
            // A momentary Alt lets this process move the foreground back to the app
            // that was active before the panel opened.
            Win32.keybd_event(0x12, 0, 0, UIntPtr.Zero);
            Win32.keybd_event(0x12, 0, Win32.KEYEVENTF_KEYUP, UIntPtr.Zero);
            Win32.ShowWindow(hwnd, 5);
            Win32.BringWindowToTop(hwnd);
            Win32.SwitchToThisWindow(hwnd, true);
            Win32.SetForegroundWindow(hwnd);
            if (focus != IntPtr.Zero) Win32.SetFocus(focus);
        }
        finally
        {
            if (attachTarget) Win32.AttachThreadInput(thisThread, targetThread, false);
            if (attachFore) Win32.AttachThreadInput(thisThread, foreThread, false);
        }
    }

    private static bool Down(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;

    private static void Tap(uint vk, bool up) => Win32.keybd_event((byte)vk, 0, up ? Win32.KEYEVENTF_KEYUP : 0, UIntPtr.Zero);
}
