using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace PadDeck.Core
{
    /// <summary>
    /// The CH57x macropad (USB 1189:8840, 6 buttons + 1 knob). Programs its key table through the
    /// vendor HID interface (MI_00), so no extra driver is needed. Protocol from kriomant/ch57x-keyboard-tool (k884x).
    /// </summary>
    public static class Pad
    {
        const string Match = "vid_1189&pid_8840";
        public const byte F19Usage = 0x6E; // HID keyboard usage for F19; F24 = 0x73
        public const ushort MediaVolDown = 0xEA, MediaVolUp = 0xE9, MediaPlay = 0xCD;
        const byte KnobCcw = 16, KnobPress = 17, KnobCw = 18; // 15 button slots, then 3 per knob

        // ---------------- messages ----------------

        static byte[] Msg(params byte[] b)
        {
            var m = new byte[64];
            Array.Copy(b, m, b.Length);
            return m;
        }

        static IEnumerable<byte[]> Finish()
        {
            yield return Msg(0x03, 0xaa, 0xaa);
            yield return Msg(0x03, 0xfd, 0xfe, 0xff);
            yield return Msg(0x03, 0xaa, 0xaa);
        }

        /// <summary>Bind a button (key id 1..15) on layer 1 to a single key without modifiers.</summary>
        public static IEnumerable<byte[]> BindKey(byte keyId, byte usage)
        {
            yield return Msg(0x03, 0xfe, keyId, 0x01, 0x01, 0, 0, 0, 0, 0, 0x01, 0x00, usage);
            foreach (var m in Finish()) yield return m;
        }

        public static IEnumerable<byte[]> BindMedia(byte keyId, ushort code)
        {
            yield return Msg(0x03, 0xfe, keyId, 0x01, 0x02, 0, 0, 0, 0, 0, 0x00, (byte)code, (byte)(code >> 8));
            foreach (var m in Finish()) yield return m;
        }

        /// <summary>The full layout PadDeck uses: buttons F19–F24, knob volume down / play-pause / volume up.</summary>
        public static List<byte[]> Layout()
        {
            var l = new List<byte[]>();
            for (int i = 0; i < AppSettings.ButtonCount; i++) l.AddRange(BindKey((byte)(i + 1), (byte)(F19Usage + i)));
            l.AddRange(BindMedia(KnobCcw, MediaVolDown));
            l.AddRange(BindMedia(KnobPress, MediaPlay));
            l.AddRange(BindMedia(KnobCw, MediaVolUp));
            return l;
        }

        // ---------------- device ----------------

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid cls; public int flags; public IntPtr reserved; }

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, string e, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, int index, ref SP_DEVICE_INTERFACE_DATA d);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int required, IntPtr info);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool WriteFile(SafeFileHandle h, byte[] buf, int len, out int written, IntPtr overlapped);

        /// <summary>All HID interface paths of the pad (lower case).</summary>
        public static List<string> Paths()
        {
            var found = new List<string>();
            HidD_GetHidGuid(out var g);
            IntPtr set = SetupDiGetClassDevs(ref g, null, IntPtr.Zero, 0x12); // PRESENT | DEVICEINTERFACE
            if (set == new IntPtr(-1)) return found;
            try
            {
                var d = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d); i++)
                {
                    SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                    IntPtr buf = Marshal.AllocHGlobal(size);
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                        if (SetupDiGetDeviceInterfaceDetail(set, ref d, buf, size, out _, IntPtr.Zero))
                        {
                            string p = Marshal.PtrToStringUni(buf + 4).ToLowerInvariant();
                            if (p.Contains(Match)) found.Add(p);
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return found;
        }

        public static bool Connected() { try { return Paths().Count > 0; } catch { return false; } }

        /// <summary>Writes the PadDeck layout to the pad. Returns null on success, otherwise an error message.</summary>
        public static string Program()
        {
            string path = Paths().Find(p => p.Contains("&mi_00"));
            if (path == null) return Connected() ? "The pad’s setup interface wasn’t found." : "The macropad isn’t plugged in.";

            var t = Task.Run(() =>
            {
                using var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (h.IsInvalid) return "Couldn’t open the pad (error " + Marshal.GetLastWin32Error() + "). Close the vendor MINI KeyBoard tool if it’s open.";
                // The interface's only output report is id 3 with 64 data bytes. Every message already starts with
                // 0x03 (over raw USB that byte is the report id), so the message fills the report from byte 0.
                var report = new byte[65];
                foreach (var m in Layout())
                {
                    Array.Copy(m, 0, report, 0, 64);
                    if (!WriteFile(h, report, report.Length, out int written, IntPtr.Zero) || written != report.Length)
                        return "Writing to the pad failed (error " + Marshal.GetLastWin32Error() + ").";
                    System.Threading.Thread.Sleep(4);
                }
                return null;
            });
            try
            {
                if (!t.Wait(TimeSpan.FromSeconds(8))) return "The pad stopped responding while being programmed. Unplug it, plug it back in and try again.";
                return t.Result;
            }
            catch (AggregateException e) { return e.InnerException?.Message ?? e.Message; }
        }
    }
}
