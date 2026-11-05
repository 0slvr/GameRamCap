using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GameRamCap;

/// <summary>TEMPORARY diagnostics (only runs when DebugInputHost is true): looks for any signal that tells when the Win+V panel
/// (TextInputHost) is open, has focus, or closes. Writes "Probe ..." lines to log.txt. It logs process names, window class names,
/// handles and timings only: no window titles of other apps, no clipboard content, no typed text.</summary>
internal static class InputHostProbe
{
    delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] struct PRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct GUITHREADINFO
    {
        public int cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public PRect rcCaret;
    }

    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO info);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out PRect r);
    [DllImport("user32.dll")] static extern int GetWindowLongW(IntPtr hwnd, int index);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    static readonly List<IntPtr> _hooks = new();
    static WinEventProc? _proc;
    static volatile bool _stop;
    static int[] _pids = Array.Empty<int>();
    static long _pidsAt;
    static readonly Dictionary<int, string> _names = new();
    static int _budgetMinute, _budgetUsed;

    public static void Start()
    {
        if (_proc != null) return;
        _proc = OnEvent;
        // system: foreground, menus, mouse capture | object: create/destroy/show/hide, focus, state change, cloak/uncloak
        var ranges = new (uint, uint)[] { (0x0003, 0x0009), (0x8000, 0x8003), (0x8005, 0x8005), (0x800A, 0x800A), (0x8017, 0x8018) };
        foreach (var (a, b) in ranges)
            _hooks.Add(SetWinEventHook(a, b, IntPtr.Zero, _proc, 0, 0, 0 /* OUTOFCONTEXT */));
        _stop = false;
        new Thread(PollLoop) { IsBackground = true, Name = "InputHostProbe" }.Start();
        Logger.Write("Probe: started (events + focus + activity + windows)");
    }

    public static void Stop()
    {
        _stop = true;
        foreach (var h in _hooks) if (h != IntPtr.Zero) UnhookWinEvent(h);
        _hooks.Clear();
        _proc = null;
    }

    static string EvtName(uint e) => e switch
    {
        0x03 => "FOREGROUND", 0x04 => "MENUSTART", 0x05 => "MENUEND", 0x06 => "MENUPOPUPSTART", 0x07 => "MENUPOPUPEND",
        0x08 => "CAPTURESTART", 0x09 => "CAPTUREEND", 0x8000 => "CREATE", 0x8001 => "DESTROY", 0x8002 => "SHOW",
        0x8003 => "HIDE", 0x8005 => "FOCUS", 0x800A => "STATECHANGE", 0x8017 => "CLOAKED", 0x8018 => "UNCLOAKED",
        _ => "0x" + e.ToString("X"),
    };

    static bool IsInputHost(int pid)
    {
        long now = Environment.TickCount64;
        if (now - _pidsAt > 1000)
        {
            _pidsAt = now;
            var ps = Process.GetProcessesByName("TextInputHost");
            _pids = ps.Select(p => p.Id).ToArray();
            foreach (var p in ps) p.Dispose();
        }
        return Array.IndexOf(_pids, pid) >= 0;
    }

    static string ProcName(int pid)
    {
        lock (_names)
        {
            if (_names.TryGetValue(pid, out var n)) return n;
            try { using var p = Process.GetProcessById(pid); n = p.ProcessName; } catch { n = "?"; }
            if (_names.Count > 500) _names.Clear();
            return _names[pid] = n;
        }
    }

    static string Cls(IntPtr hwnd)
    {
        var sb = new StringBuilder(80);
        GetClassNameW(hwnd, sb, 80);
        return sb.ToString();
    }

    static string Win(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "-";
        GetWindowThreadProcessId(hwnd, out uint pid);
        return $"{ProcName((int)pid)}:{Cls(hwnd)}";
    }

    /// <summary>Milliseconds since the last hotkey / Esc / Enter / click mark (long.MaxValue if none).</summary>
    static double SinceMarkMs(out string label)
    {
        long k = Native.LastInputPanelKeyTicks, m = Native.LastPanelMarkTicks;
        label = m > k ? Native.LastPanelMarkLabel : "hotkey";
        long last = Math.Max(k, m);
        return last == 0 ? double.MaxValue : (DateTime.UtcNow.Ticks - last) / (double)TimeSpan.TicksPerMillisecond;
    }

    static bool Budget()
    {
        int m = Environment.TickCount / 60000;
        if (m != _budgetMinute) { _budgetMinute = m; _budgetUsed = 0; }
        return ++_budgetUsed <= 300;
    }

    static void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return;
            GetWindowThreadProcessId(hwnd, out uint pid);
            bool mine = IsInputHost((int)pid);
            double since = SinceMarkMs(out string label);
            // Every window event of TextInputHost, plus ANY process's window events for 3 s after the hotkey / Esc / Enter / click:
            // the real panel window may belong to another process.
            if (!mine && since > 3000) return;
            if (evt >= 0x8000 && evt != 0x8005 && evt != 0x800A && idObject != 0) return;   // top-level window objects only
            if (!Budget()) return;
            int cloak = 0; DwmGetWindowAttribute(hwnd, 14, out cloak, sizeof(int));
            Logger.Write($"Probe event: {EvtName(evt)} {ProcName((int)pid)}:{Cls(hwnd)} hwnd=0x{hwnd.ToInt64():X} obj={idObject} vis={(IsWindowVisible(hwnd) ? 1 : 0)} cloak={cloak}"
                + (mine ? " [TextInputHost]" : "") + (since < 60000 ? $"  +{since:0}ms after {label}" : ""));
        }
        catch { }
    }

    static string Gui(GUITHREADINFO g) =>
        $"active={Win(g.hwndActive)} focus={Win(g.hwndFocus)} capture={Win(g.hwndCapture)} menu={Win(g.hwndMenuOwner)} caret={Win(g.hwndCaret)} flags={g.flags}";

    static string TihWindows(int pid)
    {
        var list = new List<string>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint p);
            if (p != (uint)pid) return true;
            int cloak = 0; DwmGetWindowAttribute(h, 14, out cloak, sizeof(int));
            GetWindowRect(h, out var r);
            list.Add($"[{Cls(h)}|vis={(IsWindowVisible(h) ? 1 : 0)}|cloak={cloak}|ex=0x{GetWindowLongW(h, -20):X}|{r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}]");
            return true;
        }, IntPtr.Zero);
        list.Sort(StringComparer.Ordinal);
        return string.Concat(list);
    }

    static void PollLoop()
    {
        string lastFocus = "", lastWin = "", lastMark = "";
        bool busy = false; DateTime quietSince = DateTime.UtcNow;
        TimeSpan prevCpu = TimeSpan.Zero; long prevPf = 0; DateTime prevAt = DateTime.MinValue; int prevPid = 0;
        while (!_stop)
        {
            try
            {
                // 1) who has focus / activation / capture / caret: foreground thread and every thread of TextInputHost
                var fg = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
                string focus = GetGUIThreadInfo(0, ref fg) ? "FG[" + Gui(fg) + "]" : "FG[?]";
                string win = "";
                foreach (var p in Process.GetProcessesByName("TextInputHost"))
                {
                    try
                    {
                        var own = new List<string>();
                        foreach (ProcessThread t in p.Threads)
                        {
                            var ti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
                            if (GetGUIThreadInfo((uint)t.Id, ref ti)
                                && (ti.hwndActive != IntPtr.Zero || ti.hwndFocus != IntPtr.Zero || ti.hwndCapture != IntPtr.Zero || ti.hwndCaret != IntPtr.Zero || ti.hwndMenuOwner != IntPtr.Zero))
                                own.Add($"T{t.Id}[{Gui(ti)}]");
                        }
                        focus += own.Count == 0 ? " TIH[no thread has active/focus]" : " TIH" + string.Concat(own);
                        win += $"pid{p.Id} " + TihWindows(p.Id);

                        // 2) activity: CPU % of one core and page faults per second
                        var cpu = p.TotalProcessorTime;
                        Native.GetMemory(p.Id, out long ws, out long pf);
                        var now = DateTime.UtcNow;
                        if (prevPid == p.Id && prevAt != DateTime.MinValue)
                        {
                            double dt = Math.Max(0.05, (now - prevAt).TotalSeconds);
                            double cpuPct = (cpu - prevCpu).TotalSeconds / dt * 100, pfRate = Math.Max(0, pf - prevPf) / dt;
                            bool hot = cpuPct > 2 || pfRate > 100;
                            if (hot) quietSince = now;
                            bool nowBusy = hot || (busy && (now - quietSince).TotalSeconds < 1.5);
                            if (nowBusy != busy)
                            {
                                busy = nowBusy;
                                string since = SinceMarkMs(out string lab) < 60000 ? $"  +{SinceMarkMs(out _):0}ms after {lab}" : "";
                                Logger.Write($"Probe activity: {(busy ? "BUSY" : "quiet")} cpu={cpuPct:0}% pf={pfRate:0}/s ws={ws / (1024 * 1024)}MB{since}");
                            }
                        }
                        prevCpu = cpu; prevPf = pf; prevAt = now; prevPid = p.Id;
                    }
                    finally { p.Dispose(); }
                }

                string since2 = SinceMarkMs(out string lab2) < 60000 ? $"  +{SinceMarkMs(out _):0}ms after {lab2}" : "";
                if (focus != lastFocus) { lastFocus = focus; Logger.Write("Probe focus: " + focus + since2); }
                if (win != lastWin) { lastWin = win; Logger.Write("Probe windows: " + (win.Length == 0 ? "(TextInputHost not running)" : win) + since2); }

                string mark = Native.LastPanelMarkTicks.ToString();
                if (mark != lastMark) { lastMark = mark; if (Native.LastPanelMarkTicks != 0) Logger.Write($"Probe mark: {Native.LastPanelMarkLabel}"); }
            }
            catch { }
            Thread.Sleep(200);
        }
    }
}
