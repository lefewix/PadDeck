using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace PadDeck.Core
{
    /// <summary>
    /// Windows won't let a background app (PadDeck) hand focus to a window it opens, so a launched app would only
    /// flash in the taskbar. This watches for the new window and brings it to the front.
    /// </summary>
    public static class Foreground
    {
        /// <summary>Runs <paramref name="launch"/>, then focuses the first new app window that appears within a few seconds.</summary>
        public static void LaunchAndFocus(Action launch, int timeoutMs = 6000)
        {
            var before = new HashSet<IntPtr>();
            foreach (var w in Reset.Windows()) before.Add(w.Hwnd);
            launch();
            for (int waited = 0; waited < timeoutMs; waited += 100)
            {
                Thread.Sleep(100);
                foreach (var w in Reset.Windows())
                {
                    if (before.Contains(w.Hwnd) || w.Process.Equals("PadDeck", StringComparison.OrdinalIgnoreCase)) continue;
                    Thread.Sleep(150); // let it finish drawing its first frame
                    Focus(w.Hwnd);
                    return;
                }
            }
        }

        public static void Focus(IntPtr hwnd)
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, 9); // SW_RESTORE
            // A synthetic Alt press makes PadDeck the source of the last input, which lifts the focus-stealing lock.
            KeyCombo.SendKey(KeyCombo.Alt, false);
            bool ok = SetForegroundWindow(hwnd);
            KeyCombo.SendKey(KeyCombo.Alt, true);
            if (!ok) SwitchToThisWindow(hwnd, true);
            BringWindowToTop(hwnd);
        }

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] static extern void SwitchToThisWindow(IntPtr h, bool altTab);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    }
}
