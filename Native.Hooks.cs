using System.Runtime.InteropServices;
using System.Text;

namespace GameRamCap;

// Hooks and window events.
//   * WinEvent hooks (foreground / window activity / Alt+Tab switcher) run on the UI thread, which pumps messages.
//   * The optional low-level keyboard + mouse hooks run on their OWN thread with its own message loop, so a busy UI thread can
//     never delay keyboard / mouse input of the whole system (Windows waits for a low-level hook to return).
internal static partial class Native
{
    delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint thread, uint flags);

    [DllImport("user32.dll")]
    static extern bool UnhookWinEvent(IntPtr hook);

    const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;

    // ---------------------------------------------------------------- foreground change
    static WinEventProc? _fgProc;   // kept in a field so the garbage collector cannot free it while Windows still calls it
    static IntPtr _fgHook;

    /// <summary>Calls onChange the moment Windows switches the foreground window (no waiting for the next engine tick).
    /// Must be called from a thread that pumps messages (the UI thread).</summary>
    public static void WatchForeground(Action onChange)
    {
        if (_fgHook != IntPtr.Zero) return;
        _fgProc = (_, _, hwnd, idObject, _, _, _) =>
        {
            if (idObject != 0 || hwnd == IntPtr.Zero) return;
            try { GetWindowThreadProcessId(hwnd, out uint pid); if (pid != 0) _actTicks[(int)pid] = DateTime.UtcNow.Ticks; } catch { }
            try { onChange(); } catch { }
        };
        _fgHook = SetWinEventHook(0x0003 /* EVENT_SYSTEM_FOREGROUND */, 0x0003, IntPtr.Zero, _fgProc, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    public static void StopWatchingForeground()
    {
        if (_fgHook != IntPtr.Zero) { UnhookWinEvent(_fgHook); _fgHook = IntPtr.Zero; }
    }

    // ---------------------------------------------------------------- low-level keyboard / mouse hooks (own thread)
    delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public int ptX, ptY; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookExW(int idHook, LowLevelProc fn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vk);

    [DllImport("user32.dll")]
    static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    static extern bool PeekMessageW(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    static extern bool PostThreadMessageW(uint threadId, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandleW(string? name);

    static LowLevelProc? _kbProc, _msProc;   // kept in fields so the garbage collector cannot free them
    static IntPtr _kbHook, _msHook;
    static Thread? _hookThread;
    static uint _hookThreadId;

    /// <summary>Raised (from the hook thread, so keep it fast) the instant Win+V / Win+. / Win+; / Win+H is pressed.</summary>
    public static Action? OnPanelKey;
    /// <summary>Raised (from the hook thread, so keep it fast) the instant Win, Win+S or Win+Q is pressed (Start menu / Search).</summary>
    public static Action? OnStartKey;
    /// <summary>Raised the instant the Alt+Tab switcher opens (keyboard hook when it is on, otherwise the switcher window event).</summary>
    public static Action? OnAltTabKey;
    /// <summary>Raised (from the hook thread, so keep it fast) when any mouse button goes down, with the screen position.</summary>
    public static Action<int, int>? OnMouseDown;

    /// <summary>Installs the keyboard + mouse hooks on a dedicated thread. Both or none: if one cannot be installed, the other is removed again.
    /// Nothing is logged or stored: the hooks only look at "Win / Alt held + one key" and at "a button went down".
    /// Every key and click is passed on to Windows untouched.</summary>
    public static void WatchInputPanelKeys()
    {
        if (_hookThread != null) return;
        var ready = new ManualResetEventSlim(false);
        var t = new Thread(() => HookLoop(ready)) { IsBackground = true, Name = "GameRamCap input hooks", Priority = ThreadPriority.AboveNormal };
        _hookThread = t;
        t.Start();
        ready.Wait(3000);
    }

    static void HookLoop(ManualResetEventSlim ready)
    {
        try
        {
            _hookThreadId = GetCurrentThreadId();
            PeekMessageW(out _, IntPtr.Zero, 0, 0, 0);   // creates this thread's message queue, so the quit message can reach it
            _kbProc = KeyboardCallback;
            _msProc = MouseCallback;
            IntPtr mod = GetModuleHandleW(null);
            _kbHook = SetWindowsHookExW(13 /* WH_KEYBOARD_LL */, _kbProc, mod, 0);
            _msHook = SetWindowsHookExW(14 /* WH_MOUSE_LL */, _msProc, mod, 0);
            if (_kbHook == IntPtr.Zero || _msHook == IntPtr.Zero)
            {
                Logger.Write("Global keyboard/mouse hooks could not be installed; continuing without them.");
                return;   // the finally block removes whichever one was installed
            }
            ready.Set();
            while (GetMessageW(out _, IntPtr.Zero, 0, 0) > 0) { }   // low-level hooks are served from inside GetMessage; WM_QUIT ends the loop
        }
        catch { }
        finally
        {
            if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook);
            if (_msHook != IntPtr.Zero) UnhookWindowsHookEx(_msHook);
            _kbHook = _msHook = IntPtr.Zero;
            _hookThreadId = 0;
            _hookThread = null;
            ready.Set();
        }
    }

    static IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            int msg = wParam.ToInt32();
            if (nCode >= 0 && (msg == 0x0100 || msg == 0x0104))   // WM_KEYDOWN / WM_SYSKEYDOWN
            {
                int vk = Marshal.ReadInt32(lParam);               // KBDLLHOOKSTRUCT.vkCode
                bool winHeld = ((GetAsyncKeyState(0x5B) | GetAsyncKeyState(0x5C)) & 0x8000) != 0;
                if (vk == 0x5B || vk == 0x5C || ((vk == 0x53 || vk == 0x51) && winHeld))   // Win, or Win+S / Win+Q
                {
                    try { OnStartKey?.Invoke(); } catch { }
                }
                if (vk == 0x09 && (GetAsyncKeyState(0x12) & 0x8000) != 0)   // Tab with Alt held
                {
                    try { OnAltTabKey?.Invoke(); } catch { }
                }
                if ((vk == 0x56 || vk == 0xBE || vk == 0xBA || vk == 0x48) && winHeld)   // V . ; H with Win held
                {
                    try { OnPanelKey?.Invoke(); } catch { }
                }
            }
        }
        catch { }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    static IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            int msg = wParam.ToInt32();
            if (nCode >= 0 && (msg == 0x0201 || msg == 0x0204 || msg == 0x0207 || msg == 0x020B))   // left / right / middle / X1-X2 button down
            {
                var cb = OnMouseDown;
                if (cb != null) cb(Marshal.ReadInt32(lParam), Marshal.ReadInt32(lParam, 4));   // MSLLHOOKSTRUCT.pt; the engine does the work off this thread
            }
        }
        catch { }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    public static void StopWatchingInputPanelKeys()
    {
        var t = _hookThread;
        if (t == null) return;
        uint id = _hookThreadId;
        if (id != 0) PostThreadMessageW(id, 0x0012 /* WM_QUIT */, UIntPtr.Zero, IntPtr.Zero);
        t.Join(1500);
    }

    // ---------------------------------------------------------------- window activity of ANY process
    // Windows raises these events for every app: a window shown, uncloaked, cloaked, or given focus. "This process just did something
    // visible" works for new tools and survives Windows updates. It marks transitions, not how long a window stays open.
    // Events caused by this program's own windows are skipped (WINEVENT_SKIPOWNPROCESS).
    static WinEventProc? _actProc;
    static readonly List<IntPtr> _actHooks = new();
    static readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> _actTicks = new();
    static Action? _onPanelOpened;

    /// <param name="onPanelOpened">Called the moment the Win+V panel takes keyboard focus (on the UI thread).</param>
    public static void WatchWindowActivity(Action? onPanelOpened = null)
    {
        if (_actProc != null) return;
        _onPanelOpened = onPanelOpened;
        _actProc = (_, evt, hwnd, idObject, _, _, _) =>
        {
            try
            {
                if (evt == 0x0014)   // EVENT_SYSTEM_SWITCHSTART: the Alt+Tab switcher opened. The keyboard hook does this itself when it is on.
                {
                    if (_kbHook == IntPtr.Zero) OnAltTabKey?.Invoke();
                    return;
                }
                if (hwnd == IntPtr.Zero) return;
                if (evt != 0x8005 && idObject != 0) return;   // window-level events only (focus may come from a child control)
                if (evt == 0x8005 && _onPanelOpened != null)   // focus moved into the Win+V panel: lift its limit right now
                {
                    var cls = new StringBuilder(80);
                    GetClassNameW(hwnd, cls, 80);
                    if (cls.ToString() == "Windows.UI.Core.CoreComponentInputSource") _onPanelOpened();
                }
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != 0) _actTicks[(int)pid] = DateTime.UtcNow.Ticks;
            }
            catch { }
        };
        // switcher start | window show | focus | cloaked + uncloaked  (the foreground event is handled by WatchForeground)
        foreach (var (a, b) in new (uint, uint)[] { (0x0014, 0x0014), (0x8002, 0x8002), (0x8005, 0x8005), (0x8017, 0x8018) })
        {
            var h = SetWinEventHook(a, b, IntPtr.Zero, _actProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            if (h != IntPtr.Zero) _actHooks.Add(h);
        }
    }

    public static void StopWatchingWindowActivity()
    {
        foreach (var h in _actHooks) UnhookWinEvent(h);
        _actHooks.Clear();
        _actProc = null;
    }

    /// <summary>Process ids that showed / uncloaked / cloaked / focused a window within the last <paramref name="seconds"/> seconds.</summary>
    public static List<int> RecentlyActivePids(double seconds)
    {
        long now = DateTime.UtcNow.Ticks, limit = now - (long)(seconds * TimeSpan.TicksPerSecond);
        var list = new List<int>();
        foreach (var kv in _actTicks)
        {
            if (kv.Value >= limit) list.Add(kv.Key);
            else if (now - kv.Value > TimeSpan.TicksPerMinute * 5) _actTicks.TryRemove(kv.Key, out _);   // forget old entries
        }
        return list;
    }
}
