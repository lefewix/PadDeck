using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using PadDeck.Core;

namespace PadDeck
{
    public static class Program
    {
        static Application NewApp()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/PadDeck;component/src/Theme.xaml")
            });
            return app;
        }

        [DllImport("userenv.dll", SetLastError = true)] static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
        [DllImport("userenv.dll")] static extern bool DestroyEnvironmentBlock(IntPtr env);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        /// <summary>
        /// Swaps this process's environment for a fresh sign-in environment (what Explorer gives apps), so whatever
        /// PadDeck launches never inherits variables from whoever started it. Started from a Claude Code shell, it
        /// would otherwise pass on NO_COLOR, CLAUDECODE, GIT_EDITOR=true and the like.
        /// </summary>
        public static void CleanEnvironment()
        {
            if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle, 0x000A, out IntPtr token)) return; // QUERY | DUPLICATE
            try
            {
                if (!CreateEnvironmentBlock(out IntPtr block, token, false)) return;
                try
                {
                    var fresh = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (IntPtr p = block; ;)
                    {
                        string entry = Marshal.PtrToStringUni(p);
                        if (string.IsNullOrEmpty(entry)) break;
                        int eq = entry.IndexOf('=', 1); // entries like "=C:=C:\" start with '='
                        if (eq > 0) fresh[entry.Substring(0, eq)] = entry.Substring(eq + 1);
                        p += (entry.Length + 1) * 2;
                    }
                    if (fresh.Count == 0) return;
                    foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
                        if (!fresh.ContainsKey((string)e.Key)) Environment.SetEnvironmentVariable((string)e.Key, null);
                    foreach (var kv in fresh) Environment.SetEnvironmentVariable(kv.Key, kv.Value);
                }
                finally { DestroyEnvironmentBlock(block); }
            }
            finally { CloseHandle(token); }
        }

        [STAThread]
        public static int Main(string[] args)
        {
            try { CleanEnvironment(); } catch { }
            if (args.Length >= 2 && args[0] == "--selftest") return Tests.SelfTest.Run(args[1]);

            if (args.Length >= 2 && args[0] == "--program")
            {
                // Programs the pad without the UI; writes "ok" or the error to the given file.
                string err = Pad.Program();
                if (err == null)
                {
                    var cfg = AppSettings.Load();
                    cfg.PadProgrammed = true;
                    cfg.Save();
                }
                File.WriteAllText(args[1], err ?? "ok");
                return err == null ? 0 : 1;
            }

            if (args.Length >= 2 && args[0] == "--env")
            {
                // Writes the environment PadDeck hands to the apps it launches (after cleaning), for checking.
                var lines = new System.Collections.Generic.List<string>();
                foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables()) lines.Add(e.Key + "=" + e.Value);
                lines.Sort(StringComparer.OrdinalIgnoreCase);
                File.WriteAllLines(args[1], lines);
                return 0;
            }

            if (args.Length >= 2 && args[0] == "--obs-ping")
            {
                File.WriteAllText(args[1], Core.Obs.Ping());
                return 0;
            }

            if (args.Length >= 1 && args[0] == "--apply-windows")
            {
                // Places the open windows per the saved rules, once, without starting the hook or UI.
                var cfg = AppSettings.Load();
                using var placer = new Core.WindowPlacer(() => cfg.Windows);
                int n = placer.ApplyAll();
                if (args.Length >= 2) File.WriteAllText(args[1], $"placed {n} window(s)");
                return 0;
            }

            if (args.Length >= 2 && args[0] == "--reset-plan")
            {
                // Read-only: lists the windows a Reset would close and what it would start. Closes nothing.
                var b = new ButtonConfig { Kind = ActionKind.Reset, Core = Core.Reset.DefaultCore() };
                var saved = AppSettings.Load().Buttons.Find(x => x.Kind == ActionKind.Reset);
                if (saved != null) b = saved;
                var plan = Core.Reset.MakePlan(b);
                var lines = new System.Collections.Generic.List<string> { "All app windows:" };
                foreach (var w in Core.Reset.Windows()) lines.Add($"  {w.Process,-22} {w.Title}");
                lines.Add("Would close:");
                foreach (var w in plan.Close) lines.Add($"  {w.Process,-22} {w.Title}");
                lines.Add("Would start: " + string.Join(", ", plan.Start.ConvertAll(c => c.Name)));
                lines.Add($"Stop LLM: {plan.Llm}, stop ComfyUI: {plan.Comfy}");
                lines.Add("Popup text: " + Core.Reset.Describe(plan));
                File.WriteAllLines(args[1], lines);
                return 0;
            }

            if (args.Length >= 2 && args[0] == "--snapshot")
            {
                // Renders the UI (and a sample popup) to PNGs without ever showing a window.
                NewApp();
                var w = new Ui.MainWindow(startHidden: true, preview: true);
                w.LoadSample();
                w.RenderSnapshot(args[1], 1060, 640);
                var w2 = new Ui.MainWindow(startHidden: true, preview: true);
                w2.LoadSample(1);
                w2.RenderSnapshot(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1])), Path.GetFileNameWithoutExtension(args[1]) + "-switch.png"), 1060, 640);
                foreach (int real in new[] { 0, 1, 4 }) // the user's real settings: Reset, an on/off switch, a hold action
                {
                    var w3 = new Ui.MainWindow(startHidden: true, preview: true);
                    w3.SelectButton(real);
                    w3.RenderSnapshot(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1])), Path.GetFileNameWithoutExtension(args[1]) + (real == 0 ? "-real.png" : $"-real{real + 1}.png")), 1060, 900);
                }
                // Line icons next to Segoe symbols, at tile size and enlarged, to compare stroke weight and size.
                var cmp = new System.Windows.Controls.StackPanel { Margin = new Thickness(20) };
                foreach (double sz in new[] { 30.0, 110.0 })
                {
                    var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
                    foreach (var spec in new[] { "glyph:E8F2", "glyph:E72C", "glyph:E7F6", "line:claude", "line:comfyui", "line:obs" })
                        row.Children.Add(new System.Windows.Controls.Border { Width = sz * 1.3, Height = sz * 1.3, Child = Ui.Icons.Draw(spec, "?", sz, Ui.Theme.Accent) });
                    cmp.Children.Add(row);
                }
                Ui.Snapshot.Save(new System.Windows.Controls.Border { Background = Ui.Theme.Panel2, Child = cmp, Width = 940, Height = 260 }, 940, 260,
                    Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1])), Path.GetFileNameWithoutExtension(args[1]) + "-lines.png"));
                var w4 = new Ui.MainWindow(startHidden: true, preview: true); // icon picker with the symbol grid open
                w4.SelectButton(1, symbols: true);
                w4.RenderSnapshot(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1])), Path.GetFileNameWithoutExtension(args[1]) + "-icons.png"), 1060, 640);
                var pops = new System.Windows.Controls.StackPanel();
                pops.Children.Add(Ui.PopupWindow.Build(new Outcome(Glyphs.MicOff, "Mic muted", "Microphone (HyperX QuadCast S)", Tone.Off)));
                pops.Children.Add(Ui.PopupWindow.Build(new Outcome(Glyphs.Replay, "Replay saved", "Replay 2026-10-17 22-41-05.mp4", Tone.On), "line:obs"));
                pops.Children.Add(Ui.PopupWindow.Build(new Outcome(Glyphs.Open, "Claude", "Opened", Tone.Normal), "line:claude"));
                pops.Children.Add(Ui.PopupWindow.Build(new Outcome(Glyphs.Warning, "Heads up", string.Join("\n",
                    GameWatch.Lines(new System.Collections.Generic.List<(string, int)> { ("ComfyUI", 2) }, false, 4)), Tone.Error)));
                pops.Children.Add(Ui.PopupWindow.Build(new Outcome(Glyphs.Power, "ComfyUI is on", "Press again to turn it off", Tone.Normal)));
                pops.Children.Add(Ui.PopupWindow.Build(new Outcome(Glyphs.Folder, "Opened OBS", @"C:\Users\me\Videos\OBS", Tone.Normal)));
                Ui.Snapshot.Save(new System.Windows.Controls.Border { Background = Ui.Theme.Bg, Child = pops, Width = 420, Height = 640 }, 420, 640,
                    Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1])), Path.GetFileNameWithoutExtension(args[1]) + "-popup.png"));
                return 0;
            }

            bool tray = Array.IndexOf(args, "--tray") >= 0;
            using var single = new Mutex(true, @"Local\PadDeck.Single", out bool first);
            if (!first)
            {
                try { EventWaitHandle.OpenExisting(@"Local\PadDeck.Show").Set(); } catch { }
                return 0;
            }
            using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\PadDeck.Show");

            var app = NewApp();
            var win = new Ui.MainWindow(startHidden: tray);

            // A second launch (e.g. from the Start menu) brings this window back instead of starting another hook.
            new Thread(() =>
            {
                try { while (showEvent.WaitOne()) win.Dispatcher.InvokeAsync(win.ShowFromTray); }
                catch { }
            }) { IsBackground = true }.Start();

            app.Run();
            GC.KeepAlive(single);
            return 0;
        }
    }
}
