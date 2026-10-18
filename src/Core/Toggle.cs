using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PadDeck.Core
{
    /// <summary>
    /// Open buttons that work as on/off switches (the local LLM, ComfyUI): pressed while the tool runs, they run its
    /// stop script instead of starting it again.
    /// </summary>
    public static class Toggle
    {
        /// <summary>"python in C:\claude-tools\ComfyUI" → ("python", "C:\claude-tools\ComfyUI"); "llama-server.exe" → ("llama-server", null).</summary>
        public static (string name, string folder) ParseSpec(string spec)
        {
            spec = (spec ?? "").Trim();
            string folder = null;
            int at = spec.IndexOf(" in ", StringComparison.OrdinalIgnoreCase);
            if (at > 0)
            {
                folder = Environment.ExpandEnvironmentVariables(spec.Substring(at + 4).Trim().Trim('"')).TrimEnd('\\');
                spec = spec.Substring(0, at).Trim();
            }
            if (spec.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) spec = spec.Substring(0, spec.Length - 4);
            return (spec, string.IsNullOrEmpty(folder) ? null : folder);
        }

        /// <summary>True when the exe lives in the folder (or below it).</summary>
        public static bool InFolder(string exePath, string folder) =>
            exePath != null && exePath.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);

        public static bool IsOn(string spec)
        {
            var (name, folder) = ParseSpec(spec);
            if (name.Length == 0) return false;
            bool on = false;
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    if (on) continue;
                    if (folder == null) { on = true; continue; }
                    try { on = InFolder(p.MainModule.FileName, folder); } catch { }
                }
            }
            return on;
        }

        /// <summary>Runs the button's stop script hidden and checks the tool really went. Called off the UI thread.</summary>
        public static Outcome Stop(ButtonConfig b)
        {
            string title = ActionRunner.Title(b);
            try
            {
                string target = Environment.ExpandEnvironmentVariables(b.StopTarget.Trim().Trim('"'));
                if (!File.Exists(target)) throw new InvalidOperationException("Can’t find " + target);
                if (target.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                    Reset.RunHidden("cmd.exe", $"/c \"\"{target}\"\"", 60000);
                else
                    Reset.RunHidden(target, "", 60000);
                for (int t = 0; t < 5000 && IsOn(b.RunningProcess); t += 250) Thread.Sleep(250);
                if (IsOn(b.RunningProcess)) return new Outcome(Glyphs.Warning, title, "Still running after its stop script", Tone.Error);
                return new Outcome(Glyphs.Power, title + " is off", Obs.IsRunning() ? "OBS is back on" : "Turned off", Tone.Normal);
            }
            catch (Exception ex)
            {
                return new Outcome(Glyphs.Warning, title, ex.Message, Tone.Error);
            }
        }
    }
}
