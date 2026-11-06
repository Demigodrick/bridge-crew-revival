using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// [Debug] CrewLogsKey (F11): ask your crewmates for their logs. Each crewmate with the mod is asked in a
    /// popup whether to send theirs; if they agree, their game sends you its bug report (the zip F10 saves:
    /// BepInEx's and the game's logs, the mod's and BepInEx's settings, a summary) over the game's own
    /// connection, to you alone, and it's saved in the game folder's CrewLogs folder. Crewmates without the
    /// mod are never asked; who didn't answer is logged. Photon events 150-152, which the game doesn't use
    /// (its own handlers look only at their own codes); sent a piece at a time, so the crew's voice and game
    /// data keep moving while it does.
    /// </summary>
    internal static class CrewLogs
    {
        private const byte AskEvent = 150, PartEvent = 151, RefusedEvent = 152;
        private const int PartBytes = 32 * 1024;
        private const float PartEvery = 0.1f, AnswerWait = 120f;
        private const int MaxParts = 16 * 1024 * 1024 / PartBytes; // a log of up to 16 MB

        private sealed class Incoming
        {
            public string Name;
            public byte[][] Parts;
            public int Received;
            public float Started;
        }

        // Asking: crewmates asked and not heard from yet (actor number → name), and logs on the way.
        private static readonly Dictionary<int, string> waiting = new Dictionary<int, string>();
        private static readonly HashSet<int> asked = new HashSet<int>(); // who may send you a log: those asked, until it's in or they said no
        private static readonly Dictionary<int, Incoming> incoming = new Dictionary<int, Incoming>();
        private static float askedAt = -1f;

        // Sending: your log, a piece at a time, to whoever asked.
        private static readonly Queue<object[]> outgoing = new Queue<object[]>();
        private static int sendTo;
        private static float nextPart;
        private static bool askOpen; // the popup asking you is showing

        private static bool hooked;

        internal static void Patch()
        {
            NDIOGIGMPBH.OnEventCall += OnEvent;
            hooked = true;
        }

        internal static void Update()
        {
            if (!hooked) return;
            if (Input.GetKeyDown(Plugin.CrewLogsKey.Value)) Ask();
            float now = Time.realtimeSinceStartup;
            if (outgoing.Count > 0 && now >= nextPart)
            {
                nextPart = now + PartEvery;
                Send(PartEvent, outgoing.Dequeue(), sendTo);
                if (outgoing.Count == 0) Plugin.Log.LogInfo($"Crew logs: your log sent to {NameOf(sendTo)}");
            }
            if (askedAt >= 0f && now - askedAt > AnswerWait)
            {
                askedAt = -1f;
                foreach (var kv in waiting)
                    Plugin.Log.LogInfo($"Crew logs: no answer from {kv.Value} (without the mod, or a version without this, they weren't asked)");
                waiting.Clear();
                foreach (var kv in incoming.Where(kv => now - kv.Value.Started > AnswerWait).ToList())
                {
                    Plugin.Log.LogWarning($"Crew logs: {kv.Value.Name}'s log stopped coming ({kv.Value.Received} of {kv.Value.Parts.Length} pieces)");
                    incoming.Remove(kv.Key);
                }
                asked.RemoveWhere(actor => !incoming.ContainsKey(actor)); // the request has closed
            }
        }

        // ---- Asking ---------------------------------------------------------------------------------

        private static void Ask()
        {
            var others = Others();
            if (others.Count == 0)
            {
                Notify("There's no one in your crew to ask for a log.");
                return;
            }
            waiting.Clear();
            foreach (var p in others)
            {
                waiting[Id(p)] = Name(p);
                asked.Add(Id(p));
            }
            askedAt = Time.realtimeSinceStartup;
            if (!Send(AskEvent, Plugin.Version, others.Select(Id).ToArray()))
            {
                Notify("Couldn't ask your crew for their logs.");
                return;
            }
            Plugin.Log.LogInfo("Crew logs: asked " + string.Join(", ", others.Select(Name).ToArray()));
            Notify($"Asked {others.Count} crewmate{(others.Count == 1 ? "" : "s")} for their logs. They arrive in the game folder's CrewLogs folder.");
        }

        private static void OnEvent(byte code, object content, int sender)
        {
            try
            {
                switch (code)
                {
                    case AskEvent: Asked(sender); break;
                    case PartEvent: Received(sender, content as object[]); break;
                    case RefusedEvent:
                        if (!asked.Remove(sender)) break;
                        waiting.Remove(sender);
                        Plugin.Log.LogInfo($"Crew logs: {NameOf(sender)} said no");
                        break;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Crew logs: " + e.Message);
            }
        }

        private static void Received(int sender, object[] part)
        {
            // Only from a crewmate you asked, and only a log of a sane size: anything else is ignored.
            if (!asked.Contains(sender)) return;
            if (part == null || part.Length != 3 || !(part[0] is int index) || !(part[1] is int count) || !(part[2] is byte[] data)) return;
            if (count <= 0 || count > MaxParts || index < 0 || index >= count || data.Length == 0 || data.Length > PartBytes) return;
            if (!incoming.TryGetValue(sender, out var log) || log.Parts.Length != count)
            {
                incoming[sender] = log = new Incoming { Name = NameOf(sender), Parts = new byte[count][], Started = Time.realtimeSinceStartup };
                waiting.Remove(sender);
                Plugin.Log.LogInfo($"Crew logs: {log.Name}'s log coming ({count} pieces)");
            }
            if (log.Parts[index] == null) log.Received++;
            log.Parts[index] = data;
            if (log.Received < count) return;
            incoming.Remove(sender);
            asked.Remove(sender);
            var folder = Path.Combine(Paths.GameRootPath, "CrewLogs");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"{FileName(log.Name)}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write))
                foreach (var p in log.Parts) file.Write(p, 0, p.Length);
            Plugin.Log.LogInfo($"Crew logs: {log.Name}'s log saved: {path} ({new FileInfo(path).Length / 1024} KB)");
            Notify($"Got {log.Name}'s log: CrewLogs\\{Path.GetFileName(path)}");
        }

        // ---- Being asked ------------------------------------------------------------------------------

        private static void Asked(int sender)
        {
            string name = NameOf(sender);
            if (askOpen || outgoing.Count > 0)
            {
                Plugin.Log.LogInfo($"Crew logs: {name} asked for your log while you were already answering or sending");
                return;
            }
            Plugin.Log.LogInfo($"Crew logs: {name} asked for your log");
            askOpen = true;
            try
            {
                UI.PopupMgr.ShowPopup(Translation.T($"{name} is asking for your game log (the mod's and the game's logs and settings) to look into a problem. Send it to them?").Replace('|', ' '),
                    new UI.LGNBEBILNCL(yes =>
                    {
                        askOpen = false;
                        if (yes) SendLog(sender);
                        else
                        {
                            Send(RefusedEvent, null, sender);
                            Plugin.Log.LogInfo($"Crew logs: you didn't send {name} your log");
                        }
                    }), UI.PDHCCBFJODN.Question, true);
            }
            catch (Exception e)
            {
                askOpen = false;
                Send(RefusedEvent, null, sender);
                Plugin.Log.LogWarning($"Crew logs: couldn't ask you about {name}'s request ({e.Message}); nothing sent");
            }
        }

        private static void SendLog(int to)
        {
            byte[] zip;
            try { zip = BugReport.Build(); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Crew logs: couldn't put your log together: " + e.Message);
                Send(RefusedEvent, null, to);
                return;
            }
            int count = (zip.Length + PartBytes - 1) / PartBytes;
            outgoing.Clear();
            for (int i = 0; i < count; i++)
            {
                int n = Math.Min(PartBytes, zip.Length - i * PartBytes);
                var data = new byte[n];
                Buffer.BlockCopy(zip, i * PartBytes, data, 0, n);
                outgoing.Enqueue(new object[] { i, count, data });
            }
            sendTo = to;
            Plugin.Log.LogInfo($"Crew logs: sending your log to {NameOf(to)} ({zip.Length / 1024} KB, {count} pieces)");
        }

        // ---- Photon -----------------------------------------------------------------------------------

        private static bool Send(byte code, object content, params int[] to)
        {
            try
            {
                return NDIOGIGMPBH.RaiseEvent(code, content, true, new CGJCFMCOBMJ { TargetActors = to });
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Crew logs: sending: {e.Message}");
                return false;
            }
        }

        // PhotonNetwork's player lists and PhotonPlayer's ID and name (their properties' names are obfuscated).
        private static CBBCCGMALMP[] Players(string list) =>
            typeof(NDIOGIGMPBH).GetMethod(list, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.Invoke(null, null) as CBBCCGMALMP[]
            ?? new CBBCCGMALMP[0];

        private static int Id(CBBCCGMALMP player) => player.KLNDGCGHADM; // ID

        private static string Name(CBBCCGMALMP player) => player.GPICMMEIAPM; // name

        private static List<CBBCCGMALMP> Others()
        {
            try { return Players("get_otherPlayers").Where(p => p != null).ToList(); }
            catch { return new List<CBBCCGMALMP>(); }
        }

        private static string NameOf(int actor)
        {
            try
            {
                var player = Players("get_playerList").FirstOrDefault(p => p != null && Id(p) == actor);
                if (player != null && !string.IsNullOrEmpty(Name(player))) return Name(player);
            }
            catch { }
            return waiting.TryGetValue(actor, out var name) ? name : $"player {actor}";
        }

        private static string FileName(string name)
        {
            var bad = Path.GetInvalidFileNameChars();
            var safe = new string(name.Select(c => bad.Contains(c) || c == ' ' ? '_' : c).ToArray()).Trim('_', '.');
            return safe.Length == 0 ? "crewmate" : safe.Length > 40 ? safe.Substring(0, 40) : safe;
        }

        private static void Notify(string text)
        {
            Plugin.Log.LogInfo("Crew logs: " + text);
            try { UI.PopupMgr.ShowPopup(Translation.T(text).Replace('|', ' '), null, UI.PDHCCBFJODN.Notification, true); }
            catch { /* no popups here */ }
        }
    }
}
