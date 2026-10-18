using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using WFKeys = System.Windows.Forms.Keys;

namespace PadDeck.Core
{
    /// <summary>Parses and formats key combos like "Ctrl+Shift+M" and sends them with SendInput.</summary>
    public static class KeyCombo
    {
        public const ushort Ctrl = 0xA2, Shift = 0xA0, Alt = 0xA4, Win = 0x5B;

        static readonly Dictionary<string, ushort> Aliases = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["ctrl"] = Ctrl, ["control"] = Ctrl, ["shift"] = Shift, ["alt"] = Alt, ["win"] = Win, ["windows"] = Win,
            ["esc"] = 0x1B, ["escape"] = 0x1B, ["enter"] = 0x0D, ["return"] = 0x0D, ["space"] = 0x20, ["tab"] = 0x09,
            ["backspace"] = 0x08, ["del"] = 0x2E, ["delete"] = 0x2E, ["ins"] = 0x2D, ["insert"] = 0x2D,
            ["home"] = 0x24, ["end"] = 0x23, ["pgup"] = 0x21, ["pageup"] = 0x21, ["pgdn"] = 0x22, ["pagedown"] = 0x22,
            ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
            ["prtsc"] = 0x2C, ["printscreen"] = 0x2C, ["capslock"] = 0x14, ["numlock"] = 0x90, ["scrolllock"] = 0x91,
            ["pause"] = 0x13, ["menu"] = 0x5D, ["apps"] = 0x5D,
            ["playpause"] = 0xB3, ["play"] = 0xB3, ["nexttrack"] = 0xB0, ["next"] = 0xB0, ["prevtrack"] = 0xB1, ["previous"] = 0xB1,
            ["stop"] = 0xB2, ["mute"] = 0xAD, ["volumemute"] = 0xAD, ["volup"] = 0xAF, ["volumeup"] = 0xAF,
            ["voldown"] = 0xAE, ["volumedown"] = 0xAE,
            ["plus"] = 0xBB, ["minus"] = 0xBD, ["comma"] = 0xBC, ["period"] = 0xBE, ["dot"] = 0xBE,
            ["semicolon"] = 0xBA, ["slash"] = 0xBF, ["backslash"] = 0xDC, ["quote"] = 0xDE, ["grave"] = 0xC0, ["backtick"] = 0xC0,
            ["lbracket"] = 0xDB, ["rbracket"] = 0xDD,
        };

        static readonly Dictionary<ushort, string> Names = new Dictionary<ushort, string>
        {
            [Ctrl] = "Ctrl", [Shift] = "Shift", [Alt] = "Alt", [Win] = "Win",
            [0x1B] = "Esc", [0x0D] = "Enter", [0x20] = "Space", [0x09] = "Tab", [0x08] = "Backspace", [0x2E] = "Delete",
            [0x2D] = "Insert", [0x24] = "Home", [0x23] = "End", [0x21] = "PageUp", [0x22] = "PageDown",
            [0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down", [0x2C] = "PrintScreen", [0x14] = "CapsLock",
            [0x90] = "NumLock", [0x91] = "ScrollLock", [0x13] = "Pause", [0x5D] = "Menu",
            [0xB3] = "PlayPause", [0xB0] = "NextTrack", [0xB1] = "PrevTrack", [0xB2] = "Stop", [0xAD] = "Mute",
            [0xAF] = "VolumeUp", [0xAE] = "VolumeDown",
            [0xBB] = "Plus", [0xBD] = "Minus", [0xBC] = "Comma", [0xBE] = "Period", [0xBA] = "Semicolon", [0xBF] = "Slash",
            [0xDC] = "Backslash", [0xDE] = "Quote", [0xC0] = "Grave", [0xDB] = "LBracket", [0xDD] = "RBracket",
        };

        public static bool IsModifier(ushort vk) => vk == Ctrl || vk == Shift || vk == Alt || vk == Win;

        /// <summary>Normalises generic and right-hand modifier VKs to the left-hand ones used for sending.</summary>
        public static ushort NormalizeVk(int vk) => vk switch
        {
            0x11 or 0xA2 or 0xA3 => Ctrl,
            0x10 or 0xA0 or 0xA1 => Shift,
            0x12 or 0xA4 or 0xA5 => Alt,
            0x5B or 0x5C => Win,
            _ => (ushort)vk,
        };

        static ushort? ParseKey(string tok)
        {
            tok = tok.Trim();
            if (tok.Length == 0) return null;
            if (Aliases.TryGetValue(tok, out var a)) return a;
            if (tok.Length == 1)
            {
                char c = char.ToUpperInvariant(tok[0]);
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return c;
            }
            if ((tok[0] == 'F' || tok[0] == 'f') && int.TryParse(tok.Substring(1), out int f) && f >= 1 && f <= 24)
                return (ushort)(0x70 + f - 1);
            if (tok.StartsWith("num", StringComparison.OrdinalIgnoreCase) && tok.Length == 4 && char.IsDigit(tok[3]))
                return (ushort)(0x60 + tok[3] - '0');
            if (Enum.TryParse(tok, true, out WFKeys k) && !int.TryParse(tok, out _))
            {
                int v = (int)(k & WFKeys.KeyCode);
                if (v > 0 && v < 0xFF) return NormalizeVk(v);
            }
            return null;
        }

        /// <summary>"Ctrl+C, Ctrl+V" → [[Ctrl,C],[Ctrl,V]]. Throws FormatException with a readable message.</summary>
        public static List<List<ushort>> Parse(string s)
        {
            var result = new List<List<ushort>>();
            if (string.IsNullOrWhiteSpace(s)) return result;
            foreach (var part in s.Split(','))
            {
                if (part.Trim().Length == 0) continue;
                var combo = new List<ushort>();
                foreach (var tok in part.Split('+'))
                {
                    var vk = ParseKey(tok) ?? throw new FormatException($"Unknown key “{tok.Trim()}”");
                    if (!combo.Contains(vk)) combo.Add(vk);
                }
                // Modifiers first, in a fixed order, so "M+Ctrl" behaves like "Ctrl+M".
                var mods = new[] { Win, Ctrl, Shift, Alt }.Where(combo.Contains).ToList();
                var keys = combo.Where(v => !IsModifier(v)).ToList();
                if (keys.Count > 1) throw new FormatException($"“{part.Trim()}” has more than one main key. Use commas for a sequence.");
                mods.AddRange(keys);
                result.Add(mods);
            }
            return result;
        }

        public static string KeyName(ushort vk)
        {
            if (Names.TryGetValue(vk, out var n)) return n;
            if ((vk >= 'A' && vk <= 'Z') || (vk >= '0' && vk <= '9')) return ((char)vk).ToString();
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1);
            if (vk >= 0x60 && vk <= 0x69) return "Num" + (vk - 0x60);
            return ((WFKeys)vk).ToString();
        }

