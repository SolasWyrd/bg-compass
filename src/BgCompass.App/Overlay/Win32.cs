using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BgCompass.App.Overlay;

// Every Win32 call of the app lives in this file, reached only behind OperatingSystem.IsWindows().
// What it does: find the Hearthstone window by class and title (or process name), read its client rectangle, learn which window is in
// front, set extended styles on our own windows, and register two global hotkeys. It never opens the game process,
// reads its memory, injects anything or sends input.

[SupportedOSPlatform("windows")]
internal static class Native
{
    const string User32 = "user32.dll";

    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000;
    public const uint WM_HOTKEY = 0x0312, WM_QUIT = 0x0012;
    public const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10, LWA_ALPHA = 2;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MSG { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; }

    [DllImport(User32, CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")] public static extern IntPtr FindWindow(string cls, string? title);
    [DllImport(User32)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport(User32)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport(User32)] public static extern IntPtr GetForegroundWindow();
    [DllImport(User32)] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport(User32)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsIconic(IntPtr h);
    [DllImport(User32, EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport(User32, EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    [DllImport(User32)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetLayeredWindowAttributes(IntPtr h, uint colorKey, byte alpha, uint flags);
    [DllImport(User32)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport(User32, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport(User32)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport(User32)] public static extern int GetMessageW(out MSG msg, IntPtr h, uint min, uint max);
    [DllImport(User32)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool PostThreadMessageW(uint thread, uint msg, UIntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
}

/// <summary>FindWindow("UnityWndClass", "Hearthstone") plus GetClientRect/ClientToScreen and GetForegroundWindow.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsGameProbe : IGameWindowProbe
{
    readonly uint _ourPid = (uint)Environment.ProcessId;

    public GameWindowInfo Poll()
    {
        var h = Native.FindWindow("UnityWndClass", "Hearthstone");
        if (h == IntPtr.Zero) h = FindByProcessName();
        if (h == IntPtr.Zero || Native.IsIconic(h) || !Native.GetClientRect(h, out var rc))
            return new GameWindowInfo(false, default, false);
        var origin = new Native.POINT();
        if (!Native.ClientToScreen(h, ref origin)) return new GameWindowInfo(false, default, false);
        var rect = new PxRect(origin.X, origin.Y, rc.Right - rc.Left, rc.Bottom - rc.Top);
        if (rect.W < 100 || rect.H < 100) return new GameWindowInfo(false, default, false);

        var fg = Native.GetForegroundWindow();
        var show = fg == h;
        if (!show && fg != IntPtr.Zero)
        {
            Native.GetWindowThreadProcessId(fg, out var pid);
            show = pid == _ourPid;
        }
        return new GameWindowInfo(true, rect, show);
    }

    /// <summary>For clients with a localized window title. Reads only the process list and its window handle.</summary>
    static IntPtr FindByProcessName()
    {
        var found = IntPtr.Zero;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("Hearthstone"))
        {
            using (p)
                if (found == IntPtr.Zero) found = p.MainWindowHandle;
        }
        return found;
    }
}

[SupportedOSPlatform("windows")]
public static class WindowStyles
{
    /// <summary>
    /// Never takes focus, hidden from Alt+Tab and the taskbar. Click-through additionally lets every mouse event fall
    /// to the window below (a layered, transparent window).
    /// </summary>
    public static void Apply(IntPtr hwnd, bool clickThrough)
    {
        if (hwnd == IntPtr.Zero) return;
        var style = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        var want = style | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
        want = clickThrough ? want | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED : want & ~Native.WS_EX_TRANSPARENT;
        if (want == style) return;
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(want));
        // A window made layered stays invisible until its layer attributes are set; alpha 255 keeps per-pixel transparency.
        if ((want & Native.WS_EX_LAYERED) != 0 && (style & Native.WS_EX_LAYERED) == 0)
            Native.SetLayeredWindowAttributes(hwnd, 0, 255, Native.LWA_ALPHA);
    }

    /// <summary>Puts the window back above the game without activating it.</summary>
    public static void RaiseTopmost(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }
}

/// <summary>RegisterHotKey on a dedicated thread with its own message loop. Ctrl+Shift+H and Ctrl+Shift+T.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHotkeys : IHotkeys
{
    const uint VkH = 0x48, VkT = 0x54;
    readonly object _gate = new();
    readonly ManualResetEventSlim _ready = new();
    readonly List<HotkeyStatus> _status = [];
    uint _threadId;
    Thread? _thread;

    public IReadOnlyList<HotkeyStatus> Status { get { lock (_gate) return _status.ToList(); } }
    public event Action<HotkeyAction>? Pressed;

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "BgCompass hotkeys" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(3));
    }

    void Loop()
    {
        _threadId = Native.GetCurrentThreadId();
        Register(1, "Показать или скрыть панели", "Ctrl+Shift+H", VkH);
        Register(2, "Сквозной режим", "Ctrl+Shift+T", VkT);
        _ready.Set();
        while (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message != Native.WM_HOTKEY) continue;
            var action = (int)msg.wParam == 1 ? HotkeyAction.TogglePanels : HotkeyAction.ToggleClickThrough;
            try { Pressed?.Invoke(action); } catch (Exception e) { Runtime.AppLog.Error("Обработчик горячей клавиши", e); }
        }
        Native.UnregisterHotKey(IntPtr.Zero, 1);
        Native.UnregisterHotKey(IntPtr.Zero, 2);
    }

    void Register(int id, string name, string keys, uint vk)
    {
        var ok = Native.RegisterHotKey(IntPtr.Zero, id, Native.MOD_CONTROL | Native.MOD_SHIFT | Native.MOD_NOREPEAT, vk);
        var err = ok ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        var detail = ok ? "Зарегистрирована."
            : err == Native.ERROR_HOTKEY_ALREADY_REGISTERED
                ? "Сочетание уже занято другой программой; используйте меню в трее."
                : $"Не удалось зарегистрировать (код Windows {err}); используйте меню в трее.";
        lock (_gate) _status.Add(new HotkeyStatus(name, keys, ok, detail));
        Runtime.AppLog.Info($"Горячая клавиша {keys}: {detail}");
    }

    public void Dispose()
    {
        if (_threadId != 0) Native.PostThreadMessageW(_threadId, Native.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
        _thread?.Join(1000);
    }
}
