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

        /// <summary>Makes the next device in the list the default output. Returns its name.</summary>
        public static string CycleOutput(IList<string> ids)
        {
            using var en = new MMDeviceEnumerator();
            var active = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToDictionary(d => d.ID, d => d.FriendlyName);
            var usable = ids.Where(active.ContainsKey).ToList();
            if (usable.Count == 0) throw new InvalidOperationException("None of the chosen output devices are connected");
            string cur = null;
            try { cur = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { }
            string next = NextInCycle(usable, cur);
            var pc = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")));
            try
            {
                for (int role = 0; role < 3; role++) Marshal.ThrowExceptionForHR(pc.SetDefaultEndpoint(next, role));
            }
            finally { Marshal.ReleaseComObject(pc); }
            return active[next];
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
