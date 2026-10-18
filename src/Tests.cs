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
            s.Buttons[1] = new ButtonConfig { Kind = ActionKind.OutputDevice, DeviceIds = new List<string> { "x", "y" } };
            s.Buttons[4] = new ButtonConfig { Kind = ActionKind.Keys, Keys = "Ctrl+Shift+M", Label = "Mute" };
            string json = JsonSerializer.Serialize(s, AppSettings.Opts);
            Check("kinds saved as names", json.Contains("\"OutputDevice\""));
            var r = JsonSerializer.Deserialize<AppSettings>(json, AppSettings.Opts);
            r.Normalize();
            Eq("round-trip keys", r.Buttons[4].Keys, "Ctrl+Shift+M");
            Eq("round-trip devices", string.Join(",", r.Buttons[1].DeviceIds), "x,y");
            var old = JsonSerializer.Deserialize<AppSettings>("{\"Buttons\":[{\"Kind\":\"Open\"}]}", AppSettings.Opts);
            old.Normalize();
            Eq("short list padded to six", old.Buttons.Count, 6);
            Check("missing device list filled", old.Buttons[0].DeviceIds != null);
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

        static void CycleTests()
        {
            var ids = new List<string> { "a", "b", "c" };
            Eq("cycle a→b", AudioCtl.NextInCycle(ids, "a"), "b");
            Eq("cycle wraps c→a", AudioCtl.NextInCycle(ids, "c"), "a");
            Eq("unknown current → first", AudioCtl.NextInCycle(ids, "zzz"), "a");
            Eq("empty list → null", AudioCtl.NextInCycle(new List<string>(), "a"), null);
        }
    }
}
