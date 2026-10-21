using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PadDeck.Core;

namespace PadDeck.Tests
{
    /// <summary>Checks that need no hardware. Run: PadDeck.exe --selftest report.txt</summary>
    static class SelfTest
    {
        static readonly List<string> log = new List<string>();
        static int fails, passes;

        public static int Run(string reportPath)
        {
            try
            {
                ParseTests();
                StrokeTests();
                PacketTests();
                GateTests();
                SettingsTests();
                LabelTests();
                CycleTests();
                ResetTests();
                WindowTests();
                ObsTests();
                SettingsFileTests();
                ToggleTests();
                GameWatchTests();
                HookTests();
            }
            catch (Exception ex)
            {
                log.Add("EXCEPTION " + ex);
                fails++;
            }
            log.Add("");
            log.Add($"{passes} passed, {fails} failed");
            File.WriteAllLines(reportPath, log);
            return fails == 0 ? 0 : 1;
        }

        static void Check(string name, bool ok, string detail = "")
        {
            log.Add($"{(ok ? "PASS" : "FAIL")}  {name,-52} {detail}");
            if (ok) passes++; else fails++;
        }

        static void Eq<T>(string name, T got, T want) => Check(name, Equals(got, want), $"got {got}, want {want}");

        static string Hex(IEnumerable<ushort> v) => string.Join(" ", v.Select(x => x.ToString("X2")));

        static void Throws(string name, Action a)
        {
            try { a(); Check(name, false, "no exception"); }
            catch (FormatException ex) { Check(name, true, ex.Message); }
        }

        static void ParseTests()
        {
            Eq("Ctrl+Shift+M", Hex(KeyCombo.Parse("Ctrl+Shift+M")[0]), "A2 A0 4D");
            Eq("modifiers sorted first", Hex(KeyCombo.Parse("m+shift+ctrl")[0]), "A2 A0 4D");
            Eq("digit key", Hex(KeyCombo.Parse("Alt+1")[0]), "A4 31");
            Eq("F-key", Hex(KeyCombo.Parse("F24")[0]), "87");
            Eq("media alias", Hex(KeyCombo.Parse("PlayPause")[0]), "B3");
            Eq("WinForms enum fallback", Hex(KeyCombo.Parse("MediaNextTrack")[0]), "B0");
            Eq("numpad", Hex(KeyCombo.Parse("Num5")[0]), "65");
            var seq = KeyCombo.Parse("Ctrl+C, Ctrl+V");
            Eq("sequence count", seq.Count, 2);
            Eq("sequence round-trip", KeyCombo.Format(seq), "Ctrl+C, Ctrl+V");
            Eq("format names", KeyCombo.Format(KeyCombo.Parse("win+shift+s")[0]), "Win+Shift+S");
            Eq("empty is no keys", KeyCombo.Parse("  ").Count, 0);
            Eq("right ctrl normalises", KeyCombo.NormalizeVk(0xA3), KeyCombo.Ctrl);
            Throws("unknown key rejected", () => KeyCombo.Parse("Ctrl+Banana"));
            Throws("two main keys rejected", () => KeyCombo.Parse("Ctrl+A+B"));
            Throws("dangling plus rejected", () => KeyCombo.Parse("Ctrl+"));
        }

        static void StrokeTests()
        {
            var s = KeyCombo.Strokes(KeyCombo.Parse("Ctrl+Shift+M")[0]);
            Eq("strokes: downs then reverse ups", string.Join(" ", s.Select(x => (x.up ? "^" : "v") + x.vk.ToString("X2"))), "vA2 vA0 v4D ^4D ^A0 ^A2");
            Check("arrows are extended", KeyCombo.IsExtended(0x25));
            Check("media keys are extended", KeyCombo.IsExtended(0xB3));
            Check("letters are not extended", !KeyCombo.IsExtended(0x41));
            var t = KeyCombo.TextStrokes("hi\r\n");
            Eq("text: 3 chars → 6 strokes (CR dropped)", t.Count, 6);
            Eq("text: newline becomes Enter", t[4].vk, (ushort)0x0D);
            Eq("text: letters are unicode", t[0].vk, (ushort)0);
        }

        static string Bytes(byte[] m)
        {
            int end = Array.FindLastIndex(m, b => b != 0) + 1;
            return string.Join(" ", m.Take(end).Select(b => b.ToString("x2")));
        }

