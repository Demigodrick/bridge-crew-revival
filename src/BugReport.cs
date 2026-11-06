using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// [Debug] BugReportKey (F10): save what a bug report needs as one zip in the game folder:
    /// BepInEx's log, the game's own log, the mod's and BepInEx's settings, the last Ubisoft import result,
    /// and a summary of versions and where the game was. No save data or personal files.
    /// </summary>
    internal static class BugReport
    {
        private static float lastSaved = -10f;

        internal static void Update()
        {
            if (!Input.GetKeyDown(Plugin.BugReportKey.Value) || Time.realtimeSinceStartup - lastSaved < 3f) return;
            lastSaved = Time.realtimeSinceStartup;
            string message;
            try
            {
                var name = $"BridgeCrewRevival-report-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
                var path = Path.Combine(Paths.GameRootPath, name);
                Save(path);
                Plugin.Log.LogInfo($"Bug report saved: {path}");
                message = $"Bug report saved in the game folder:\n{name}\n\nAttach it to your report on GitHub.";
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Bug report failed: " + e);
                message = "Couldn't save the bug report: " + e.Message;
            }
            RoomBrowser.Show(message.Replace("\n\n", " ").Replace("\n", " "));
            try { UI.PopupMgr.ShowPopup(message.Replace('|', ' '), null, UI.PDHCCBFJODN.Notification, true); }
            catch { /* not in a scene with popups */ }
        }

        private static void Save(string zipPath) => File.WriteAllBytes(zipPath, Build());

        /// <summary>The report as zip bytes (also what a crewmate sends when asked for their log, CrewLogs).</summary>
        internal static byte[] Build()
        {
            var files = new List<KeyValuePair<string, byte[]>>
            {
                Entry("summary.txt", Encoding.UTF8.GetBytes(Summary())),
            };
            void Add(string name, string path)
            {
                if (!File.Exists(path)) return;
                // BepInEx and Unity keep their logs open for writing.
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
                    files.Add(Entry(name, data));
                }
            }
            Add("BepInEx-LogOutput.log", Path.Combine(Paths.BepInExRootPath, "LogOutput.log"));
            Add("game-output_log.txt", Path.Combine(Path.Combine(Paths.GameRootPath, "stbc_Data"), "output_log.txt"));
            Add("stbc.revival.cfg", Path.Combine(Paths.ConfigPath, "stbc.revival.cfg"));
            Add("BepInEx.cfg", Path.Combine(Paths.ConfigPath, "BepInEx.cfg"));
            Add("stbc.revival.import.txt", Path.Combine(Paths.ConfigPath, "stbc.revival.import.txt"));
            return Zip.Write(files);
        }

        private static KeyValuePair<string, byte[]> Entry(string name, byte[] data) => new KeyValuePair<string, byte[]>(name, data);

        private static string Summary()
        {
            var s = new StringBuilder();
            s.AppendLine($"Bridge Crew Revival {Plugin.Version}");
            s.AppendLine($"Saved: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({DateTime.UtcNow:HH:mm} UTC)");
            s.AppendLine($"Unity {Application.unityVersion}, {SystemInfo.operatingSystem}");
            s.AppendLine($"Mode: {Plugin.Mode.Value}");
            try
            {
                s.AppendLine($"Phase: {GameState.Phase()}, loading: {GameState.Loading()}, connected: {GameState.ConnectedAndReady()}");
                var room = GameState.RoomName();
                if (room != null)
                {
                    GameState.RoomCounts(out int players, out int max, out bool open);
                    s.AppendLine($"Room: {room}, {players}/{max}, open: {open}, hosting: {SteamMode.Enabled && SteamMode.Server?.HostedRooms().Exists(r => r.Name == room) == true}");
                }
            }
            catch (Exception e)
            {
                s.AppendLine("Game state unavailable: " + e.Message);
            }
            s.AppendLine($"Time running: {Time.realtimeSinceStartup / 60f:0.0} min");
            return s.ToString();
        }

        /// <summary>Minimal zip writer: .NET 3.5 has no zip support. Entries are deflated where this game's Mono can
        /// (its DeflateStream needs a native helper that isn't always there), else stored.</summary>
        internal static class Zip
        {
            private static readonly uint[] CrcTable = BuildTable();
            private static bool deflateFailed;

            internal static byte[] Write(List<KeyValuePair<string, byte[]>> files)
            {
                var stream = new MemoryStream();
                using (var w = new BinaryWriter(stream))
                {
                    var central = new MemoryStream();
                    var c = new BinaryWriter(central);
                    var now = DateTime.Now;
                    ushort time = (ushort)((now.Hour << 11) | (now.Minute << 5) | (now.Second / 2));
                    ushort date = (ushort)(((now.Year - 1980) << 9) | (now.Month << 5) | now.Day);
                    foreach (var file in files)
                    {
                        var name = Encoding.UTF8.GetBytes(file.Key);
                        uint crc = Crc(file.Value), offset = (uint)stream.Position, size = (uint)file.Value.Length;
                        var data = Deflate(file.Value);
                        ushort method = (ushort)(data != null ? 8 : 0);
                        data = data ?? file.Value;
                        uint packed = (uint)data.Length;
                        // Local file header.
                        w.Write(0x04034b50u); w.Write((ushort)20); w.Write((ushort)0x0800); w.Write(method);
                        w.Write(time); w.Write(date); w.Write(crc); w.Write(packed); w.Write(size);
                        w.Write((ushort)name.Length); w.Write((ushort)0); w.Write(name); w.Write(data);
                        // Central directory entry.
                        c.Write(0x02014b50u); c.Write((ushort)20); c.Write((ushort)20); c.Write((ushort)0x0800); c.Write(method);
                        c.Write(time); c.Write(date); c.Write(crc); c.Write(packed); c.Write(size);
                        c.Write((ushort)name.Length); c.Write((ushort)0); c.Write((ushort)0); c.Write((ushort)0); c.Write((ushort)0);
                        c.Write(0u); c.Write(offset); c.Write(name);
                    }
                    uint centralOffset = (uint)stream.Position;
                    w.Write(central.ToArray());
                    // End of central directory.
                    w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0);
                    w.Write((ushort)files.Count); w.Write((ushort)files.Count);
                    w.Write((uint)central.Length); w.Write(centralOffset); w.Write((ushort)0);
                }
                return stream.ToArray();
            }

            // Raw deflate (what a zip entry holds), or null to store it as it is.
            private static byte[] Deflate(byte[] data)
            {
                if (deflateFailed || data.Length < 256) return null;
                try
                {
                    var packed = new MemoryStream();
                    using (var deflate = new System.IO.Compression.DeflateStream(packed, System.IO.Compression.CompressionMode.Compress, true))
                        deflate.Write(data, 0, data.Length);
                    return packed.Length < data.Length ? packed.ToArray() : null;
                }
                catch (Exception e)
                {
                    deflateFailed = true;
                    Plugin.Log.LogInfo("Bug report: no compression in this game (" + e.GetType().Name + "); files stored as they are");
                    return null;
                }
            }

            private static uint Crc(byte[] data)
            {
                uint crc = 0xFFFFFFFFu;
                foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
                return ~crc;
            }

            private static uint[] BuildTable()
            {
                var table = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint v = i;
                    for (int k = 0; k < 8; k++) v = (v & 1) != 0 ? 0xEDB88320u ^ (v >> 1) : v >> 1;
                    table[i] = v;
                }
                return table;
            }
        }
    }
}
