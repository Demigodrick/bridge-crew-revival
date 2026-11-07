using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace StbcRevival
{
    /// <summary>
    /// Other copies of the game still running, and whether Steam Input's lock is free. A freeze at startup was the
    /// game's main thread waiting, inside Steam's overlay, on Steam Input's shared mutex (SteamXInput_mutex-IPCWrapper)
    /// held by some other process, most likely an earlier stbc.exe that never exited: every launch then froze at
    /// the same point. Logged at startup, when the hang watchdog sees a freeze, and in the crash report.
    /// </summary>
    internal static class OtherInstances
    {
        private const string SteamInputMutex = "SteamXInput_mutex-IPCWrapper";

        /// <summary>One line: the other stbc.exe processes (or none) and the Steam Input lock's state.</summary>
        internal static string Describe(out bool suspicious)
        {
            suspicious = false;
            string others;
            try
            {
                int self = Process.GetCurrentProcess().Id;
                var list = Process.GetProcessesByName("stbc").Where(p => p.Id != self).ToList();
                others = list.Count == 0
                    ? "no other stbc.exe running"
                    : $"{list.Count} other stbc.exe running: " + string.Join(", ", list.Select(Describe).ToArray());
                suspicious |= list.Count > 0;
            }
            catch (Exception e)
            {
                others = "couldn't list processes (" + e.GetType().Name + ")";
            }
            string lockState = SteamInputLock();
            suspicious |= lockState.StartsWith("held");
            return others + "; Steam Input lock: " + lockState;
        }

        private static string Describe(Process p)
        {
            string started;
            try { started = "started " + p.StartTime.ToString("yyyy-MM-dd HH:mm:ss"); }
            catch { started = "start time unknown"; }
            return $"pid {p.Id} ({started})";
        }

        // Free, held (by another process: a wait for it would block), not there (Steam Input isn't hooking games),
        // or unknown. Taken and let go at once when it's free.
        private static string SteamInputLock()
        {
            try
            {
                IntPtr mutex = OpenMutexW(Synchronize | MutexModifyState, false, SteamInputMutex);
                if (mutex == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    return error == 2 ? "not there" : $"unknown (Windows error {error})";
                }
                try
                {
                    uint wait = WaitForSingleObject(mutex, 0);
                    switch (wait)
                    {
                        case WaitTimeout: return "held by another process";
                        case WaitObject0: ReleaseMutex(mutex); return "free";
                        case WaitAbandoned: ReleaseMutex(mutex); return "free (abandoned by a process that ended)";
                        default: return $"unknown (wait result {wait:x})";
                    }
                }
                finally
                {
                    CloseHandle(mutex);
                }
            }
            catch (Exception e)
            {
                return "unknown (" + e.GetType().Name + ")";
            }
        }

        private const uint Synchronize = 0x00100000;
        private const uint MutexModifyState = 0x0001;
        private const uint WaitObject0 = 0x00000000;
        private const uint WaitAbandoned = 0x00000080;
        private const uint WaitTimeout = 0x00000102;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenMutexW(uint access, bool inherit, string name);

        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll")]
        private static extern bool ReleaseMutex(IntPtr mutex);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