        static void PacketTests()
        {
            // Byte layouts match kriomant/ch57x-keyboard-tool's k884x tests.
            var k = Pad.BindKey(1, Pad.F19Usage).ToList();
            Eq("bind F19 packet", Bytes(k[0]), "03 fe 01 01 01 00 00 00 00 00 01 00 6e");
            Eq("finish 1", Bytes(k[1]), "03 aa aa");
            Eq("finish 2", Bytes(k[2]), "03 fd fe ff");
            Eq("finish 3", Bytes(k[3]), "03 aa aa");
            var m = Pad.BindMedia(2, Pad.MediaVolUp).First();
            Eq("media packet (vol up on button 2)", Bytes(m), "03 fe 02 01 02 00 00 00 00 00 00 e9");
            var l = Pad.Layout();
            Eq("layout: 9 bindings × 4", l.Count, 9 * 4);
            Check("all packets 64 bytes", l.All(p => p.Length == 64));
            Check("all start with report id 3", l.All(p => p[0] == 0x03));
            Eq("button 6 → F24", Bytes(l[5 * 4]), "03 fe 06 01 01 00 00 00 00 00 01 00 73");
            Eq("knob ccw → vol down", Bytes(l[6 * 4]), "03 fe 10 01 02 00 00 00 00 00 00 ea");
            Eq("knob press → play", Bytes(l[7 * 4]), "03 fe 11 01 02 00 00 00 00 00 00 cd");
            Eq("knob cw → vol up", Bytes(l[8 * 4]), "03 fe 12 01 02 00 00 00 00 00 00 e9");
            Eq("hook range is F19..F24", $"{KeyHook.FirstVk:X2}-{KeyHook.LastVk:X2}", "82-87");
        }

        static void GateTests()
        {
            var g = new KeyGate();
            Check("first down fires", g.Press(2, true));
            Check("auto-repeat ignored", !g.Press(2, true));
            Check("other key independent", g.Press(3, true));
            Check("up doesn't fire", !g.Press(2, false));
            Check("next press fires again", g.Press(2, true));
        }

        static void SettingsTests()
        {
            var s = new AppSettings();
            s.Normalize();
            Eq("six buttons", s.Buttons.Count, 6);
            s.Buttons[1] = new ButtonConfig { Kind = ActionKind.OutputDevice, Devices = { new DeviceRef { Id = "x", Name = "X" }, new DeviceRef { Id = "y", Name = "Y" } } };
            s.Buttons[4] = new ButtonConfig { Kind = ActionKind.Keys, Keys = "Ctrl+Shift+M", Label = "Mute" };
            string json = JsonSerializer.Serialize(s, AppSettings.Opts);
            Check("kinds saved as names", json.Contains("\"OutputDevice\""));
            var r = JsonSerializer.Deserialize<AppSettings>(json, AppSettings.Opts);
            r.Normalize();
            Eq("round-trip keys", r.Buttons[4].Keys, "Ctrl+Shift+M");
            Eq("round-trip devices", string.Join(",", r.Buttons[1].Devices.Select(d => d.Id + "=" + d.Name)), "x=X,y=Y");
            var old = JsonSerializer.Deserialize<AppSettings>("{\"Buttons\":[{\"Kind\":\"Open\"}]}", AppSettings.Opts);
            old.Normalize();
            Eq("short list padded to six", old.Buttons.Count, 6);
            Check("missing device list filled", old.Buttons[0].Devices != null);
        }

