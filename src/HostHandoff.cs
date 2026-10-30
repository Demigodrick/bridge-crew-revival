using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using StbcServer;
using StbcServer.Protocol;
using UnityEngine;
using PhotonHashtable = ExitGames.Client.Photon.Hashtable;

namespace StbcRevival
{
    /// <summary>
    /// Steam mode: the host's game is the crew's server, so when the host drops everyone else is thrown out
    /// to the main menu. Instead, the crew regroups in the briefing room under a new host (like the game does
    /// when its captain leaves mid-mission, the mission itself doesn't survive):
    ///   - While in someone else's crew, everyone keeps a note of the room (name and settings) and who would
    ///     host next: the remaining player who joined first. Players' Steam IDs travel in their player
    ///     properties ("SID"), so every game agrees without asking.
    ///   - When the link to the host closes from their end (their game went, or Steam lost them), the next
    ///     host recreates the room on their own in-game server under the same name and settings and joins it;
    ///     the others point that room at the next host and join it, three tries each (about half a minute).
    ///     If the next host never makes the room (they quit too, or their game missed the drop), the one after
    ///     them takes over, and so on down the crew in joining order: every game works out the same line.
    ///   - The game's "Failed to join room" popup stays quiet while regrouping (one message says how it went).
    /// </summary>
    internal static class HostHandoff
    {
        internal const string SteamIdKey = "SID";
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;
        private const int AttemptsEach = 3;    // per next host in line, before the one after them takes over
        private const float RetrySeconds = 12f;

        private sealed class Crew
        {
            public string Room;
            public PHashtable Properties;
            public ulong Host;
            public List<ulong> Line = new List<ulong>(); // who hosts next, in joining order
            public List<string> Names = new List<string>();
            public float Seen;
            public ulong Successor => Line.Count > 0 ? Line[0] : 0;
        }

        private static Crew last;      // the crew we're in (someone else hosting), as of Seen
        private static Crew regroup;   // a regroup in progress
        private static int attempts;
        private static float nextAttempt, nextCheck;
        private static bool reserved;

        private static int candidate; // which of the regroup's line is the next host now

        private static bool IAmNext => regroup != null && candidate < regroup.Line.Count && regroup.Line[candidate] == SteamMode.MySteamId;

        private static string NameOf(int i) => regroup == null || i >= regroup.Names.Count || string.IsNullOrEmpty(regroup.Names[i]) ? "another player" : regroup.Names[i];

        /// <summary>While regrouping: a failed join is expected (the next host may not be ready), no popup.</summary>
        internal static bool Regrouping => regroup != null;

        internal static void Patch(HarmonyLib.Harmony harmony)
        {
            var setError = typeof(SVR.Network.NetworkStatus).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "SetError" && m.GetParameters().Length == 4);
            if (setError == null) Plugin.Log.LogWarning("Host hand-off: NetworkStatus.SetError not found; failed joins while regrouping show a popup");
            else harmony.Patch(setError, prefix: new HarmonyLib.HarmonyMethod(typeof(HostHandoff), nameof(SetError_Prefix)));
        }

        // A join that fails while regrouping (the next host isn't ready, or is being skipped): no popup.
        private static void SetError_Prefix(object[] __args)
        {
            if (!Regrouping || __args[0]?.ToString() != "RoomJoinFailed" || __args[1] == null) return;
            __args[1] = Enum.Parse(__args[1].GetType(), "None");
        }

        /// <summary>From SteamMode: the link to this host closed from their end.</summary>
        internal static void HostLinkLost(ulong host)
        {
            var crew = last;
            if (regroup != null || crew == null || crew.Host != host || Time.realtimeSinceStartup - crew.Seen > 30f) return; // 30 s: a long level load stops the once-a-second note
            if (crew.Successor == 0)
            {
                Plugin.Log.LogInfo("Host hand-off: the host dropped, but nobody's left to take over");
                return;
            }
            regroup = crew;
            last = null;
            attempts = 0;
            candidate = 0;
            reserved = false;
            nextAttempt = Time.realtimeSinceStartup + (IAmNext ? 0f : 5f); // give the next host a head start
            Plugin.Log.LogInfo($"Host hand-off: host {host} dropped from {crew.Room}; hosts in line: {string.Join(", ", crew.Names.ToArray())}{(IAmNext ? " (us first)" : "")}");
            RoomBrowser.Show(IAmNext
                ? "The host dropped. You're the new host: regrouping the crew in the briefing room…"
                : $"The host dropped. Regrouping with {NameOf(0)} as the new host…");
        }

        /// <summary>Our Steam ID in our player properties now (out of a room: kept locally, sent with the join).</summary>
        internal static void PublishOwn()
        {
            try
            {
                if (!SteamMode.Enabled || SteamMode.MySteamId == 0) return;
                var me = Patches.PhotonNetworkType?.GetMethod("get_player", Static)?.Invoke(null, null);
                string mine = SteamMode.MySteamId.ToString();
                if (me != null && !Equals(Props(me)?[SteamIdKey], mine)) PhotonProps.Set(me, SteamIdKey, mine);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Host hand-off: " + e.Message);
            }
        }

