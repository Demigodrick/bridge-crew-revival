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
    /// Steam mode: the host's game is the crew's server, so when the link to it goes (the host's game went, or only
    /// this player's link broke) the game takes it for a lost internet connection: "Bridge Crew services are
    /// unreachable... Error Code: 21", back through the intro, and a Retry button to press. Instead:
    ///   - The message says what happened ("Lost the connection to NAME's game. Rejoining the crew…"), and the
    ///     intro's error screen goes on by itself after a few seconds, so the game reconnects to the menu unattended.
    ///   - Back at the menu it first rejoins the same host (twice, about half a minute): when only this player's
    ///     link broke, they're back with the crew, in the mission if it's still on.
    /// When the host is gone, the crew regroups in the briefing room under a new host (like the game does when its
    /// captain leaves mid-mission, the mission itself doesn't survive):
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
        private const int HostAttempts = 2;    // the same host first: only our link may have broken
        private const float RetrySeconds = 12f;
        private const float ErrorScreenSeconds = 4f; // the intro's error screen, read, then on by itself

        private sealed class Crew
        {
            public string Room;
            public PHashtable Properties;
            public ulong Host;
            public string HostName;
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

        private static int candidate; // which of the regroup's line is the next host now; -1: the same host again
        private static int tries;     // joins tried with this candidate

        private static bool IAmNext => regroup != null && candidate >= 0 && candidate < regroup.Line.Count && regroup.Line[candidate] == SteamMode.MySteamId;

        private static string HostName => string.IsNullOrEmpty(regroup?.HostName) ? "the host" : regroup.HostName;

        private static string NameOf(int i) => regroup == null || i >= regroup.Names.Count || string.IsNullOrEmpty(regroup.Names[i]) ? "another player" : regroup.Names[i];

        /// <summary>While regrouping: a failed join is expected (the next host may not be ready), no popup.</summary>
        internal static bool Regrouping => regroup != null;

        internal static void Patch(HarmonyLib.Harmony harmony)
        {
            var setError = typeof(SVR.Network.NetworkStatus).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "SetError" && m.GetParameters().Length == 4);
            if (setError == null) Plugin.Log.LogWarning("Host hand-off: NetworkStatus.SetError not found; failed joins while regrouping show a popup");
            else harmony.Patch(setError, prefix: new HarmonyLib.HarmonyMethod(typeof(HostHandoff), nameof(SetError_Prefix)));

            // The game's "lost connection" (FailWithError, from an unexpected disconnect): our own words when it was the
            // link to another player's game, and the regroup (or rejoin) started.
            var fail = HarmonyLib.AccessTools.Method(typeof(GPMTransitionManager), "HOOLAEHPPKI");
            if (fail == null) Plugin.Log.LogWarning("Host hand-off: GPMTransitionManager.FailWithError not found; a lost host shows the game's internet error");
            else harmony.Patch(fail, prefix: new HarmonyLib.HarmonyMethod(typeof(HostHandoff), nameof(FailWithError_Prefix)));
            // The intro's error screen: on by itself after a few seconds for that error (no Retry to press).
            var errorScreen = HarmonyLib.AccessTools.Method(typeof(NetErrorIntroElement), "Update");
            if (errorScreen == null) Plugin.Log.LogWarning("Host hand-off: NetErrorIntroElement.Update not found; the lost-host error waits for Retry");
            else harmony.Patch(errorScreen, postfix: new HarmonyLib.HarmonyMethod(typeof(HostHandoff), nameof(ErrorScreen_Postfix)));
        }

        private static readonly MethodInfo SetMessage = HarmonyLib.AccessTools.Method(typeof(SVR.Network.PAFNPDODDCM), "KBNGALKDCHL");
        private static readonly HarmonyLib.AccessTools.FieldRef<NetErrorIntroElement, SVR.Network.PAFNPDODDCM> ShownError =
            HarmonyLib.AccessTools.FieldRefAccess<NetErrorIntroElement, SVR.Network.PAFNPDODDCM>("CCPDMKAHEDO");
        private static readonly MethodInfo GetErrorId = HarmonyLib.AccessTools.Method(typeof(SVR.Network.PAFNPDODDCM), "get_ErrorId");
        private static int ErrorId(SVR.Network.PAFNPDODDCM error) => GetErrorId?.Invoke(error, null) is int id ? id : -2;
        private static int ourErrorId = -1;   // the error whose words are ours (its screen goes on by itself)
        private static float errorShownAt = -1f;

        // Lost connection while in another player's crew: their game was our server, so it's the link to them, not
        // the internet. Our words for it, and the rejoin started (if the link's close didn't start it already).
        private static void FailWithError_Prefix(SVR.Network.PAFNPDODDCM __0)
        {
            try
            {
                if (__0 == null || !SteamMode.Enabled || __0.MJBLDGAMJPF != SVR.Network.PAFNPDODDCM.JEPEEEDHDLL.LostConnectionToPhoton) return;
                var crew = regroup ?? last;
                if (crew == null || Time.realtimeSinceStartup - crew.Seen > 90f) return; // not in someone else's crew lately
                if (regroup == null) Start(crew, "the link to them timed out");
                string name = string.IsNullOrEmpty(crew.HostName) ? "the host" : crew.HostName;
                SetMessage?.Invoke(__0, new object[] { $"Lost the connection to {name}'s game. Rejoining the crew…" });
                ourErrorId = ErrorId(__0);
                errorShownAt = -1f;
                Plugin.Log.LogInfo($"Host hand-off: the game's lost-connection error (id {ourErrorId}) reworded; its screen goes on by itself");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Host hand-off: rewording the lost-connection error: " + e.Message); }
        }

        // The intro's error screen showing that error: on after a few seconds, as if Retry was pressed.
        private static void ErrorScreen_Postfix(NetErrorIntroElement __instance)
        {
            if (ourErrorId < 0) return;
            try
            {
                var shown = ShownError(__instance);
                if (shown == null || ErrorId(shown) != ourErrorId) return;
                if (errorShownAt < 0f) errorShownAt = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup - errorShownAt < ErrorScreenSeconds) return;
                ourErrorId = -1;
                Plugin.Log.LogInfo("Host hand-off: the error screen goes on by itself");
                __instance.OnReconnect();
            }
            catch (Exception e) { ourErrorId = -1; Plugin.Log.LogWarning("Host hand-off: the error screen: " + e.Message); }
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
            Start(crew, "the link closed");
        }

        // The link to the host is gone: rejoin them first (only our link may have broken), then the line of next hosts.
        private static void Start(Crew crew, string how)
        {
            regroup = crew;
            last = null;
            attempts = 0;
            tries = 0;
            candidate = -1;
            reserved = false;
            nextAttempt = Time.realtimeSinceStartup;
            Plugin.Log.LogInfo($"Host hand-off: lost {HostName} ({crew.Host}) in {crew.Room}: {how}; rejoining them first, then hosts in line: {(crew.Names.Count == 0 ? "none" : string.Join(", ", crew.Names.ToArray()))}");
            RoomBrowser.Show($"Lost the connection to {HostName}'s game. Rejoining the crew…");
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
                    bool same = candidate < 0;
                    Plugin.Log.LogInfo(same ? $"Host hand-off: rejoined {HostName} in {name}" : $"Host hand-off: regrouped in {name}");
                    RoomBrowser.Show(same ? $"Rejoined {HostName}'s crew." : "Crew regrouped in the briefing room.");
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
            if (photon.GetMethod("get_playerList", Static)?.Invoke(null, null) is Array everyone)
                foreach (var p in everyone)
                    if (p != null && ulong.TryParse(Props(p)?[SteamIdKey] as string, out var sid) && sid == crew.Host) crew.HostName = Get(p, "get_name") as string;
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

            // This one had their tries: the next takes over (we hand our own room back if it was us). The same host first,
            // then the line in joining order; every game in the crew goes through it the same way.
            if (tries >= (candidate < 0 ? HostAttempts : AttemptsEach))
            {
                if (reserved) SteamMode.Server.ReleaseRoom(regroup.Room);
                reserved = false;
                candidate++;
                tries = 0;
                if (candidate < regroup.Line.Count)
                {
                    string gone = candidate == 0 ? $"{HostName} isn't back" : $"{NameOf(candidate - 1)} didn't take over";
                    Plugin.Log.LogInfo($"Host hand-off: {gone}; next host {NameOf(candidate)}{(IAmNext ? " = us" : "")}");
                    RoomBrowser.Show(IAmNext
                        ? $"{gone}. You're the new host: regrouping the crew in the briefing room…"
                        : $"{gone}. Regrouping with {NameOf(candidate)} as the new host…");
                    if (!IAmNext) { nextAttempt = Time.realtimeSinceStartup + 5f; return; } // give the next host a head start
                }
            }
            if (candidate >= regroup.Line.Count)
            {
                Plugin.Log.LogWarning($"Host hand-off: gave up regrouping in {regroup.Room}");
                if (reserved) SteamMode.Server.ReleaseRoom(regroup.Room);
                RoomBrowser.Show(regroup.Line.Count == 0
                    ? $"Couldn't get back to {HostName}'s game. Host or join a crew as usual."
                    : "Couldn't regroup the crew after the host dropped. Host or join a crew as usual.");
                regroup = null;
                return;
            }
            attempts++;
            tries++;
            nextAttempt = Time.realtimeSinceStartup + RetrySeconds;

            if (candidate < 0)
            {
                SteamMode.RedirectRoom(regroup.Room, regroup.Host); // the same host's game, if it's still there
            }
            else if (IAmNext)
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
