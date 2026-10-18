using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace PadDeck.Core
{
    public sealed record AudioDevice(string Id, string Name);

    public static class AudioCtl
    {
        public static List<AudioDevice> Devices(DataFlow flow)
        {
            using var en = new MMDeviceEnumerator();
            return en.EnumerateAudioEndPoints(flow, DeviceState.Active).Select(d => new AudioDevice(d.ID, d.FriendlyName)).ToList();
        }

        static MMDevice Find(MMDeviceEnumerator en, DataFlow flow, string id)
        {
            if (!string.IsNullOrEmpty(id))
            {
                try
                {
                    var d = en.GetDevice(id);
                    if (d.State == DeviceState.Active) return d;
                }
                catch { }
            }
            return en.GetDefaultAudioEndpoint(flow, Role.Console);
        }

        /// <summary>Toggles the mic's mute. Returns (muted, device name).</summary>
        public static (bool muted, string name) ToggleMic(string id)
        {
            using var en = new MMDeviceEnumerator();
            var d = Find(en, DataFlow.Capture, id);
            var v = d.AudioEndpointVolume;
            v.Mute = !v.Mute;
            return (v.Mute, d.FriendlyName);
        }

        public static bool? MicMuted(string id)
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                return Find(en, DataFlow.Capture, id).AudioEndpointVolume.Mute;
            }
            catch { return null; }
        }

        /// <summary>Picks the device after the current default in the list (wrapping). Exposed for tests.</summary>
        public static string NextInCycle(IList<string> ids, string current)
        {
            if (ids.Count == 0) return null;
            int i = ids.IndexOf(current);
            return ids[(i + 1) % ids.Count];
        }

        /// <summary>The adapter part of an endpoint name without Windows' "2- " re-enumeration prefix:
        /// "DC Headphones (2- FIIO KA13)" → "FIIO KA13".</summary>
        public static string Adapter(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            int open = name.IndexOf('('), close = name.LastIndexOf(')');
            string a = open >= 0 && close > open ? name.Substring(open + 1, close - open - 1) : name;
            return System.Text.RegularExpressions.Regex.Replace(a, @"^\d+-\s*", "").Trim();
        }

        /// <summary>
        /// Maps remembered devices onto the connected ones, in order: by id, then by exact name, then by adapter
        /// name when exactly one connected device has it. Devices that can't be found are skipped.
        /// </summary>
        public static List<AudioDevice> Resolve(IEnumerable<DeviceRef> refs, IList<AudioDevice> active)
        {
            var result = new List<AudioDevice>();
            foreach (var r in refs)
            {
                var d = active.FirstOrDefault(a => a.Id == r.Id);
                if (d == null)
                {
                    var byName = active.Where(a => string.Equals(a.Name, r.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (byName.Count == 1) d = byName[0];
                }
                if (d == null && !string.IsNullOrEmpty(r.Name))
                {
                    var byAdapter = active.Where(a => string.Equals(Adapter(a.Name), Adapter(r.Name), StringComparison.OrdinalIgnoreCase)).ToList();
                    if (byAdapter.Count == 1) d = byAdapter[0];
                }
                if (d != null && !result.Contains(d)) result.Add(d);
            }
            return result;
        }

        /// <summary>
        /// Makes the next device in the list the default (like SoundSwitch). Communications is only changed when asked,
        /// so a call can stay on the headset. Returns the new device's name.
        /// </summary>
        public static string CycleDevice(IList<DeviceRef> refs, bool recording, bool comms)
        {
            var flow = recording ? DataFlow.Capture : DataFlow.Render;
            using var en = new MMDeviceEnumerator();
            var active = Devices(flow);
            var usable = Resolve(refs, active);
            if (usable.Count == 0) throw new InvalidOperationException("None of the chosen " + (recording ? "recording" : "playback") + " devices are connected");
            string cur = null;
            try { cur = en.GetDefaultAudioEndpoint(flow, Role.Multimedia).ID; } catch { }
            var ids = usable.Select(d => d.Id).ToList();
            string next = NextInCycle(ids, cur);
            var pc = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")));
            try
            {
                Marshal.ThrowExceptionForHR(pc.SetDefaultEndpoint(next, 0)); // console
                Marshal.ThrowExceptionForHR(pc.SetDefaultEndpoint(next, 1)); // multimedia
                if (comms) Marshal.ThrowExceptionForHR(pc.SetDefaultEndpoint(next, 2));
            }
            finally { Marshal.ReleaseComObject(pc); }
            return usable.First(d => d.Id == next).Name;
        }

        /// <summary>The device list SoundSwitch cycles through, read from its settings file. Null if it isn't set up.</summary>
        public static (List<DeviceRef> playback, List<DeviceRef> recording, bool comms)? SoundSwitchDevices()
        {
            try
            {
                string path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoundSwitch", "SoundSwitchConfiguration.json");
                if (!System.IO.File.Exists(path)) return null;
                using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
                var root = doc.RootElement;
                var play = new List<DeviceRef>();
                var rec = new List<DeviceRef>();
                if (root.TryGetProperty("SelectedDevices", out var sel))
                {
                    foreach (var d in sel.EnumerateArray())
                    {
                        var r = new DeviceRef { Id = d.GetProperty("Id").GetString(), Name = d.GetProperty("Name").GetString() };
                        (d.TryGetProperty("Type", out var t) && t.GetInt32() == 1 ? rec : play).Add(r);
                    }
                }
                bool comms = root.TryGetProperty("ChangeCommunications", out var c) && c.GetBoolean();
                return (play, rec, comms);
            }
            catch { return null; }
        }

        static string ProcName(uint pid)
        {
            try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; }
            catch { return null; }
        }

        /// <summary>Names of processes currently playing (or able to play) audio.</summary>
        public static List<string> AudioApps()
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                var s = d.AudioSessionManager.Sessions;
                for (int i = 0; i < s.Count; i++)
                {
                    uint pid = s[i].GetProcessID;
                    if (pid == 0) continue;
                    var n = ProcName(pid);
                    if (n != null) names.Add(n);
                }
            }
            return names.ToList();
        }

        /// <summary>Toggles mute on every audio session of the app, on every output. Returns the new state.</summary>
        public static bool ToggleApp(string app)
        {
            app = (app ?? "").Trim();
            if (app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) app = app.Substring(0, app.Length - 4);
            var sessions = new List<SimpleAudioVolume>();
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                var s = d.AudioSessionManager.Sessions;
                for (int i = 0; i < s.Count; i++)
                {
                    uint pid = s[i].GetProcessID;
                    if (pid != 0 && string.Equals(ProcName(pid), app, StringComparison.OrdinalIgnoreCase)) sessions.Add(s[i].SimpleAudioVolume);
                }
            }
            if (sessions.Count == 0) throw new InvalidOperationException(app + " isn’t playing audio right now");
            bool mute = !sessions.All(v => v.Mute);
            foreach (var v in sessions) v.Mute = mute;
            return mute;
        }

        // Undocumented but stable since Windows 7; used by every audio-switcher tool.
        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
            [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
            [PreserveSig] int ResetDeviceFormat(IntPtr a);
            [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
            [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
            [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
            [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
            [PreserveSig] int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
            [PreserveSig] int SetEndpointVisibility(IntPtr a, int b);
        }
    }
}
