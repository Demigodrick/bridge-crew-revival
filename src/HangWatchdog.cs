using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// Hang watchdog. A game that freezes (stuck in a VR runtime, a driver, a deadlock) and is then closed leaves
    /// no crash folder, no dump and no Windows error: only a log that stops. So a background thread watches the
    /// frames: when the game hasn't run one for [Debug] HangWatchdogSeconds (counted from launch for the first
    /// frame), it logs that and writes a minidump of the game, every thread's stack, the loaded and unloaded DLLs,
    /// to BepInEx/sessions/&lt;session&gt;.hang-1.dmp, and a second one if it's still stuck four times as long
    /// later, to show whether it moved. The next launch's crash report takes them. Time with the game in the
    /// background doesn't count when it pauses there (Application.runInBackground off).
    /// </summary>
    internal static class HangWatchdog
    {
        private const int MaxDumps = 2;

        private static long lastFrameTicks;     // Stopwatch ticks of the last frame (or the start)
        private static volatile bool sawFrame;
        private static volatile bool runInBackground = true;
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static uint mainThreadId;
        private static uint processId;

        /// <summary>Plugin.Awake, on the main thread, once the settings are read.</summary>
        internal static void Start(float seconds)
        {
            if (seconds <= 0f)
            {
                Plugin.Log.LogInfo("Hang watchdog: off ([Debug] HangWatchdogSeconds = 0)");
                return;
            }
            try
            {
                mainThreadId = GetCurrentThreadId();
                processId = GetCurrentProcessId();
                runInBackground = Application.runInBackground;
                Interlocked.Exchange(ref lastFrameTicks, clock.ElapsedTicks);
                new Thread(() => Watch(seconds)) { IsBackground = true, Name = "Hang watchdog", Priority = System.Threading.ThreadPriority.AboveNormal }.Start();
                Plugin.Log.LogInfo($"Hang watchdog: on, {seconds:0} s (main thread {mainThreadId})");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Hang watchdog: off: " + e.Message);
            }
        }

        /// <summary>Each frame (Plugin.Update, first thing).</summary>
        internal static void Beat()
        {
            Interlocked.Exchange(ref lastFrameTicks, clock.ElapsedTicks);
            if (!sawFrame)
            {
                sawFrame = true;
                runInBackground = Application.runInBackground;
                Plugin.Log.LogInfo($"Hang watchdog: first frame, {clock.Elapsed.TotalSeconds:0.0} s after the mod loaded");
            }
        }

        private static void Watch(float seconds)
        {
            int dumps = 0;
            bool stalled = false;
            double longest = 0;
            double nextDumpAt = seconds;
            while (true)
            {
                Thread.Sleep(1000);
                try
                {
                    long last = Interlocked.Read(ref lastFrameTicks);
                    double idle = (clock.ElapsedTicks - last) / (double)Stopwatch.Frequency;
                    if (idle < seconds)
                    {
                        if (stalled)
                        {
                            Plugin.Log.LogWarning($"Hang watchdog: the game is running frames again (none for at least {longest:0} s)");
                            stalled = false;
                            nextDumpAt = seconds;
                        }
                        continue;
                    }
                    // Paused in the background by Unity itself: not a hang.
                    if (!runInBackground && !GameInForeground())
                    {
                        Interlocked.Exchange(ref lastFrameTicks, clock.ElapsedTicks);
                        continue;
                    }
                    longest = idle;
                    if (!stalled)
                    {
                        stalled = true;
                        Plugin.Log.LogWarning(sawFrame
                            ? $"Hang watchdog: the game hasn't run a frame for {idle:0} s"
                            : $"Hang watchdog: the game hasn't run its first frame {idle:0} s after the mod loaded");
                    }
                    if (idle >= nextDumpAt && dumps < MaxDumps)
                    {
                        dumps++;
                        nextDumpAt = idle * 4;
                        WriteDump(dumps, idle);
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Hang watchdog: " + e.Message);
                }
            }
        }

        private static void WriteDump(int n, double idle)
        {
            string prefix = CrashReport.SessionPathPrefix;
            if (prefix == null) return;
            string path = $"{prefix}.hang-{n}.dmp";
            bool ok;
            int error = 0;
            using (var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
            {
                ok = MiniDumpWriteDump(GetCurrentProcess(), processId, file.SafeFileHandle.DangerousGetHandle(), DumpType,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (!ok) error = Marshal.GetLastWin32Error();
            }
            if (ok)
                Plugin.Log.LogWarning($"Hang watchdog: saved a dump after {idle:0} s with no frame: {Path.GetFileName(path)} " +
                                      $"({new FileInfo(path).Length / 1024} KB; the game's main thread is {mainThreadId})");
            else
            {
                Plugin.Log.LogWarning($"Hang watchdog: couldn't write a dump (Windows error {error})");
                try { File.Delete(path); } catch { /* left empty */ }
            }
        }

        private static bool GameInForeground()
        {
            uint pid;
            GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            return pid == processId;
        }

        // Every thread's stack and registers, the DLLs (with the unloaded ones), thread and handle details, and the
        // memory the stacks point at: a few MB, enough to see where each thread is.
        private const uint DumpType =
            0x00000004 |  // MiniDumpWithHandleData
            0x00000020 |  // MiniDumpWithUnloadedModules
            0x00000040 |  // MiniDumpWithIndirectlyReferencedMemory
            0x00000100 |  // MiniDumpWithProcessThreadData
            0x00001000;   // MiniDumpWithThreadInfo

        [DllImport("dbghelp.dll", SetLastError = true)]
        private static extern bool MiniDumpWriteDump(IntPtr process, uint processId, IntPtr file, uint type,
            IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    }
}
