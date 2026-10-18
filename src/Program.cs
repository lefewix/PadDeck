using System;
using System.IO;
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

        [STAThread]
        public static int Main(string[] args)
        {
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

            if (args.Length >= 2 && args[0] == "--obs-ping")
            {
                File.WriteAllText(args[1], Core.Obs.Ping());
                return 0;
            }

            if (args.Length >= 1 && args[0] == "--apply-windows")
            {
                // Places the open windows per the saved rules, once, without starting the hook or UI.
                var cfg = AppSettings.Load();
                using var placer = new Core.WindowPlacer(() => cfg.Windows, System.Windows.Threading.Dispatcher.CurrentDispatcher);
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
                var w3 = new Ui.MainWindow(startHidden: true, preview: true); // the user's real settings
                w3.SelectButton(0);
                w3.RenderSnapshot(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1])), Path.GetFileNameWithoutExtension(args[1]) + "-real.png"), 1060, 640);
                var pop = Ui.PopupWindow.Build(new Outcome(Glyphs.MicOff, "Mic muted", "Microphone (HyperX QuadCast S)", Tone.Off));
                Ui.Snapshot.Save(new System.Windows.Controls.Border { Background = Ui.Theme.Bg, Child = pop, Width = 420, Height = 120 }, 420, 120,
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