        public static string Format(IEnumerable<ushort> combo) => string.Join("+", combo.Select(KeyName));

        public static string Format(List<List<ushort>> seq) => string.Join(", ", seq.Select(c => Format(c)));

        // ---------------- sending ----------------

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public InputUnion u; }

        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);

        const uint KEYUP = 0x2, EXTENDED = 0x1, UNICODE = 0x4;
        /// <summary>Marks our own injected keys so the hook can ignore them.</summary>
        public static readonly IntPtr Tag = new IntPtr(0x50414444); // "PADD"

        public static bool IsExtended(ushort vk) =>
            (vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E || vk == 0x5B || vk == 0x5C || vk == 0x5D ||
            vk == 0x6F || vk == 0x90 || vk == 0xA3 || vk == 0xA5 || vk == 0x2C || (vk >= 0xA6 && vk <= 0xB7);

        static INPUT Key(ushort vk, bool up)
        {
            uint flags = (up ? KEYUP : 0) | (IsExtended(vk) ? EXTENDED : 0);
            return new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKey(vk, 0), dwFlags = flags, extra = Tag } } };
        }

        static INPUT Unit(char c, bool up) =>
            new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = UNICODE | (up ? KEYUP : 0), extra = Tag } } };

        /// <summary>Downs in order, ups in reverse.</summary>
        public static List<(ushort vk, bool up)> Strokes(List<ushort> combo)
        {
            var l = combo.Select(v => (v, false)).ToList();
            for (int i = combo.Count - 1; i >= 0; i--) l.Add((combo[i], true));
            return l;
        }

        static void Send(INPUT[] inputs)
        {
            if (inputs.Length == 0) return;
            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            if (sent != inputs.Length) throw new InvalidOperationException("Windows blocked the keystrokes (is an admin window in front?)");
        }

        public static void SendKey(ushort vk, bool up) => Send(new[] { Key(vk, up) });

        public static void SendSequence(List<List<ushort>> seq)
        {
            for (int i = 0; i < seq.Count; i++)
            {
                if (i > 0) Thread.Sleep(20);
                Send(Strokes(seq[i]).Select(s => Key(s.vk, s.up)).ToArray());
            }
        }

        /// <summary>Per-character input for typing text: newlines become Enter, tabs Tab, everything else Unicode.</summary>
        public static List<(ushort vk, char ch, bool up)> TextStrokes(string text)
        {
            var l = new List<(ushort, char, bool)>();
            foreach (char c in text ?? "")
            {
                if (c == '\r') continue;
                ushort vk = c == '\n' ? (ushort)0x0D : c == '\t' ? (ushort)0x09 : (ushort)0;
                l.Add((vk, c, false));
                l.Add((vk, c, true));
            }
            return l;
        }

        public static void TypeText(string text)
        {
            var strokes = TextStrokes(text);
            // Chunks keep long texts from flooding apps that drop input.
            for (int i = 0; i < strokes.Count; i += 64)
            {
                if (i > 0) Thread.Sleep(10);
                Send(strokes.Skip(i).Take(64).Select(s => s.vk != 0 ? Key(s.vk, s.up) : Unit(s.ch, s.up)).ToArray());
            }
        }
    }
}
