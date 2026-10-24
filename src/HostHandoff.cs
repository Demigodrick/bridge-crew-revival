using System;
using System.Collections;
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
    ///     the others point that room at the next host and join it. Retried for a minute or so.
    /// </summary>
    internal static class HostHandoff
    {
        internal const string SteamIdKey = "SID";
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;
        private const int MaxAttempts = 6;
        private const float RetrySeconds = 12f;

        private sealed class Crew
        {
            public string Room;
            public PHashtable Properties;
            public ulong Host, Successor;
            public string SuccessorName;
            public float Seen;
        }

        private static Crew last;      // the crew we're in (someone else hosting), as of Seen
        private static Crew regroup;   // a regroup in progress
        private static int attempts;
        private static float nextAttempt, nextCheck;
        private static bool reserved;

        private static bool IAmNext => regroup != null && regroup.Successor == SteamMode.MySteamId;

        /// <summary>From SteamMode: the link to this host closed from their end.</summary>
        internal static void HostLinkLost(ulong host)
        {
            var crew = last;
            if (regroup != null || crew == null || crew.Host != host || Time.realtimeSinceStartup - crew.Seen > 5f) return;
            if (crew.Successor == 0)
            {
                Plugin.Log.LogInfo("Host hand-off: the host dropped, but nobody's left to take over");
                return;
            }
            regroup = crew;
            last = null;
            attempts = 0;
            reserved = false;
            nextAttempt = Time.realtimeSinceStartup + (IAmNext ? 0f : 5f); // give the next host a head start
            Plugin.Log.LogInfo($"Host hand-off: host {host} dropped from {crew.Room}; next host {crew.SuccessorName} ({crew.Successor}){(IAmNext ? " = us" : "")}");
            RoomBrowser.Show(IAmNext
                ? "The host dropped. You're the new host: regrouping the crew in the briefing room…"
                : $"The host dropped. Regrouping with {crew.SuccessorName} as the new host…");
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

            ulong successor = 0;
            string successorName = null;
            int best = int.MaxValue;
            if (photon.GetMethod("get_playerList", Static)?.Invoke(null, null) is Array players)
                foreach (var p in players)
                {
                    if (p == null || !ulong.TryParse(Props(p)?[SteamIdKey] as string, out var sid) || sid == SteamMode.CurrentHost) continue;
                    int actor = Get(p, "get_ID") is int id ? id : int.MaxValue;
                    if (actor >= best) continue;
                    best = actor;
                    successor = sid;
                    successorName = Get(p, "get_name") as string;
                }

            last = new Crew
            {
                Room = name, Properties = props, Host = SteamMode.CurrentHost,
                Successor = successor, SuccessorName = string.IsNullOrEmpty(successorName) ? "another player" : successorName,
                Seen = Time.realtimeSinceStartup,
            };
        }

        // Back at the main menu after the drop: rebuild the room (next host) or join it (everyone else).
        private static void Regroup()
        {
            var phase = GameState.Phase();
            if (phase == null || phase == "GamePhase_Intro" || phase == "GamePhase_Loading") return;
            if (!GameState.ConnectedAndReady() || GameState.Loading() || Time.realtimeSinceStartup < nextAttempt) return;

            if (attempts >= MaxAttempts)
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
                if (!reserved)
                {
                    reserved = true;
                    bool made = SteamMode.Server.ReserveRoom(regroup.Room, regroup.Properties);
                    Plugin.Log.LogInfo($"Host hand-off: {(made ? "recreated" : "already have")} {regroup.Room} on our server");
                }
            }
            else
            {
                SteamMode.RedirectRoom(regroup.Room, regroup.Successor);
            }
            Plugin.Log.LogInfo($"Host hand-off: joining {regroup.Room} (attempt {attempts})");
            RoomBrowser.Join(regroup.Room);
        }

        private static object Get(object target, string getter) => target.GetType().GetMethod(getter, All)?.Invoke(target, null);

        private static IDictionary Props(object owner) => Get(owner, "get_customProperties") as IDictionary;
    }
}
