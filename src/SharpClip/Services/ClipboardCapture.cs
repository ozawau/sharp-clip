using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SharpClip;

internal sealed record CapturedClip(string ContentType, byte[] Raw, string? Text, string? SourceApp, string? SourceBundle);

internal readonly record struct ClipPayload(uint Format, byte[] Data);

internal static class ClipboardCapture
{
    private static int _skip;
    private static uint _ownSequence;
    private static string? _lastExternalFile;
    private static string? _lastExternalName;
    internal static volatile bool SuppressCapture;

    public static void SkipNext() => Interlocked.Increment(ref _skip);

    public static void NoteOwnWrite() => _ownSequence = Win32.GetClipboardSequenceNumber();

    public static bool IsOwnUpdate() => _ownSequence != 0 && Win32.GetClipboardSequenceNumber() == _ownSequence;

    public static bool ConsumeSkip()
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref _skip);
            if (seen <= 0) return false;
        }
        while (Interlocked.CompareExchange(ref _skip, seen - 1, seen) != seen);
        return true;
    }

    public static CapturedClip? Read()
    {
        if (IsExcludedForeground()) return null;
        if (QueueMarkersPresent()) return null;
        if (!Win32.OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            var (app, bundle) = ForegroundIdentity();
            var color = ReadColor();
            if (color is not null)
            {
                return new CapturedClip("color", Encoding.UTF8.GetBytes(color), color, app, bundle);
            }

            if (Win32.IsClipboardFormatAvailable(Win32.CF_HDROP))
            {
                var files = ReadFiles();
                if (files.Length > 0)
                {
                    var text = string.Join(Environment.NewLine, files);
                    return new CapturedClip("fileURL", Encoding.UTF8.GetBytes(text), text, app, bundle);
                }
            }

            var png = Format("PNG");
            if (png != 0 && Win32.IsClipboardFormatAvailable(png))
            {
                var bytes = ReadBytes(png);
                if (bytes is { Length: > 8 }) return new CapturedClip("image", bytes, null, app, bundle);
            }

            var htmlFormat = Format("HTML Format");
            if (htmlFormat != 0 && Win32.IsClipboardFormatAvailable(htmlFormat))
            {
                var html = ReadText(htmlFormat);
                if (!string.IsNullOrWhiteSpace(html))
                {
                    var plain = Win32.IsClipboardFormatAvailable(Win32.CF_UNICODETEXT) ? ReadText(Win32.CF_UNICODETEXT) : null;
                    return new CapturedClip("html", Encoding.UTF8.GetBytes(html), string.IsNullOrWhiteSpace(plain) ? StripHtmlFragment(html) : plain, app, bundle);
                }
            }

            var rtf = Format("Rich Text Format");
            if (rtf != 0 && Win32.IsClipboardFormatAvailable(rtf))
            {
                var bytes = ReadBytes(rtf);
                if (bytes is { Length: > 0 })
                {
                    var plain = Win32.IsClipboardFormatAvailable(Win32.CF_UNICODETEXT) ? ReadText(Win32.CF_UNICODETEXT) : null;
                    return new CapturedClip("richText", bytes, plain, app, bundle);
                }
            }

            if (Win32.IsClipboardFormatAvailable(Win32.CF_UNICODETEXT))
            {
                var text = ReadText(Win32.CF_UNICODETEXT);
                if (string.IsNullOrEmpty(text)) return null;
                var type = LooksLikeUrl(text) ? "url" : "plainText";
                return new CapturedClip(type, Encoding.UTF8.GetBytes(text), text, app, bundle);
            }

            var dib = ReadImageFallback();
            if (dib is { Length: > 0 }) return new CapturedClip("image", dib, null, app, bundle);
        }
        finally
        {
            Win32.CloseClipboard();
        }
        return null;
    }

    public static IReadOnlyList<ClipPayload> Payloads(ClipItem item, bool plain)
    {
        var list = new List<ClipPayload>();
        var text = item.Text ?? "";
        if (plain || item.ContentType is "plainText" or "url" or "color" or "unknown")
        {
            list.Add(new ClipPayload(Win32.CF_UNICODETEXT, Unicode(plain ? text.Trim() : text)));
            return list;
        }
        if (item.ContentType == "image" && item.Raw.Length > 0)
        {
            var png = Images.ToPng(item.Raw);
            var dib = Images.ToDib(item.Raw);
            var pngFormat = Format("PNG");
            if (pngFormat != 0 && png is { Length: > 0 }) list.Add(new ClipPayload(pngFormat, png));
            if (dib is { Length: > 0 }) list.Add(new ClipPayload(Win32.CF_DIB, dib));
            if (list.Count > 0) return list;
        }
        if (item.ContentType == "fileURL" && !string.IsNullOrWhiteSpace(item.Text))
        {
            var files = item.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (files.Length > 0 && files.All(File.Exists))
            {
                list.Add(new ClipPayload(Win32.CF_HDROP, DropFiles(files)));
                return list;
            }
        }
        if (item.ContentType == "richText" && item.Raw.Length > 0)
        {
            var format = Format("Rich Text Format");
            if (format != 0) list.Add(new ClipPayload(format, item.Raw));
            list.Add(new ClipPayload(Win32.CF_UNICODETEXT, Unicode(text)));
            return list;
        }
        if (item.ContentType == "html" && item.Raw.Length > 0)
        {
            var format = Format("HTML Format");
            var html = Encoding.UTF8.GetString(item.Raw).TrimEnd('\0');
            if (format != 0) list.Add(new ClipPayload(format, Encoding.UTF8.GetBytes(EnsureHtmlClipboard(html))));
            list.Add(new ClipPayload(Win32.CF_UNICODETEXT, Unicode(text)));
            return list;
        }
        list.Add(new ClipPayload(Win32.CF_UNICODETEXT, Unicode(plain ? text.Trim() : text)));
        return list;
    }

    public static void Place(ClipItem item, bool plain)
    {
        WritePayloads(Payloads(item, plain));
    }

    public static bool PasteItemInto(IntPtr previous, ClipItem item, bool plain) =>
        PasteItemInto(previous, IntPtr.Zero, item, plain);

    public static bool PasteItemInto(IntPtr previous, IntPtr focus, ClipItem item, bool plain)
    {
        Place(item, plain);
        if (!SettingsStore.Current.PasteIntoActiveApp) return true;
        return SendPaste(previous, focus);
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
            NoteOwnWrite();
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }

    public static bool PasteIntoPreviousApp(IntPtr previous, string text, bool plain) =>
        PasteIntoPreviousApp(previous, IntPtr.Zero, text, plain);

    public static bool PasteIntoPreviousApp(IntPtr previous, IntPtr focus, string text, bool plain)
    {
        WriteText(text, plain);
        if (!SettingsStore.Current.PasteIntoActiveApp) return true;
        return SendPaste(previous, focus);
    }

    private static bool SendPaste(IntPtr previous, IntPtr focus)
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
            if (previous == IntPtr.Zero || Win32.GetForegroundWindow() != previous) return false;

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
            return true;
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
            var dib = Images.ToDib(bytes);
            if (dib is { Length: > 0 })
            {
                var dibMem = CopyToGlobal(dib);
                if (dibMem != IntPtr.Zero) Win32.SetClipboardData(Win32.CF_DIB, dibMem);
            }
            NoteOwnWrite();
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
            NoteOwnWrite();
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

    public static void NoteExternalWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return;
        var process = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return;
        try
        {
            var buffer = new char[1024];
            var size = buffer.Length;
            if (!Win32.QueryFullProcessImageName(process, 0, buffer, ref size)) return;
            var path = new string(buffer, 0, size);
            _lastExternalFile = Path.GetFileName(path);
            _lastExternalName = _lastExternalFile;
            try
            {
                var description = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) _lastExternalName = description.Trim();
            }
            catch
            {
                _lastExternalName = Path.GetFileNameWithoutExtension(path);
            }
        }
        finally
        {
            Win32.CloseHandle(process);
        }
    }

    public static (string? File, string? Name) LastExternalApp() => (_lastExternalFile, _lastExternalName);

    public static (string? App, string? Bundle) ForegroundIdentity()
    {
        var path = ForegroundImagePath();
        if (string.IsNullOrEmpty(path)) return (null, null);
        var file = Path.GetFileName(path);
        var friendly = file;
        try
        {
            var description = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) friendly = description.Trim();
        }
        catch
        {
            friendly = Path.GetFileNameWithoutExtension(file);
        }
        return (friendly, path);
    }

    public static bool QueueMarkersPresent()
    {
        foreach (var name in new[] { "org.nspasteboard.TransientType", "org.nspasteboard.ConcealedType", "org.nspasteboard.AutoGeneratedType" })
        {
            var format = Format(name);
            if (format == 0 || !Win32.IsClipboardFormatAvailable(format)) return false;
        }
        return true;
    }

    private static uint Format(string name) => Win32.RegisterClipboardFormat(name);

    private static byte[]? ReadImageFallback()
    {
        if (Win32.IsClipboardFormatAvailable(Win32.CF_DIBV5))
        {
            var bytes = ReadBytes(Win32.CF_DIBV5);
            var png = bytes is null ? null : Images.ToPng(bytes);
            if (png is { Length: > 0 }) return png;
        }
        if (Win32.IsClipboardFormatAvailable(Win32.CF_DIB))
        {
            var bytes = ReadBytes(Win32.CF_DIB);
            var png = bytes is null ? null : Images.ToPng(bytes);
            if (png is { Length: > 0 }) return png;
        }
        return null;
    }

    private static string? ReadColor()
    {
        foreach (var name in new[] { "application/x-color", "Color" })
        {
            var format = Format(name);
            if (format == 0 || !Win32.IsClipboardFormatAvailable(format)) continue;
            var bytes = ReadBytes(format);
            var hex = ColorHex(bytes);
            if (hex is not null) return hex;
        }
        return null;
    }

    private static string? ColorHex(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 3) return null;
        var text = Encoding.UTF8.GetString(bytes).Trim('\0', ' ', '\r', '\n');
        if (text.Length >= 7 && text[0] == '#'
            && int.TryParse(text.AsSpan(1, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            return text[..7].ToUpperInvariant();
        }
        if (bytes.Length is 4 or 8) return $"#{bytes[0]:X2}{bytes[1]:X2}{bytes[2]:X2}";
        return null;
    }

    private static void WritePayloads(IReadOnlyList<ClipPayload> payloads)
    {
        if (payloads.Count == 0) return;
        SkipNext();
        if (!Win32.OpenClipboard(IntPtr.Zero)) return;
        try
        {
            Win32.EmptyClipboard();
            foreach (var payload in payloads)
            {
                var mem = CopyToGlobal(payload.Data);
                if (mem != IntPtr.Zero) Win32.SetClipboardData(payload.Format, mem);
            }
            NoteOwnWrite();
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }

    private static byte[] Unicode(string text) => Encoding.Unicode.GetBytes(text + "\0");

    private static byte[] DropFiles(string[] files)
    {
        var payload = string.Join('\0', files) + "\0\0";
        var text = Encoding.Unicode.GetBytes(payload);
        var bytes = new byte[20 + text.Length];
        BitConverter.GetBytes(20).CopyTo(bytes, 0);
        BitConverter.GetBytes(1).CopyTo(bytes, 16);
        text.CopyTo(bytes, 20);
        return bytes;
    }

    private static string EnsureHtmlClipboard(string html)
    {
        if (html.Contains("StartHTML:", StringComparison.Ordinal)) return html;
        var fragment = html;
        const string header = "Version:0.9\r\nStartHTML:00000000\r\nEndHTML:00000000\r\nStartFragment:00000000\r\nEndFragment:00000000\r\n";
        var document = $"<html><body><!--StartFragment-->{fragment}<!--EndFragment--></body></html>";
        var startHtml = header.Length;
        var startFragment = startHtml + document.IndexOf("<!--StartFragment-->", StringComparison.Ordinal) + "<!--StartFragment-->".Length;
        var endFragment = startHtml + document.IndexOf("<!--EndFragment-->", StringComparison.Ordinal);
        var endHtml = startHtml + document.Length;
        var prefix = string.Format(CultureInfo.InvariantCulture, "Version:0.9\r\nStartHTML:{0:00000000}\r\nEndHTML:{1:00000000}\r\nStartFragment:{2:00000000}\r\nEndFragment:{3:00000000}\r\n", startHtml, endHtml, startFragment, endFragment);
        return prefix + document;
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
