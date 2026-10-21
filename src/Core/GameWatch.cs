using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace PadDeck.Core
{
    /// <summary>
    /// Notices when a fullscreen game comes to the front, so PadDeck can warn about things that ruin a session:
    /// a GPU tool still holding VRAM, or no replay buffer to clip from.
    /// </summary>
    public static class GameWatch
    {
        /// <summary>Apps that go fullscreen without being games: browsers (F11, video), players, viewers.</summary>
        public static readonly HashSet<string> NotGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "thorium", "chrome", "msedge", "firefox", "brave", "opera", "PotPlayerMini64", "PotPlayer64", "PotPlayerMini",
            "mpv", "vlc", "ImageGlass", "ClipTrimmer", "obs64", "Discord", "ApplicationFrameHost", "Photos", "Video.UI",
            "Microsoft.Media.Player", "POWERPNT", "steamwebhelper", "VoiceChain", "FanControl",
        };

        /// <summary>A borderless or exclusive fullscreen window: no title bar, and covering its whole monitor.</summary>
        public static bool IsFullscreen(PxRect win, PxRect mon, int style) =>
            (style & 0x00C00000) != 0x00C00000 && win.L <= mon.L && win.T <= mon.T && win.R >= mon.R && win.B >= mon.B; // WS_CAPTION

        public static bool IsGame(string process) =>
            !string.IsNullOrEmpty(process) && !NotGames.Contains(process) && !Reset.AlwaysKeep.Contains(process);

        /// <summary>The fullscreen game in front, or null.</summary>
        public static (int pid, string name)? GameInFront()
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return null;
            IntPtr old = SetThreadDpiAwarenessContext(new IntPtr(-4)); // physical pixels
            try
            {
                var mi = new MONITORINFO { cb = Marshal.SizeOf<MONITORINFO>() };
                if (!GetWindowRect(h, out var r) || !GetMonitorInfo(MonitorFromWindow(h, 2), ref mi)) return null;
                if (!IsFullscreen(r, mi.mon, GetWindowLong(h, -16))) return null;
            }
            finally { SetThreadDpiAwarenessContext(old); }
            GetWindowThreadProcessId(h, out int pid);
            string name;
            try { using var p = Process.GetProcessById(pid); name = p.ProcessName; } catch { return null; }
            return IsGame(name) ? (pid, name) : null;
        }

        /// <summary>The warning lines, given which GPU tools are on (title, button index) and the replay buffer state. Pure, for tests.</summary>
        /// <param name="replay">True = buffering, false = off, null = OBS couldn't be asked (no warning then).</param>
        public static List<string> Lines(IList<(string title, int index)> toolsOn, bool? replay, int replayButton)
        {
            var lines = toolsOn.Select(t => $"{t.title} is on · press {Key(t.index)} twice to turn it off").ToList();
            if (replay == false)
                lines.Add(toolsOn.Count > 0 || replayButton < 0 ? "Replay buffer is off" : $"Replay buffer is off · press {Key(replayButton)} to turn it on");
            return lines;
        }

        static string Key(int index) => "F" + (19 + index);

        /// <summary>Checks the GPU tools and OBS right now. Called off the UI thread.</summary>
        public static List<string> Problems(IList<ButtonConfig> buttons)
        {
            var on = new List<(string, int)>();
            int replayButton = -1;
            for (int i = 0; i < buttons.Count; i++)
            {
                var b = buttons[i];
                if (b.IsToggle && b.GpuHeavy && Toggle.IsOn(b.RunningProcess)) on.Add((ActionRunner.Title(b), i));
                if (replayButton < 0 && b.Kind == ActionKind.Obs && b.ObsAction == ObsCommand.SaveReplay) replayButton = i;
            }
            bool? replay;
            if (!Obs.IsRunning()) replay = false;
            else replay = Obs.State()?.Replay; // null: running but not answering (e.g. still starting), so don't nag
            return Lines(on, replay, replayButton);
        }

        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cb; public PxRect mon, work; public int flags; }
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out PxRect r);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
    }
}
