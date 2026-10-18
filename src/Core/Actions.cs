using System;
using System.Diagnostics;
using System.IO;

namespace PadDeck.Core
{
    public enum Tone { Normal, On, Off, Error }

    /// <summary>What the popup shows after a button fires.</summary>
    public sealed record Outcome(string Glyph, string Title, string Detail, Tone Tone);

    public static class Glyphs
    {
        // Segoe Fluent Icons / MDL2 code points
        public const string Open = "", Keys = "", Text = "", Mic = "", MicOff = "",
            Speaker = "", Volume = "", Mute = "", Warning = "", Empty = "";

        public static string For(ActionKind k) => k switch
        {
            ActionKind.Open => Open,
            ActionKind.Keys => Keys,
            ActionKind.Text => Text,
            ActionKind.MicMute => Mic,
            ActionKind.OutputDevice => Speaker,
            ActionKind.AppMute => Volume,
            _ => Empty,
        };
    }

    public static class ActionRunner
    {
        public static string Describe(ActionKind k) => k switch
        {
            ActionKind.None => "Nothing",
            ActionKind.Open => "Open",
            ActionKind.Keys => "Keys",
            ActionKind.Text => "Type text",
            ActionKind.MicMute => "Mic mute",
            ActionKind.OutputDevice => "Output device",
            ActionKind.AppMute => "App mute",
            _ => k.ToString(),
        };

        /// <summary>A short name for the button when the user hasn't given it a label.</summary>
        public static string AutoLabel(ButtonConfig b)
        {
            switch (b.Kind)
            {
                case ActionKind.Open:
                    if (string.IsNullOrWhiteSpace(b.Target)) return "Open";
                    string t = b.Target.Trim().TrimEnd('\\', '/');
                    if (Uri.TryCreate(t, UriKind.Absolute, out var u) && !u.IsFile) return string.IsNullOrEmpty(u.Host) ? u.Scheme : u.Host.Replace("www.", "");
                    return Path.GetFileNameWithoutExtension(t) is { Length: > 0 } n ? n : t;
                case ActionKind.Keys: return string.IsNullOrWhiteSpace(b.Keys) ? "Keys" : b.Keys.Trim();
                case ActionKind.Text:
                    var s = (b.Text ?? "").Replace("\r", "").Replace("\n", " ").Trim();
                    return s.Length == 0 ? "Type text" : s.Length > 18 ? s.Substring(0, 18) + "…" : s;
                case ActionKind.AppMute: return string.IsNullOrWhiteSpace(b.App) ? "App mute" : "Mute " + b.App.Trim();
                case ActionKind.None: return "";
                default: return Describe(b.Kind);
            }
        }

        public static string Title(ButtonConfig b) => string.IsNullOrWhiteSpace(b.Label) ? AutoLabel(b) : b.Label.Trim();

        /// <summary>Runs the button's action. Called off the UI thread. Never throws.</summary>
        public static Outcome Run(ButtonConfig b)
        {
            string title = Title(b);
            try
            {
                switch (b.Kind)
                {
                    case ActionKind.Open:
                    {
                        if (string.IsNullOrWhiteSpace(b.Target)) throw new InvalidOperationException("Nothing to open yet");
                        string target = Environment.ExpandEnvironmentVariables(b.Target.Trim().Trim('"'));
                        var psi = new ProcessStartInfo(target) { UseShellExecute = true, Arguments = b.Args ?? "" };
                        if (File.Exists(target)) psi.WorkingDirectory = Path.GetDirectoryName(target);
                        Process.Start(psi)?.Dispose();
                        return new Outcome(Glyphs.Open, title, "Opened", Tone.Normal);
                    }
                    case ActionKind.Keys:
                    {
                        var seq = KeyCombo.Parse(b.Keys);
                        if (seq.Count == 0) throw new InvalidOperationException("No keys set yet");
                        KeyCombo.SendSequence(seq);
                        return new Outcome(Glyphs.Keys, title, KeyCombo.Format(seq), Tone.Normal);
                    }
                    case ActionKind.Text:
                    {
                        if (string.IsNullOrEmpty(b.Text)) throw new InvalidOperationException("No text set yet");
                        KeyCombo.TypeText(b.Text);
                        return new Outcome(Glyphs.Text, title, "Typed " + b.Text.Replace("\r", "").Length + " characters", Tone.Normal);
                    }
                    case ActionKind.MicMute:
                    {
                        var (muted, name) = AudioCtl.ToggleMic(b.DeviceId);
                        return new Outcome(muted ? Glyphs.MicOff : Glyphs.Mic, muted ? "Mic muted" : "Mic on", Short(name), muted ? Tone.Off : Tone.On);
                    }
                    case ActionKind.OutputDevice:
                    {
                        if (b.DeviceIds.Count == 0) throw new InvalidOperationException("Pick the output devices to switch between");
                        string name = AudioCtl.CycleOutput(b.DeviceIds);
                        return new Outcome(Glyphs.Speaker, "Output", Short(name), Tone.Normal);
                    }
                    case ActionKind.AppMute:
                    {
                        if (string.IsNullOrWhiteSpace(b.App)) throw new InvalidOperationException("Pick an app to mute");
                        bool muted = AudioCtl.ToggleApp(b.App);
                        return new Outcome(muted ? Glyphs.Mute : Glyphs.Volume, b.App.Trim() + (muted ? " muted" : " unmuted"), title, muted ? Tone.Off : Tone.On);
                    }
                    default:
                        return null;
                }
            }
            catch (Exception ex)
            {
                return new Outcome(Glyphs.Warning, title.Length > 0 ? title : "PadDeck", ex.Message, Tone.Error);
            }
        }

        /// <summary>"Speakers (FIIO KA13)" reads better than the endpoint's full name when it's long.</summary>
        static string Short(string name) => name != null && name.Length > 48 ? name.Substring(0, 47) + "…" : name;
    }
}