        internal static void Update()
        {
            if (!SteamMode.Enabled || SteamMode.MySteamId == 0 || Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 1f;
            var photon = Patches.PhotonNetworkType;
            if (photon == null) return;

            // Our Steam ID in our player properties, so the crew knows who's who.
            var me = photon.GetMethod("get_player", Static)?.Invoke(null, null);
            string mine = SteamMode.MySteamId.ToString();
            if (me != null && !Equals(Props(me)?[SteamIdKey], mine))
                PhotonProps.Set(me, SteamIdKey, mine);

            var room = photon.GetMethod("get_room", Static)?.Invoke(null, null);
            if (room != null)
            {
                var name = Get(room, "get_name") as string;
                if (regroup != null && name == regroup.Room)
                {
                    Plugin.Log.LogInfo($"Host hand-off: regrouped in {name}");
                    RoomBrowser.Show("Crew regrouped in the briefing room.");
                    regroup = null;
                }
                if (SteamMode.CurrentHost != SteamMode.MySteamId) Note(photon, room, name);
                else last = null;
                return;
            }
            if (regroup != null) Regroup();
        }

        // The crew as it is now: the room's settings and who would host next.
        private static void Note(Type photon, object room, string name)
        {
            var props = new PHashtable
            {
                [GameProp.MaxPlayers] = Convert.ToByte(Get(room, "get_maxPlayers") ?? 4),
                [GameProp.IsOpen] = Get(room, "get_open") as bool? ?? true,
                [GameProp.IsVisible] = Get(room, "get_visible") as bool? ?? true,
            };
            if (Get(room, "get_propertiesListedInLobby") is string[] listed) props[GameProp.PropsListedInLobby] = listed;
            if (Props(room) is IDictionary custom)
                foreach (DictionaryEntry kv in custom)
                    if (kv.Key is string key) props[key] = kv.Value;

            // Everyone but the host, in joining order (Photon actor number): the line of next hosts.
            var line = new List<KeyValuePair<int, object>>(); // actor number, player
            if (photon.GetMethod("get_playerList", Static)?.Invoke(null, null) is Array players)
                foreach (var p in players)
                {
                    if (p == null || !ulong.TryParse(Props(p)?[SteamIdKey] as string, out var sid) || sid == SteamMode.CurrentHost) continue;
                    line.Add(new KeyValuePair<int, object>(Get(p, "get_ID") is int id ? id : int.MaxValue, p));
                }
            line.Sort((a, b) => a.Key.CompareTo(b.Key));

            var crew = new Crew { Room = name, Properties = props, Host = SteamMode.CurrentHost, Seen = Time.realtimeSinceStartup };
            foreach (var next in line)
            {
                crew.Line.Add(ulong.Parse((string)Props(next.Value)[SteamIdKey]));
                crew.Names.Add(Get(next.Value, "get_name") as string);
            }
            last = crew;
        }

        // Back at the main menu after the drop: rebuild the room (next host) or join it (everyone else).
        private static void Regroup()
        {
            var phase = GameState.Phase();
            if (phase == null || phase == "GamePhase_Intro" || phase == "GamePhase_Loading") return;
            if (!GameState.ConnectedAndReady() || GameState.Loading() || Time.realtimeSinceStartup < nextAttempt) return;

            // This next host had their tries: the one after them takes over (we hand our own room back if it was us).
            if (attempts > 0 && attempts % AttemptsEach == 0 && attempts / AttemptsEach > candidate)
            {
                if (reserved) SteamMode.Server.ReleaseRoom(regroup.Room);
                reserved = false;
                candidate = attempts / AttemptsEach;
                if (candidate < regroup.Line.Count)
                {
                    Plugin.Log.LogInfo($"Host hand-off: {NameOf(candidate - 1)} didn't take over; next host {NameOf(candidate)}{(IAmNext ? " = us" : "")}");
                    RoomBrowser.Show(IAmNext
                        ? $"{NameOf(candidate - 1)} didn't take over. You're the new host: regrouping the crew…"
                        : $"{NameOf(candidate - 1)} didn't take over. Regrouping with {NameOf(candidate)}…");
                }
            }
            if (candidate >= regroup.Line.Count)
            {
                Plugin.Log.LogWarning($"Host hand-off: gave up regrouping in {regroup.Room}");
                if (reserved) SteamMode.Server.ReleaseRoom(regroup.Room);
                RoomBrowser.Show("Couldn't regroup the crew after the host dropped. Host or join a crew as usual.");
                regroup = null;
                return;
            }
            attempts++;
            nextAttempt = Time.realtimeSinceStartup + RetrySeconds;

            if (IAmNext)
            {
                // Every attempt: the server drops an empty room once its last player leaves, so a failed join
                // could otherwise leave nothing to join.
                reserved = true;
                bool made = SteamMode.Server.ReserveRoom(regroup.Room, regroup.Properties);
                Plugin.Log.LogInfo($"Host hand-off: {(made ? "recreated" : "already have")} {regroup.Room} on our server");
            }
            else
            {
                SteamMode.RedirectRoom(regroup.Room, regroup.Line[candidate]);
            }
            Plugin.Log.LogInfo($"Host hand-off: joining {regroup.Room} (attempt {attempts})");
            RoomBrowser.Join(regroup.Room);
        }

        private static object Get(object target, string getter) => target.GetType().GetMethod(getter, All)?.Invoke(target, null);

        private static IDictionary Props(object owner) => Get(owner, "get_customProperties") as IDictionary;
    }
}
