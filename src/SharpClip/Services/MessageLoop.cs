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
    private readonly WndProc _proc;

    public event Action<int>? HotkeyPressed;
    public event Action? ClipboardChanged;

    public WindowMessageLoop()
    {
        _proc = Proc;
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

    private void Run()
    {
        var wndClass = new Win32.WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            lpszClassName = ClassName,
            hInstance = Win32.GetModuleHandle(null),
        };
        Win32.RegisterClass(ref wndClass);
        _hwnd = Win32.CreateWindowEx(0, ClassName, "SharpClip", 0, 0, 0, 0, 0, new IntPtr(Win32.HWND_MESSAGE), IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);
        Win32.AddClipboardFormatListener(_hwnd);
        Win32.RegisterHotKey(_hwnd, HotkeyIds.Panel, Win32.MOD_CONTROL | Win32.MOD_SHIFT | Win32.MOD_NOREPEAT, Win32.VK_V);
        Win32.RegisterHotKey(_hwnd, HotkeyIds.Queue, Win32.MOD_CONTROL | Win32.MOD_SHIFT | Win32.MOD_NOREPEAT, Win32.VK_Q);
        TrayIcon.Attach(_hwnd);

        while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }

        Win32.UnregisterHotKey(_hwnd, HotkeyIds.Panel);
        Win32.UnregisterHotKey(_hwnd, HotkeyIds.Queue);
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
            case Win32.WM_DESTROY:
                Win32.PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