        static void LabelTests()
        {
            Eq("label from exe", ActionRunner.AutoLabel(new ButtonConfig { Kind = ActionKind.Open, Target = @"C:\Apps\Discord.exe" }), "Discord");
            Eq("label from URL", ActionRunner.AutoLabel(new ButtonConfig { Kind = ActionKind.Open, Target = "https://www.twitch.tv/x" }), "twitch.tv");
            Eq("label from folder", ActionRunner.AutoLabel(new ButtonConfig { Kind = ActionKind.Open, Target = @"C:\claude-tools\" }), "claude-tools");
            Eq("label from keys", ActionRunner.AutoLabel(new ButtonConfig { Kind = ActionKind.Keys, Keys = "Ctrl+M" }), "Ctrl+M");
            Eq("long text trimmed", ActionRunner.AutoLabel(new ButtonConfig { Kind = ActionKind.Text, Text = "good game everyone, well played\n" }), "good game everyone…");
            Eq("user label wins", ActionRunner.Title(new ButtonConfig { Kind = ActionKind.Keys, Keys = "Ctrl+M", Label = " Mute " }), "Mute");
            var o = ActionRunner.Run(new ButtonConfig { Kind = ActionKind.Open });
            Check("empty open reports an error", o.Tone == Tone.Error, o.Detail);
            var k = ActionRunner.Run(new ButtonConfig { Kind = ActionKind.Keys, Keys = "Ctrl+Nope" });
            Check("bad keys report an error", k.Tone == Tone.Error, k.Detail);
        }

        static void ResetTests()
        {
            var w = new List<Reset.AppWindow>
            {
                new Reset.AppWindow(new IntPtr(1), 1, "Discord", "Discord"),
                new Reset.AppWindow(new IntPtr(2), 2, "thorium", "YouTube"),
                new Reset.AppWindow(new IntPtr(3), 3, "WindowsTerminal", "Claude Code"),
                new Reset.AppWindow(new IntPtr(4), 4, "explorer", "Downloads"),
                new Reset.AppWindow(new IntPtr(5), 5, "notepad", "notes.txt"),
                new Reset.AppWindow(new IntPtr(6), 6, "Spotify", "Spotify"),
                new Reset.AppWindow(new IntPtr(7), 7, "PadDeck", "PadDeck"),
                new Reset.AppWindow(new IntPtr(8), 8, "steamwebhelper", "Steam"),
            };
            var close = Reset.ToClose(w, Reset.DefaultCore());
            Eq("reset closes only non-core windows", string.Join(",", close.Select(x => x.Process)), "notepad,Spotify,steamwebhelper");
            Check("terminal (Claude Code) is never closed", !close.Any(x => x.Process == "WindowsTerminal"));
            var core = Reset.DefaultCore();
            Eq("default core apps", string.Join(",", core.Select(c => c.Process)), "obs64,Discord,FanControl,thorium");
            Check("OBS starts visible with the replay buffer", core[0].Target.EndsWith("obs64.exe") && core[0].Args == "--startreplaybuffer");
            Check("Fan Control starts via its elevated task", core[2].Target == "schtasks.exe" && core[2].Args.Contains("FanControl"));
            var p = new Reset.Plan { Llm = true, Close = close, Start = { core[0] } };
            Eq("plan description", Reset.Describe(p), "Stop the LLM, close notepad, Spotify, steamwebhelper, start OBS Studio");
            var r = new Reset.Plan { Restart = { core[0], core[1] } };
            Eq("restart description", Reset.Describe(r), "Restart OBS Studio, Discord");
            Check("only OBS restarts by default", core[0].Restart && !core[1].Restart && !core[2].Restart && !core[3].Restart);
            Eq("Fan Control restarts via its task", Reset.EndTaskArgs(core[2]), "/end /tn FanControl");
            Eq("non-task apps have no end-task command", Reset.EndTaskArgs(core[0]), null);
            Eq("empty plan", Reset.Describe(new Reset.Plan()), "Nothing to do: only core apps are open");
            var cfg = JsonSerializer.Deserialize<AppSettings>("{\"Buttons\":[{\"Kind\":\"Reset\"}]}", AppSettings.Opts);
            cfg.Normalize();
            Check("reset kind loads, GPU stop defaults on", cfg.Buttons[0].Kind == ActionKind.Reset && cfg.Buttons[0].StopGpu && cfg.Buttons[0].Core != null);
            Eq("OBS live: not restarted", Reset.ObsBusy(new ObsState(true, false, true)), "live");
            Eq("OBS recording: not restarted", Reset.ObsBusy(new ObsState(false, true, true)), "recording");
            Eq("OBS only buffering: restart is fine", Reset.ObsBusy(new ObsState(false, false, true)), null);
            Eq("OBS not answering (hung): restart is fine", Reset.ObsBusy(null), null);
            Eq("skipped restart is described", Reset.Describe(new Reset.Plan { Close = close, Skip = { "OBS Studio (live)" } }),
                "Close notepad, Spotify, steamwebhelper, leave OBS Studio (live) running");
        }

        static void WindowTests()
        {
            // This PC: GS27QA on the left at x -2560, X27U Z1 (primary) at 0.
            var left = new PxRect { L = -2560, T = 0, R = 0, B = 1440 };
            var main = new PxRect { L = 0, T = 0, R = 2560, B = 1440 };
            var mons = new List<(PxRect, bool)> { (main, true), (left, false) };
            Eq("left monitor is the one at x -2560", WindowPlacer.PickMonitor(mons, MonitorPick.Left), 1);
            Eq("right monitor", WindowPlacer.PickMonitor(mons, MonitorPick.Right), 0);
            Eq("main monitor", WindowPlacer.PickMonitor(mons, MonitorPick.Main), 0);
            Eq("OBS: left half of left monitor", WindowPlacer.ZoneRect(left, Zone.LeftHalf).ToString(), "(-2560,0)-(-1280,1440)");
            Eq("Discord: right half of left monitor", WindowPlacer.ZoneRect(left, Zone.RightHalf).ToString(), "(-1280,0)-(0,1440)");
            Eq("bottom half", WindowPlacer.ZoneRect(main, Zone.BottomHalf).ToString(), "(0,720)-(2560,1440)");
            // Window rect is 7 px wider than the visible frame on the left, right and bottom (standard Win11 borders).
            var win = new PxRect { L = 93, T = 100, R = 1007, B = 807 };
            var vis = new PxRect { L = 100, T = 100, R = 1000, B = 800 };
            Eq("invisible borders added back", WindowPlacer.WithBorders(WindowPlacer.ZoneRect(left, Zone.LeftHalf), win, vis).ToString(), "(-2567,0)-(-1273,1447)");
            var rule = JsonSerializer.Deserialize<AppSettings>("{\"Windows\":[{\"Process\":\"obs64\",\"Monitor\":\"Left\",\"Zone\":\"LeftHalf\"}]}", AppSettings.Opts);
            Check("window rules load by name", rule.Windows[0].Monitor == MonitorPick.Left && rule.Windows[0].Zone == Zone.LeftHalf);
        }

        static void ObsTests()
        {
            // Example from the obs-websocket 5 protocol docs.
            Eq("websocket auth string", Obs.AuthString("supersecretpassword", "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=", "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY="),
                "1Ct943GAT+6YQUUX47Ia/ncufilbe6+oD6lY+5kaCu4=");
            Check("glyph icon spec parses", PadDeck.Ui.Icons.IsGlyph("glyph:E8F2", out var gl) && gl == "\uE8F2");
            Check("file icon spec is an image", PadDeck.Ui.Icons.IsImage(@"C:\x\claude.exe") && !PadDeck.Ui.Icons.IsImage("glyph:E8F2") && !PadDeck.Ui.Icons.IsImage(""));
            Check("claude.exe icon loads", PadDeck.Ui.Icons.Load(System.Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\.local\bin\claude.exe")) != null);
            Check("ComfyUI favicon loads", PadDeck.Ui.Icons.Load(@"C:\claude-tools\ComfyUI\_app\python_embeded\Lib\site-packages\comfyui_frontend_package\static\assets\favicon.ico") != null);
            Check("OBS shortcut icon loads", PadDeck.Ui.Icons.Load(@"%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\OBS Studio (replay buffer).lnk") != null);
            Check("line logos parse", PadDeck.Ui.Icons.IsLine("line:claude", out _) && PadDeck.Ui.Icons.IsLine("line:comfyui", out _) && PadDeck.Ui.Icons.IsLine("line:OBS", out _));
            Check("unknown line logo is rejected", !PadDeck.Ui.Icons.IsLine("line:nope", out _) && !PadDeck.Ui.Icons.IsImage("line:claude"));
            Check("missing icon file is null, not a crash", PadDeck.Ui.Icons.Load(@"C:\nope\missing.png") == null);
            Eq("label for save replay", ActionRunner.AutoLabel(new ButtonConfig { Kind = ActionKind.Obs, ObsAction = ObsCommand.SaveReplay }), "Save replay");
        }

        static void SettingsFileTests()
        {
            string dir = Path.Combine(Path.GetTempPath(), "paddeck-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string file = Path.Combine(dir, "settings.json");
                var a = new AppSettings();
                a.Normalize();
                a.Buttons[0].Label = "first";
                a.SaveTo(file);
                a.Buttons[0].Label = "second";
                a.SaveTo(file);
                Check("save keeps the previous file as .bak", File.Exists(file + ".bak") && File.ReadAllText(file + ".bak").Contains("first"));
                var ok = AppSettings.LoadFrom(file, out var p0);
                Check("good file loads with no problem", ok.Buttons[0].Label == "second" && p0 == null);
                File.WriteAllText(file, "{ \"Buttons\": [ { \"Lab");
                var fromBak = AppSettings.LoadFrom(file, out var p1);
                Check("damaged file falls back to the backup", fromBak.Buttons[0].Label == "first" && p1 != null && p1.StartsWith("Loaded the backup"), p1);
                Check("damaged file is copied aside", Directory.GetFiles(dir, "settings.broken-*.json").Length == 1);
                File.Delete(file + ".bak");
                var none = AppSettings.LoadFrom(file, out var p2);
                Check("no backup: defaults and a warning", none.Buttons.Count == AppSettings.ButtonCount && p2 != null && p2.StartsWith("Started with defaults"), p2);
                var fresh = AppSettings.LoadFrom(Path.Combine(dir, "missing.json"), out var p3);
                Check("first run (no file) is not a problem", fresh.Buttons.Count == AppSettings.ButtonCount && p3 == null);
                var json = JsonSerializer.Serialize(new ButtonConfig { Kind = ActionKind.Open, RunningProcess = "x", StopTarget = "y" }, AppSettings.Opts);
                Check("computed flags aren't saved", !json.Contains("IsToggle") && !json.Contains("HasHold"));
                var old = JsonSerializer.Deserialize<AppSettings>("{\"Buttons\":[{\"Kind\":\"Open\",\"Target\":\"a.bat\"}]}", AppSettings.Opts);
                old.Normalize();
                Check("old settings load: not a switch, no hold, warnings on", !old.Buttons[0].IsToggle && !old.Buttons[0].HasHold && old.GameWarnings);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        static void ToggleTests()
        {
            Eq("switch spec: name only", Toggle.ParseSpec("llama-server").ToString(), "(llama-server, )");
            Eq("switch spec: .exe dropped", Toggle.ParseSpec(" llama-server.exe ").name, "llama-server");
            Eq("switch spec: name in folder", Toggle.ParseSpec(@"python in C:\claude-tools\ComfyUI\").ToString(), @"(python, C:\claude-tools\ComfyUI)");
            Check("ComfyUI's python is in its folder", Toggle.InFolder(@"C:\claude-tools\ComfyUI\_app\python_embeded\python.exe", @"C:\claude-tools\ComfyUI"));
            Check("other pythons aren't", !Toggle.InFolder(@"C:\Users\me\AppData\Local\Programs\Python\Python312\python.exe", @"C:\claude-tools\ComfyUI"));
            Check("a sibling folder with the same prefix isn't", !Toggle.InFolder(@"C:\claude-tools\ComfyUI-old\python.exe", @"C:\claude-tools\ComfyUI"));
            Check("switch needs both boxes", new ButtonConfig { Kind = ActionKind.Open, RunningProcess = "x", StopTarget = "y" }.IsToggle
                && !new ButtonConfig { Kind = ActionKind.Open, RunningProcess = "x" }.IsToggle
                && !new ButtonConfig { Kind = ActionKind.Keys, RunningProcess = "x", StopTarget = "y" }.IsToggle);
            Check("nothing running under a made-up name", !Toggle.IsOn("paddeck-no-such-process"));
            var stop = Toggle.Stop(new ButtonConfig { Kind = ActionKind.Open, Label = "X", RunningProcess = "paddeck-no-such-process", StopTarget = @"C:\nope\Stop.bat" });
            Check("missing stop script is an error, not a crash", stop.Tone == Tone.Error, stop.Detail);
            var hold = ActionRunner.RunHold(new ButtonConfig { HoldTarget = @"C:\nope\missing folder" });
            Check("missing hold target is an error, not a crash", hold.Tone == Tone.Error, hold.Detail);
        }

        static void GameWatchTests()
        {
            var mon = new PxRect { L = 0, T = 0, R = 2560, B = 1440 };
            Check("borderless game is fullscreen", GameWatch.IsFullscreen(mon, mon, 0x16000000));
            Check("exclusive-style bigger window counts", GameWatch.IsFullscreen(new PxRect { L = -1, T = -1, R = 2561, B = 1441 }, mon, 0));
            Check("maximized app with a title bar isn't", !GameWatch.IsFullscreen(new PxRect { L = -8, T = -8, R = 2568, B = 1448 }, mon, 0x16CF0000));
            Check("small borderless window isn't", !GameWatch.IsFullscreen(new PxRect { L = 0, T = 0, R = 1280, B = 720 }, mon, 0x16000000));
            Check("games count", GameWatch.IsGame("r5apex_dx12") && GameWatch.IsGame("VALORANT-Win64-Shipping"));
            Check("browser, player and shell don't", !GameWatch.IsGame("thorium") && !GameWatch.IsGame("PotPlayerMini64") && !GameWatch.IsGame("explorer") && !GameWatch.IsGame(""));
            Eq("all fine: no warning", GameWatch.Lines(new List<(string, int)>(), true, 4).Count, 0);
            Eq("OBS not answering: no nag", GameWatch.Lines(new List<(string, int)>(), null, 4).Count, 0);
            Eq("no replay buffer", string.Join(" | ", GameWatch.Lines(new List<(string, int)>(), false, 4)), "Replay buffer is off · press F23 to turn it on");
            Eq("ComfyUI on (it closed OBS)", string.Join(" | ", GameWatch.Lines(new List<(string, int)> { ("ComfyUI", 2) }, false, 4)),
                "ComfyUI is on · press F21 twice to turn it off | Replay buffer is off");
            Eq("no save-replay button", string.Join(" | ", GameWatch.Lines(new List<(string, int)>(), false, -1)), "Replay buffer is off");
        }

        static void HookTests()
        {
            using (var h = new KeyHook()) { }
            Check("key hook installs on its own thread and unhooks", true);
        }

        static void CycleTests()
        {
            var ids = new List<string> { "a", "b", "c" };
            Eq("cycle a→b", AudioCtl.NextInCycle(ids, "a"), "b");
            Eq("cycle wraps c→a", AudioCtl.NextInCycle(ids, "c"), "a");
            Eq("unknown current → first", AudioCtl.NextInCycle(ids, "zzz"), "a");
            Eq("empty list → null", AudioCtl.NextInCycle(new List<string>(), "a"), null);

            // The real devices from this PC on 2026-10-17: SoundSwitch's saved list vs what's connected.
            var active = new List<AudioDevice>
            {
                new AudioDevice("{realtek}", "Speakers (Realtek(R) Audio)"),
                new AudioDevice("{ka13-new}", "DC Headphones (2- FIIO KA13)"),
                new AudioDevice("{rt-digital}", "Realtek Digital Output (Realtek(R) Audio)"),
            };
            var saved = new List<DeviceRef>
            {
                new DeviceRef { Id = "{ka13-old}", Name = "Headphones (FIIO KA13)" },
                new DeviceRef { Id = "{hda}", Name = "Speakers (High Definition Audio Device)" },
                new DeviceRef { Id = "{realtek}", Name = "Speakers (Realtek(R) Audio)" },
            };
            Eq("adapter strips 2- prefix", AudioCtl.Adapter("DC Headphones (2- FIIO KA13)"), "FIIO KA13");
            Eq("adapter keeps nested parens", AudioCtl.Adapter("Speakers (Realtek(R) Audio)"), "Realtek(R) Audio");
            Eq("resolve: KA13 by adapter, HDA skipped, Realtek by id", string.Join(",", AudioCtl.Resolve(saved, active).Select(d => d.Id)), "{ka13-new},{realtek}");
            Eq("resolve: ambiguous adapter is not guessed", AudioCtl.Resolve(new[] { new DeviceRef { Id = "?", Name = "Line Out (Realtek(R) Audio)" } }, active).Count, 0);
            Eq("resolve: exact name beats adapter", AudioCtl.Resolve(new[] { new DeviceRef { Id = "?", Name = "Realtek Digital Output (Realtek(R) Audio)" } }, active).Single().Id, "{rt-digital}");
            Eq("headphones glyph", Glyphs.ForDevice("DC Headphones (2- FIIO KA13)", false), Glyphs.Headphones);
            Eq("speaker glyph", Glyphs.ForDevice("Speakers (Realtek(R) Audio)", false), Glyphs.Speaker);
        }
    }
}
