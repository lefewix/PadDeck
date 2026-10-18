using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace PadDeck
{
    public enum ActionKind { None, Open, Keys, Text, MicMute, OutputDevice, AppMute, Reset, Obs }

    /// <summary>Put an app's main window in a fixed spot whenever it opens.</summary>
    public sealed class WindowRule
    {
        public string Name { get; set; } = "";
        public string Process { get; set; } = "";
        public Core.MonitorPick Monitor { get; set; }
        public Core.Zone Zone { get; set; }
    }

    /// <summary>A Reset core app: kept open, and started with Target/Args when its process isn't running.</summary>
    public sealed class CoreApp
    {
        public string Name { get; set; } = "";
        /// <summary>Process name without .exe, e.g. "obs64".</summary>
        public string Process { get; set; } = "";
        /// <summary>What to run if it isn't running; empty = only keep it open.</summary>
        public string Target { get; set; } = "";
        public string Args { get; set; } = "";
        /// <summary>Close and reopen it on every reset (e.g. to fix a misbehaving OBS).</summary>
        public bool Restart { get; set; }
    }

    /// <summary>A sound device remembered by id and name, so it can be found again if Windows gives it a new id.</summary>
    public sealed class DeviceRef
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    public sealed class ButtonConfig
    {
        public string Label { get; set; } = "";
        /// <summary>"" = the action's default symbol, "glyph:E8BD" = a Segoe symbol, otherwise an image, .ico, .exe or .lnk path.</summary>
        public string Icon { get; set; } = "";
        public ActionKind Kind { get; set; }
        /// <summary>Open: file, folder, app or URL.</summary>
        public string Target { get; set; } = "";
        public string Args { get; set; } = "";
        /// <summary>Keys: e.g. "Ctrl+Shift+M" or "Ctrl+C, Ctrl+V".</summary>
        public string Keys { get; set; } = "";
        public string Text { get; set; } = "";
        /// <summary>MicMute: capture endpoint id, null = the default mic.</summary>
        public string DeviceId { get; set; }
        /// <summary>OutputDevice (sound switch): devices to cycle through, in order.</summary>
        public List<DeviceRef> Devices { get; set; } = new List<DeviceRef>();
        /// <summary>OutputDevice: cycle recording devices instead of playback devices.</summary>
        public bool Recording { get; set; }
        /// <summary>OutputDevice: also change the communications default (what Discord calls use).</summary>
        public bool SwitchComms { get; set; }
        /// <summary>AppMute: process name without .exe, e.g. "Discord".</summary>
        public string App { get; set; } = "";
        /// <summary>Reset: apps that stay open and get started if missing.</summary>
        public List<CoreApp> Core { get; set; } = new List<CoreApp>();
        /// <summary>Reset: stop the local LLM server and ComfyUI first (frees VRAM).</summary>
        public bool StopGpu { get; set; } = true;
        public Core.ObsCommand ObsAction { get; set; }
        /// <summary>Open as an on/off switch: how to tell it's on, e.g. "llama-server" or "python in C:\claude-tools\ComfyUI".</summary>
        public string RunningProcess { get; set; } = "";
        /// <summary>Open as an on/off switch: what turns it off, e.g. its Stop.bat.</summary>
        public string StopTarget { get; set; } = "";
        /// <summary>Open: a GPU-heavy tool whose script closes OBS and the other GPU tools. It won't start while OBS is
        /// live or recording, and a game starting while it's on gets a warning.</summary>
        public bool GpuHeavy { get; set; }
        /// <summary>Any button: what to open when the button is held instead of pressed, e.g. the clips folder.</summary>
        public string HoldTarget { get; set; } = "";

        public bool IsSet => Kind != ActionKind.None;
        [JsonIgnore] public bool IsToggle => Kind == ActionKind.Open && !string.IsNullOrWhiteSpace(RunningProcess) && !string.IsNullOrWhiteSpace(StopTarget);
        [JsonIgnore] public bool HasHold => !string.IsNullOrWhiteSpace(HoldTarget);
    }

    public sealed class AppSettings
    {
        public const int ButtonCount = 6;

        public List<ButtonConfig> Buttons { get; set; } = new List<ButtonConfig>();
        public List<WindowRule> Windows { get; set; } = new List<WindowRule>();
        public bool PopupOn { get; set; } = true;
        public bool PopupTop { get; set; }
        /// <summary>Screen device name (e.g. \\.\DISPLAY1); null = primary.</summary>
        public string PopupScreen { get; set; }
        public bool PopupSkipFullscreen { get; set; }
        /// <summary>How long a popup stays on screen, in seconds.</summary>
        public double PopupSeconds { get; set; } = 3;
        public bool CloseToTray { get; set; } = true;
        /// <summary>Warn when a fullscreen game starts while a GPU tool is on or the replay buffer is off.</summary>
        public bool GameWarnings { get; set; } = true;
        public bool PadProgrammed { get; set; }
        public bool FirstRunDone { get; set; }

        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PadDeck");
        static readonly string FilePath = Path.Combine(Dir, "settings.json");

        /// <summary>Set when settings.json couldn't be read at startup; shown once in a popup.</summary>
        public static string LoadProblem { get; private set; }
        internal static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        public void Normalize()
        {
            Buttons ??= new List<ButtonConfig>();
            Windows ??= new List<WindowRule>();
            while (Buttons.Count < ButtonCount) Buttons.Add(new ButtonConfig());
            if (Buttons.Count > ButtonCount) Buttons.RemoveRange(ButtonCount, Buttons.Count - ButtonCount);
            foreach (var b in Buttons)
            {
                b.Devices ??= new List<DeviceRef>();
                b.Core ??= new List<CoreApp>();
            }
        }

        public static AppSettings Load()
        {
            var s = LoadFrom(FilePath, out string problem);
            if (problem != null) LoadProblem = problem;
            return s;
        }

        public void Save() => SaveTo(FilePath);

        static AppSettings Read(string file)
        {
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), Opts);
            if (s == null) throw new InvalidDataException("empty settings");
            s.Normalize();
            return s;
        }

        /// <summary>
        /// Reads the settings, falling back to the copy kept from the previous save. A damaged file is copied aside
        /// first, so the next save can't overwrite the buttons with defaults and lose them.
        /// </summary>
        internal static AppSettings LoadFrom(string file, out string problem)
        {
            problem = null;
            if (File.Exists(file))
            {
                try { return Read(file); }
                catch
                {
                    string aside = Path.Combine(Path.GetDirectoryName(file), "settings.broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
                    try { File.Copy(file, aside, true); } catch { }
                    try
                    {
                        var b = Read(file + ".bak");
                        problem = "Loaded the backup. The damaged file is " + Path.GetFileName(aside);
                        return b;
                    }
                    catch { problem = "Started with defaults. The damaged file is " + Path.GetFileName(aside); }
                }
            }
            var d = new AppSettings();
            d.Normalize();
            return d;
        }

        internal void SaveTo(string file)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                string tmp = file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
                // Replace keeps the previous file as .bak, which LoadFrom falls back to.
                if (File.Exists(file)) File.Replace(tmp, file, file + ".bak");
                else File.Move(tmp, file);
            }
            catch { }
        }
    }

    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "PadDeck";

        public static bool Enabled
        {
            get
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                return k?.GetValue(Name) != null;
            }
            set
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                if (value) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --tray");
                else k.DeleteValue(Name, false);
            }
        }
    }
}
