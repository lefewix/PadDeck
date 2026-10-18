using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace PadDeck.Core
{
    public enum MonitorPick { Left, Right, Main }
    public enum Zone { LeftHalf, RightHalf, TopHalf, BottomHalf, Full, Maximized }

    public struct PxRect { public int L, T, R, B; public int W => R - L; public int H => B - T; public override string ToString() => $"({L},{T})-({R},{B})"; }

    /// <summary>
    /// Moves an app's main window to a fixed spot every time it opens (shown from the tray, launched, restored from hiding).
    /// Uses a WinEvent hook, so it costs nothing while idle. Install on a thread that pumps messages (the UI thread).
    /// Windows are moved from the thread pool: moving another app's window waits for that app, and a hung app
    /// shouldn't freeze PadDeck.
    /// </summary>
    public sealed class WindowPlacer : IDisposable
    {
        readonly Func<IReadOnlyList<WindowRule>> rules;
        readonly WinEventProc proc;
        readonly Dictionary<IntPtr, DateTime> recent = new Dictionary<IntPtr, DateTime>();
        IntPtr hook;

        public WindowPlacer(Func<IReadOnlyList<WindowRule>> rules)
        {
            this.rules = rules;
            proc = OnEvent;
            hook = SetWinEventHook(0x8002, 0x8002, IntPtr.Zero, proc, 0, 0, 0); // EVENT_OBJECT_SHOW, out of context
        }

        void OnEvent(IntPtr h, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return; // only whole windows
            if (!IsMainWindow(hwnd)) return;
            var rule = Match(hwnd);
            if (rule == null) return;
            if (recent.TryGetValue(hwnd, out var t) && DateTime.UtcNow - t < TimeSpan.FromSeconds(1)) return;
            recent[hwnd] = DateTime.UtcNow;
            if (recent.Count > 64) recent.Clear();
            // Apps often restore their own saved position right after showing, so place twice.
            _ = PlaceLater(hwnd, rule, 150);
            _ = PlaceLater(hwnd, rule, 900);
        }

        async Task PlaceLater(IntPtr hwnd, WindowRule rule, int ms)
        {
            await Task.Delay(ms).ConfigureAwait(false);
            if (IsWindowVisible(hwnd)) Place(hwnd, rule);
        }

        WindowRule Match(IntPtr hwnd)
        {
            string name = ProcessName(hwnd);
            return name == null ? null : rules().FirstOrDefault(r => string.Equals(r.Process, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Places every open main window that has a rule (the "Apply now" button).</summary>
        public int ApplyAll()
        {
            int n = 0;
            EnumWindows((h, l) =>
            {
                if (IsWindowVisible(h) && IsMainWindow(h) && Match(h) is WindowRule r) { Place(h, r); n++; }
                return true;
            }, IntPtr.Zero);
            return n;
        }

        // ---------------- geometry (pure, tested) ----------------

        public static PxRect ZoneRect(PxRect work, Zone z)
        {
            int midX = work.L + work.W / 2, midY = work.T + work.H / 2;
            return z switch
            {
                Zone.LeftHalf => new PxRect { L = work.L, T = work.T, R = midX, B = work.B },
                Zone.RightHalf => new PxRect { L = midX, T = work.T, R = work.R, B = work.B },
                Zone.TopHalf => new PxRect { L = work.L, T = work.T, R = work.R, B = midY },
                Zone.BottomHalf => new PxRect { L = work.L, T = midY, R = work.R, B = work.B },
                _ => work,
            };
        }

        /// <summary>Monitors as (bounds, work area, primary). Left/Right are by position, so they survive display renumbering.</summary>
        public static int PickMonitor(IList<(PxRect bounds, bool primary)> mons, MonitorPick pick)
        {
            if (mons.Count == 0) return -1;
            var idx = Enumerable.Range(0, mons.Count);
            return pick switch
            {
                MonitorPick.Left => idx.OrderBy(i => mons[i].bounds.L).First(),
                MonitorPick.Right => idx.OrderByDescending(i => mons[i].bounds.L).First(),
                _ => idx.FirstOrDefault(i => mons[i].primary),
            };
        }

        /// <summary>Windows 10/11 windows have invisible resize borders; grow the target by them so the visible edges line up.</summary>
        public static PxRect WithBorders(PxRect target, PxRect window, PxRect visible) => new PxRect
        {
            L = target.L - (visible.L - window.L),
            T = target.T - (visible.T - window.T),
            R = target.R + (window.R - visible.R),
            B = target.B + (window.B - visible.B),
        };

        // ---------------- placing ----------------

        public static void Place(IntPtr hwnd, WindowRule rule)
        {
            // Physical pixels on every monitor, whatever PadDeck's own DPI mode is.
            IntPtr old = SetThreadDpiAwarenessContext(new IntPtr(-4));
            try
            {
                var mons = Monitors();
                int i = PickMonitor(mons.Select(m => (m.bounds, m.primary)).ToList(), rule.Monitor);
                if (i < 0) return;
                var work = mons[i].work;
                if (IsIconic(hwnd) || IsZoomed(hwnd)) ShowWindow(hwnd, 9); // SW_RESTORE, so it can be moved
                if (rule.Zone == Zone.Maximized)
                {
                    SetWindowPos(hwnd, IntPtr.Zero, work.L + 50, work.T + 50, work.W - 100, work.H - 100, 0x14); // onto the monitor first
                    ShowWindow(hwnd, 3); // SW_MAXIMIZE
                    return;
                }
                var target = ZoneRect(work, rule.Zone);
                GetWindowRect(hwnd, out var win);
                if (DwmGetWindowAttribute(hwnd, 9, out var vis, Marshal.SizeOf<PxRect>()) == 0) target = WithBorders(target, win, vis);
                SetWindowPos(hwnd, IntPtr.Zero, target.L, target.T, target.W, target.H, 0x14); // NOZORDER | NOACTIVATE
            }
            finally { SetThreadDpiAwarenessContext(old); }
        }

        static List<(PxRect bounds, PxRect work, bool primary)> Monitors()
        {
            var l = new List<(PxRect, PxRect, bool)>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr dc, ref PxRect r, IntPtr d) =>
            {
                var mi = new MONITORINFO { cb = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(m, ref mi)) l.Add((mi.mon, mi.work, (mi.flags & 1) != 0));
                return true;
            }, IntPtr.Zero);
            return l;
        }

        /// <summary>A resizable, unowned, top-level window: the app's main window rather than a splash, dialog or popup.</summary>
        static bool IsMainWindow(IntPtr h)
        {
            if (GetAncestor(h, 2) != h || GetWindow(h, 4) != IntPtr.Zero) return false;   // GA_ROOT, GW_OWNER
            int style = GetWindowLong(h, -16), ex = GetWindowLong(h, -20);
            if ((ex & 0x80) != 0) return false;                                          // WS_EX_TOOLWINDOW
            return (style & 0x40000) != 0;                                                // WS_THICKFRAME
        }

        static string ProcessName(IntPtr hwnd)
        {
            GetWindowThreadProcessId(hwnd, out int pid);
            try { using var p = Process.GetProcessById(pid); return p.ProcessName; } catch { return null; }
        }

        public void Dispose()
        {
            if (hook != IntPtr.Zero) { UnhookWinEvent(hook); hook = IntPtr.Zero; }
        }

        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        delegate bool EnumProc(IntPtr h, IntPtr l);
        delegate bool MonitorEnumProc(IntPtr m, IntPtr dc, ref PxRect r, IntPtr d);
        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cb; public PxRect mon, work; public int flags; }

        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint thread, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr h);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc f, IntPtr d);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out PxRect r);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out PxRect v, int size);
    }
}
