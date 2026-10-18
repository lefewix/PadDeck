using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PadDeck.Core
{
    /// <summary>
    /// "Reset": stops the GPU-heavy tools, asks every non-core app window to close (like clicking X, so apps with
    /// unsaved work can still prompt), then starts the core apps that aren't running.
    /// </summary>
    public static class Reset
    {
        /// <summary>Never closed: the shell, consoles (Claude Code runs in Windows Terminal) and PadDeck itself.</summary>
        public static readonly HashSet<string> AlwaysKeep = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "TextInputHost", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp",
            "LockApp", "dwm", "ShellHost", "Widgets", "ctfmon", "sihost", "PadDeck",
            "WindowsTerminal", "OpenConsole", "conhost", "cmd", "powershell", "pwsh", "Taskmgr",
        };

        const string LlmServer = "llama-server";
        const string ComfyStop = @"C:\claude-tools\ComfyUI\Stop.bat";
        const string ComfyPython = @"\claude-tools\ComfyUI\";

        public static List<CoreApp> DefaultCore() => new List<CoreApp>
        {
            new CoreApp { Name = "OBS Studio", Process = "obs64", Target = @"%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\OBS Studio (replay buffer).lnk" },
            new CoreApp { Name = "Discord", Process = "Discord", Target = @"%LOCALAPPDATA%\Discord\Update.exe", Args = "--processStart Discord.exe" },
            new CoreApp { Name = "Fan Control", Process = "FanControl", Target = "schtasks.exe", Args = "/run /tn FanControl" },
            new CoreApp { Name = "Thorium", Process = "thorium", Target = @"C:\Program Files\Thorium\Application\thorium.exe" },
        };

        public sealed record AppWindow(IntPtr Hwnd, int Pid, string Process, string Title);

        public sealed class Plan
        {
            public List<AppWindow> Close = new List<AppWindow>();
            public List<CoreApp> Start = new List<CoreApp>();
            public bool Llm, Comfy;

            /// <summary>Distinct app names, in window order.</summary>
            public List<string> CloseNames => Close.Select(w => w.Process).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Which windows a reset would close, given the windows on screen. Pure, for tests.</summary>
        public static List<AppWindow> ToClose(IEnumerable<AppWindow> windows, IEnumerable<CoreApp> core)
        {
            var keep = new HashSet<string>(core.Select(c => c.Process.Trim()), StringComparer.OrdinalIgnoreCase);
            return windows.Where(w => !AlwaysKeep.Contains(w.Process) && !keep.Contains(w.Process)).ToList();
        }

        public static Plan MakePlan(ButtonConfig b)
        {
            var p = new Plan { Close = ToClose(Windows(), b.Core) };
            foreach (var c in b.Core)
                if (!string.IsNullOrWhiteSpace(c.Target) && !Running(c.Process)) p.Start.Add(c);
            if (b.StopGpu)
            {
                p.Llm = Running(LlmServer);
                p.Comfy = ComfyRunning();
            }
            return p;
        }

        public static string Describe(Plan p)
        {
            var parts = new List<string>();
            var names = p.CloseNames;
            if (p.Llm || p.Comfy) parts.Add("stop " + string.Join(" and ", new[] { p.Llm ? "the LLM" : null, p.Comfy ? "ComfyUI" : null }.Where(s => s != null)));
            if (names.Count > 0) parts.Add("close " + List(names));
            if (p.Start.Count > 0) parts.Add("start " + List(p.Start.Select(c => c.Name).ToList()));
            if (parts.Count == 0) return "Nothing to do: only core apps are open";
            string s = string.Join(", ", parts);
            return char.ToUpper(s[0]) + s.Substring(1);
        }

        static string List(List<string> names) => names.Count <= 3 ? string.Join(", ", names) : string.Join(", ", names.Take(3)) + $" +{names.Count - 3}";

        /// <summary>Runs the reset. Called off the UI thread.</summary>
        public static Outcome Run(ButtonConfig b)
        {
            var plan = MakePlan(b);
            var done = new List<string>();

            if (plan.Llm)
            {
                foreach (var pr in Process.GetProcessesByName(LlmServer)) using (pr) { try { pr.Kill(); } catch { } }
                done.Add("stopped the LLM");
            }
            if (plan.Comfy && File.Exists(ComfyStop))
            {
                // keep-obs-closed: Stop.bat would otherwise restart OBS itself; Reset starts the core apps below
                RunHidden("cmd.exe", $"/c {ComfyStop} keep-obs-closed", 20000);
                done.Add("stopped ComfyUI");
            }

            foreach (var w in plan.Close) PostMessage(w.Hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
            // Give apps a few seconds; anything that prompts (unsaved work) or refuses stays open.
            var open = plan.Close;
            for (int i = 0; i < 24 && open.Count > 0; i++)
            {
                Thread.Sleep(250);
                open = open.Where(w => IsWindow(w.Hwnd) && IsWindowVisible(w.Hwnd)).ToList();
            }
            int closed = plan.CloseNames.Count - open.Select(w => w.Process).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (closed > 0) done.Add($"closed {closed} app{(closed == 1 ? "" : "s")}");

            var failed = new List<string>();
            var started = new List<string>();
            foreach (var c in plan.Start)
            {
                if (Running(c.Process)) continue; // started meanwhile (e.g. by a launcher script)
                try { Launch(c); started.Add(c.Name); }
                catch { failed.Add(c.Name); }
            }
            if (started.Count > 0) done.Add("started " + string.Join(", ", started));

            var stillOpen = open.Select(w => w.Process).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string detail = done.Count == 0 ? "Already clean" : char.ToUpper(done[0][0]) + string.Join(", ", done).Substring(1);
            if (stillOpen.Count > 0) detail += " · still open: " + List(stillOpen);
            if (failed.Count > 0) detail += " · couldn’t start " + string.Join(", ", failed);
            bool clean = stillOpen.Count == 0 && failed.Count == 0;
            return new Outcome(Glyphs.Reset, "Reset", detail, clean ? Tone.On : Tone.Error);
        }

        public static void Launch(CoreApp c)
        {
            string target = Environment.ExpandEnvironmentVariables(c.Target.Trim().Trim('"'));
            if (c.Process.Equals("obs64", StringComparison.OrdinalIgnoreCase))
            {
                // A leftover crash marker makes OBS sit on its safe-mode prompt without starting the replay buffer.
                string sentinel = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio", ".sentinel");
                if (Directory.Exists(sentinel))
                    foreach (var f in Directory.GetFiles(sentinel, "run_*")) { try { File.Delete(f); } catch { } }
            }
            if (Path.GetFileName(target).Equals("schtasks.exe", StringComparison.OrdinalIgnoreCase))
            {
                RunHidden(target, c.Args ?? "", 10000);
                return;
            }
            var psi = new ProcessStartInfo(target) { UseShellExecute = true, Arguments = c.Args ?? "" };
            if (File.Exists(target)) psi.WorkingDirectory = Path.GetDirectoryName(target);
            Process.Start(psi)?.Dispose();
        }

        static void RunHidden(string exe, string args, int timeoutMs)
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true });
            p?.WaitForExit(timeoutMs);
        }

        static bool Running(string name)
        {
            var ps = Process.GetProcessesByName((name ?? "").Trim());
            foreach (var p in ps) p.Dispose();
            return ps.Length > 0;
        }

        static bool ComfyRunning()
        {
            foreach (var p in Process.GetProcessesByName("python"))
            {
                using (p)
                {
                    try { if (p.MainModule.FileName.Contains(ComfyPython, StringComparison.OrdinalIgnoreCase)) return true; }
                    catch { }
                }
            }
            return false;
        }

        // ---------------- windows ----------------

        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);

        /// <summary>Top-level app windows a person would see in Alt+Tab.</summary>
        public static List<AppWindow> Windows()
        {
            var list = new List<AppWindow>();
            var names = new Dictionary<int, string>();
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h) || GetWindow(h, 4) != IntPtr.Zero) return true; // GW_OWNER: skip dialogs/owned
                if ((GetWindowLong(h, -20) & 0x80) != 0) return true;                  // WS_EX_TOOLWINDOW
                if (DwmGetWindowAttribute(h, 14, out int cloaked, 4) == 0 && cloaked != 0) return true; // hidden UWP frames
                var sb = new StringBuilder(256);
                if (GetWindowText(h, sb, sb.Capacity) == 0) return true;
                GetWindowThreadProcessId(h, out int pid);
                if (!names.TryGetValue(pid, out var name))
                {
                    try { using var p = Process.GetProcessById(pid); name = p.ProcessName; } catch { name = null; }
                    names[pid] = name;
                }
                if (name != null) list.Add(new AppWindow(h, pid, name, sb.ToString()));
                return true;
            }, IntPtr.Zero);
            return list;
        }
    }
}
