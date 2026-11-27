using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SharpClip;

internal sealed record CapturedClip(string ContentType, byte[] Raw, string? Text, string? SourceApp);

internal static class ClipboardCapture
{
    private static int _skip;

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
            var source = ForegroundProcessName();
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

    public static void PasteIntoPreviousApp(IntPtr previous, string text, bool plain)
    {
        WriteText(text, plain);
        ForceForeground(previous);
        var down = Key(Win32.VK_CONTROL, false);
        var vDown = Key(Win32.VK_V, false);
        var vUp = Key(Win32.VK_V, true);
        var up = Key(Win32.VK_CONTROL, true);
        Win32.SendInput(4, [down, vDown, vUp, up], Marshal.SizeOf<Win32.INPUT>());
    }

    public static string Hash(byte[] data)
    {
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }

    public static string? ForegroundProcessName()
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
            return Path.GetFileName(new string(buffer, 0, size));
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

    private static void ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var foreground = Win32.GetForegroundWindow();
        var foreThread = Win32.GetWindowThreadProcessId(foreground, out _);
        var thisThread = Win32.GetCurrentThreadId();
        Win32.AttachThreadInput(thisThread, foreThread, true);
        Win32.SetForegroundWindow(hwnd);
        Win32.AttachThreadInput(thisThread, foreThread, false);
    }

    private static Win32.INPUT Key(uint vk, bool up) => new()
    {
        type = Win32.INPUT_KEYBOARD,
        ki = new Win32.KEYBDINPUT
        {
            wVk = (ushort)vk,
            dwFlags = up ? Win32.KEYEVENTF_KEYUP : 0,
        },
    };
}
