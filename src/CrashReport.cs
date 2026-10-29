using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// Crash reports. A hard crash (in the engine, a VR runtime or a driver) takes the mod down with the game, and
    /// both logs are rewritten before the mod loads again (Unity's output_log.txt at engine start, BepInEx's
    /// LogOutput.log before any plugin). So:
    ///   - every launch writes its own log as it goes, BepInEx/sessions/&lt;date-time&gt;.log, flushed line by line,
    ///     everything BepInEx hears (the mod, other plugins, the game's own messages); the last 5 are kept;
    ///   - and copies the engine's log (stbc_Data/output_log.txt: its native lines, stack traces and crash details,
    ///     most of which never reach BepInEx) to &lt;date-time&gt;.unity.txt, following it four times a second;
    ///   - a marker (BepInEx/sessions/running.txt) lives for as long as the game runs, removed on a clean quit;
    ///   - found at launch, the last run didn't close properly: its session log, Unity's crash folder, Windows'
    ///     "Application Error" entries for stbc.exe, SteamVR's logs and the mod's settings go into
    ///     BepInEx/crash-reports/crash-&lt;date-time&gt;.zip, with Steam IDs, the Windows user name, your Steam name, the
    ///     names of players you were in a room with (kept in the marker as they're seen) and the block list's names
    ///     blanked (people attach these on GitHub);
    ///   - on the shuttle, a popup says where it is, and the folder opens.
    /// </summary>
    internal static class CrashReport
    {
        private const int SessionsKept = 5;
        private const float SettleSeconds = 10f;

        private static string SessionsDir => Path.Combine(Paths.BepInExRootPath, "sessions");
        private static string ReportsDir => Path.Combine(Paths.BepInExRootPath, "crash-reports");
        private static string Marker => Path.Combine(SessionsDir, "running.txt");

        private static SessionLog session;
        private static string sessionName;
        private static DateTime startedUtc;
        private static string steamName;      // yours, once Steam is up: kept in the marker for the next report
        private static string reportReady;    // set by the report thread: the zip's path
        private static bool told;
        private static float onShuttleSince = -1f;
        private static string system;         // read on the main thread (Unity's SystemInfo), used by the report thread
        private static readonly HashSet<string> seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // players in your rooms
        private static float nextNames;

        /// <summary>Plugin.Awake, first thing: a report for the last run if it crashed, then this run's log.</summary>
        internal static void Start()
        {
            try
            {
                Directory.CreateDirectory(SessionsDir);
                var last = File.Exists(Marker) ? ReadMarker() : null;
                system = $"{SystemInfo.operatingSystem}{(UnderWine() ? " (Wine/Proton)" : "")}; {SystemInfo.processorType}; " +
                         $"{SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceVersion}); {SystemInfo.systemMemorySize} MB; Unity {Application.unityVersion}";
                startedUtc = DateTime.UtcNow;
                sessionName = DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".log";
                session = new SessionLog(Path.Combine(SessionsDir, sessionName));
                BepInEx.Logging.Logger.Listeners.Add(session);
                FollowEngineLog(Path.Combine(Application.dataPath, "output_log.txt"), Path.Combine(SessionsDir, EngineCopy(sessionName)));
                WriteMarker();
                foreach (var pattern in new[] { "*.log", "*.unity.txt" })
                    foreach (var old in Directory.GetFiles(SessionsDir, pattern).OrderByDescending(f => f).Skip(SessionsKept))
                        TryDelete(old);
                if (last != null)
                {
                    Plugin.Log.LogWarning($"Crash report: the last run (from {last.Started.ToLocalTime():yyyy-MM-dd HH:mm}) didn't close properly; saving a report");
                    new Thread(() => Build(last)) { IsBackground = true, Name = "Crash report" }.Start();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Crash report: off: " + e.Message);
            }
        }

        /// <summary>Plugin.OnApplicationQuit: a clean exit, no report next time.</summary>
        internal static void OnQuit()
        {
            TryDelete(Marker);
            session?.Dispose();
        }

        /// <summary>Each frame: remember your Steam name; on the shuttle, say where a new report is.</summary>
        internal static void Update()
        {
            if (steamName == null && SteamName.TryGet(out var name))
            {
                steamName = name;
                WriteMarker();
            }
            if (Time.realtimeSinceStartup >= nextNames)
            {
                nextNames = Time.realtimeSinceStartup + 5f;
                if (NoteRoomPlayers()) WriteMarker();
            }
            if (reportReady == null || told) return;
            bool onShuttle = GameState.Phase() == "GamePhase_MainMenu" && !GameState.Loading()
                             && GameState.ConnectedAndReady() && GameState.RoomName() == null;
            if (!onShuttle)
            {
                onShuttleSince = -1f;
                return;
            }
            if (onShuttleSince < 0f) onShuttleSince = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - onShuttleSince < SettleSeconds) return;
            try
            {
                if (!UI.PopupMgr.ShowPopup(
                        "The game didn't close properly last time. A crash report was saved in the game folder, " +
                        $"under BepInEx\\crash-reports:\n{Path.GetFileName(reportReady)}\n\n" +
                        "If you're reporting a problem, attach it to your report on GitHub.",
                        null, UI.PDHCCBFJODN.Notification, true))
                    return; // no popups yet: next frame
            }
            catch (Exception e)
            {
                Plugin.Log.LogInfo("Crash report: couldn't show the popup: " + e.Message);
            }
            told = true;
            if (!UnderWine())
            {
                try { Process.Start("explorer.exe", $"/select,\"{reportReady}\""); }
                catch (Exception e) { Plugin.Log.LogInfo("Crash report: couldn't open the folder: " + e.Message); }
            }
        }

        // ---- The marker -----------------------------------------------------------------------------------

        private sealed class LastRun
        {
            public DateTime Started;
            public string Log;
            public string SteamName;
            public string Version;
            public List<string> Names = new List<string>();
        }

        private static void WriteMarker()
        {
            try
            {
                File.WriteAllText(Marker, $"started={startedUtc:o}\nlog={sessionName}\nsteamname={steamName}\nversion={Plugin.Version}\n"
                                          + $"names={string.Join("\t", seenNames.ToArray())}\n");
            }
            catch (Exception e)
            {
                Plugin.Log.LogInfo("Crash report: couldn't write the marker: " + e.Message);
            }
        }

        private static LastRun ReadMarker()
        {
            var run = new LastRun { Started = File.GetLastWriteTimeUtc(Marker) };
            foreach (var line in File.ReadAllLines(Marker))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line.Substring(0, eq), value = line.Substring(eq + 1).Trim();
                if (key == "started" && DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)) run.Started = at.ToUniversalTime();
                else if (key == "log") run.Log = value;
                else if (key == "steamname" && value.Length > 0) run.SteamName = value;
                else if (key == "version") run.Version = value;
                else if (key == "names") run.Names.AddRange(value.Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries));
            }
            return run;
        }

        // ---- The report (a background thread) --------------------------------------------------------------

        private static void Build(LastRun last)
        {
            try
            {
                // The block list's names too (Steam IDs go anyway): it's in the settings, and they may be in the logs.
                foreach (var item in (Plugin.BlockedPlayers?.Value ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    if (item.IndexOf('=') is int eq && eq >= 0) last.Names.Add(item.Substring(eq + 1).Trim());
                var files = new List<KeyValuePair<string, byte[]>>();
                var notes = new StringBuilder();
                void Add(string name, string path, bool text = true)
                {
                    var data = ReadShared(path);
                    if (data == null) return;
                    files.Add(new KeyValuePair<string, byte[]>(name, text ? Encoding.UTF8.GetBytes(Redact(Encoding.UTF8.GetString(data), last)) : data));
                }

                // This mod's record of that run: its log (what LogOutput.log had) and the engine's (output_log.txt).
                if (last.Log != null) Add("LogOutput.log", Path.Combine(SessionsDir, last.Log));
                if (last.Log == null || !File.Exists(Path.Combine(SessionsDir, last.Log))) notes.AppendLine("No mod log for that run.");
                if (last.Log != null) Add("output_log.txt", Path.Combine(SessionsDir, EngineCopy(last.Log)));
                if (last.Log == null || !File.Exists(Path.Combine(SessionsDir, EngineCopy(last.Log)))) notes.AppendLine("No copy of the engine's log for that run.");

                // Unity's crash folder (<date>_<time> in the game folder), if it caught the crash.
                var since = last.Started.AddMinutes(-1);
                var crashDirs = Directory.GetDirectories(Paths.GameRootPath)
                    .Where(d => Regex.IsMatch(Path.GetFileName(d), @"^\d{4}-\d{2}-\d{2}[_-]\d{6}$") && Directory.GetCreationTimeUtc(d) >= since)
                    .OrderBy(d => d).ToList();
                foreach (var dir in crashDirs)
                    foreach (var file in Directory.GetFiles(dir))
                    {
                        bool dump = file.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase);
                        if (dump && new FileInfo(file).Length > 64L * 1024 * 1024) continue; // too big to attach
                        Add($"unity-crash/{Path.GetFileName(dir)}/{Path.GetFileName(file)}", file, !dump);
                    }
                notes.AppendLine(crashDirs.Count > 0 ? $"Unity crash folder: {string.Join(", ", crashDirs.Select(Path.GetFileName).ToArray())}"
                                                     : "Unity didn't write a crash folder (the crash was too hard for it to catch, or the game was closed another way).");

                // Windows' own record: names the DLL that crashed, even when Unity couldn't.
                var events = WindowsErrors(last.Started);
                if (events != null) files.Add(new KeyValuePair<string, byte[]>("windows-application-errors.txt", Encoding.UTF8.GetBytes(Redact(events, last))));
                else notes.AppendLine(UnderWine() ? "Windows event log: not available under Wine/Proton." : "Windows event log: couldn't be read.");

                // SteamVR's logs, if it ran since then.
                var steamLogs = SteamLogsDir();
                if (steamLogs != null)
                    foreach (var name in new[] { "vrserver.txt", "vrserver.previous.txt", "vrcompositor.txt", "vrcompositor.previous.txt", "vrclient_stbc.txt", "vrclient_stbc.previous.txt" })
                    {
                        var path = Path.Combine(steamLogs, name);
                        if (File.Exists(path) && File.GetLastWriteTimeUtc(path) >= since) Add("steamvr/" + name, path);
                    }

                Add("stbc.revival.cfg", Path.Combine(Paths.ConfigPath, "stbc.revival.cfg"));
                Add("BepInEx.cfg", Path.Combine(Paths.ConfigPath, "BepInEx.cfg"));
                files.Insert(0, new KeyValuePair<string, byte[]>("summary.txt", Encoding.UTF8.GetBytes(Redact(Summary(last, notes.ToString()), last))));

                Directory.CreateDirectory(ReportsDir);
                var zip = Path.Combine(ReportsDir, $"crash-{last.Started.ToLocalTime():yyyy-MM-dd_HHmmss}.zip");
                BugReport.Zip.Write(zip, files);
                Plugin.Log.LogInfo($"Crash report: saved {zip} ({files.Count} files)");
                reportReady = zip;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Crash report: couldn't save it: " + e);
            }
        }

        private static string Summary(LastRun last, string notes)
        {
            var s = new StringBuilder();
            s.AppendLine("Bridge Crew Revival crash report");
            s.AppendLine($"The run that didn't close properly: started {last.Started.ToLocalTime():yyyy-MM-dd HH:mm:ss} (local), mod {last.Version ?? "?"}");
            s.AppendLine($"Report made: {DateTime.Now:yyyy-MM-dd HH:mm:ss} by mod {Plugin.Version}");
            s.AppendLine($"System: {system}");
            s.AppendLine($"Command line: {Environment.CommandLine}");
            s.AppendLine();
            s.Append(notes);
            return s.ToString();
        }

        // The "Application Error" and "Windows Error Reporting" entries since that run started, for stbc.exe
        // (wevtutil, part of Windows; no admin needed). Null if they can't be read.
        private static string WindowsErrors(DateTime sinceUtc)
        {
            if (UnderWine()) return null;
            try
            {
                var query = "*[System[(Provider[@Name='Application Error'] or Provider[@Name='Windows Error Reporting']) and " +
                            $"TimeCreated[@SystemTime>='{sinceUtc.AddMinutes(-1):yyyy-MM-ddTHH:mm:ss}.000Z']]]";
                var start = new ProcessStartInfo("wevtutil", $"qe Application /q:\"{query}\" /f:text /c:30")
                {
                    UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
                };
                using (var process = Process.Start(start))
                {
                    var output = process.StandardOutput.ReadToEnd();
                    if (!process.WaitForExit(15000)) return null;
                    // One entry per "Event[n]:" block; only the game's.
                    var entries = Regex.Split(output, @"(?m)^(?=Event\[\d+\]:)")
                        .Where(e => e.IndexOf("stbc", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                    return entries.Count > 0 ? string.Join("\n", entries.ToArray()) : "No Application Error entries for stbc.exe since that run started.";
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogInfo("Crash report: Windows event log: " + e.Message);
                return null;
            }
        }

        // Steam's logs folder: from the registry, else up from the game's library.
        private static string SteamLogsDir()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                    if (key?.GetValue("SteamPath") is string steam && Directory.Exists(Path.Combine(steam, "logs")))
                        return Path.Combine(steam, "logs");
            }
            catch { /* no registry */ }
            var library = Directory.GetParent(Paths.GameRootPath)?.Parent?.Parent?.FullName; // <library>/steamapps/common/<game>
            return library != null && Directory.Exists(Path.Combine(library, "logs")) ? Path.Combine(library, "logs") : null;
        }

        // Steam IDs, the Windows user name (and any C:\Users\<name>), your Steam name, other players' names, and the
        // block list's value: blanked. Longest names first, so one that contains another goes whole.
        private static string Redact(string text, LastRun last)
        {
            text = Regex.Replace(text, @"7656119\d{10}", "<steam id>");
            text = Regex.Replace(text, @"(?i)([\\/]Users[\\/])[^\\/\r\n""]+", "$1<user>");
            text = Regex.Replace(text, @"(?m)^(Blocked\s*=)[^\r\n]*", "$1 <blanked>");
            if (!string.IsNullOrEmpty(Environment.UserName) && Environment.UserName.Length >= 3)
                text = Regex.Replace(text, Regex.Escape(Environment.UserName), "<user>", RegexOptions.IgnoreCase);
            foreach (var name in new[] { last.SteamName, steamName }.Where(n => !string.IsNullOrEmpty(n) && n.Length >= 3).Distinct())
                text = Regex.Replace(text, Regex.Escape(name), "<steam name>", RegexOptions.IgnoreCase);
            foreach (var name in last.Names.Where(n => !string.IsNullOrEmpty(n) && n.Length >= 3).Distinct().OrderByDescending(n => n.Length))
                text = Regex.Replace(text, Regex.Escape(name), "<player>", RegexOptions.IgnoreCase);
            return text;
        }

        // The names of the players in your room (Photon's player list), kept for the next report's blanking. True
        // if there's a new one.
        private static bool NoteRoomPlayers()
        {
            bool added = false;
            try
            {
                var photon = Patches.PhotonNetworkType;
                if (photon?.GetMethod("get_playerList", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.Invoke(null, null) is Array players)
                    foreach (var player in players)
                    {
                        var name = player?.GetType().GetMethod("get_name")?.Invoke(player, null) as string;
                        if (string.IsNullOrEmpty(name)) continue;
                        name = name.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();
                        if (name.Length >= 3 && name != steamName && seenNames.Add(name)) added = true;
                    }
            }
            catch { /* not connected yet */ }
            return added;
        }

        // ---- Helpers ----------------------------------------------------------------------------------------

        private static string EngineCopy(string sessionLog) => Path.GetFileNameWithoutExtension(sessionLog) + ".unity.txt";

        // The engine rewrites output_log.txt at start, before any mod loads, and it holds what a crash needs most:
        // so it's copied as it grows (a background thread; the last quarter second before a hard crash can be lost).
        private static void FollowEngineLog(string source, string copy)
        {
            new Thread(() =>
            {
                try
                {
                    using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var to = new FileStream(copy, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    {
                        var buffer = new byte[64 * 1024];
                        while (true)
                        {
                            int n;
                            while ((n = from.Read(buffer, 0, buffer.Length)) > 0) to.Write(buffer, 0, n);
                            to.Flush();
                            Thread.Sleep(250);
                        }
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogInfo("Crash report: stopped copying the engine's log: " + e.Message);
                }
            }) { IsBackground = true, Name = "Engine log copy" }.Start();
        }

        private static bool? wine;

        private static bool UnderWine()
        {
            if (wine == null)
            {
                try
                {
                    using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Software\Wine")) wine = key != null;
                }
                catch { wine = false; }
            }
            return wine.Value;
        }

        // Logs are often still open for writing: read them shared. Null if missing or unreadable.
        private static byte[] ReadShared(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var data = new byte[stream.Length];
                    int read = 0;
                    while (read < data.Length)
                    {
                        int n = stream.Read(data, read, data.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read < data.Length) Array.Resize(ref data, read);
                    return data;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogInfo($"Crash report: couldn't read {Path.GetFileName(path)}: {e.Message}");
                return null;
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* next time */ }
        }

        /// <summary>This run's log: every BepInEx log event, written and flushed as it comes.</summary>
        private sealed class SessionLog : ILogListener
        {
            private readonly object gate = new object();
            private StreamWriter writer;

            public SessionLog(string path)
            {
                writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true };
                writer.WriteLine($"Bridge Crew Revival {Plugin.Version}, started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            }

            public void LogEvent(object sender, LogEventArgs e)
            {
                lock (gate)
                {
                    try { writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{e.Level,-7}:{e.Source.SourceName,10}] {e.Data}"); }
                    catch { /* disk trouble: the game goes on */ }
                }
            }

            public void Dispose()
            {
                lock (gate)
                {
                    writer?.Dispose();
                    writer = null;
                }
            }
        }
    }
}
