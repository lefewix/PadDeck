using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PadDeck.Core
{
    /// <summary>
    /// Tracks which pad keys are held so auto-repeat fires an action only once per press.
    /// Kept separate from the hook so it can be tested.
    /// </summary>
    public sealed class KeyGate
    {
        readonly bool[] held = new bool[AppSettings.ButtonCount];

        /// <summary>Returns true when this event is a fresh press.</summary>
        public bool Press(int index, bool down)
        {
            if (!down) { held[index] = false; return false; }
            if (held[index]) return false;
            held[index] = true;
            return true;
        }
    }

    /// <summary>
    /// Low-level keyboard hook for F19–F24, which the pad sends after it has been programmed.
    /// It runs on its own thread that does nothing else: Windows silently removes a low-level hook that is slow to
    /// answer even once, and every key on the system waits for it, so it must never share a thread with the UI.
    /// <see cref="OnPadKey"/> is called on that thread and must return quickly.
    /// </summary>
    public sealed class KeyHook : IDisposable
    {
        public const int FirstVk = 0x82; // F19
        public const int LastVk = FirstVk + AppSettings.ButtonCount - 1; // F24

        /// <summary>(button index, isDown, isFreshPress) → swallow the key? Called on the hook thread.</summary>
        public Func<int, bool, bool, bool> OnPadKey;

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }
        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);

        [StructLayout(LayoutKind.Sequential)]
        struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

        readonly HookProc proc; // held so the GC keeps the delegate alive
        readonly KeyGate gate = new KeyGate();
        readonly Thread thread;
        IntPtr hook;
        uint threadId;

        public KeyHook()
        {
            proc = Callback;
            using var ready = new ManualResetEventSlim();
            int error = 0;
            thread = new Thread(() =>
            {
                threadId = GetCurrentThreadId();
                using (var me = Process.GetCurrentProcess())
                    hook = SetWindowsHookEx(13, proc, GetModuleHandle(me.MainModule.ModuleName), 0);
                if (hook == IntPtr.Zero) error = Marshal.GetLastWin32Error();
                ready.Set();
                if (hook == IntPtr.Zero) return;
                // The hook is called from inside GetMessage; WM_QUIT from Dispose ends the loop.
                while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
                UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
            }) { IsBackground = true, Name = "PadDeck key hook", Priority = ThreadPriority.AboveNormal };
            thread.Start();
            ready.Wait();
            if (hook == IntPtr.Zero) throw new InvalidOperationException("Could not install the keyboard hook (error " + error + ")");
        }

        IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                bool injected = (k.flags & 0x10) != 0;
                if (k.vkCode >= FirstVk && k.vkCode <= LastVk && !(injected && k.extra == KeyCombo.Tag))
                {
                    int msg = wParam.ToInt32();
                    bool down = msg == 0x100 || msg == 0x104;
                    int index = (int)k.vkCode - FirstVk;
                    bool fresh = gate.Press(index, down);
                    bool swallow = false;
                    try { swallow = OnPadKey?.Invoke(index, down, fresh) ?? false; } catch { }
                    if (swallow) return new IntPtr(1);
                }
            }
            return CallNextHookEx(hook, code, wParam, lParam);
        }

        public void Dispose()
        {
            if (hook == IntPtr.Zero) return;
            PostThreadMessage(threadId, 0x0012, IntPtr.Zero, IntPtr.Zero); // WM_QUIT
            thread.Join(1000);
        }
    }
}
