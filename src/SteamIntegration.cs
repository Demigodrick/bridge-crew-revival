using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using SteamFriends = Steamworks.GJMOCKOJNPH;                       // Steamworks.NET SteamFriends (obfuscated)
using JoinRequested = Steamworks.LKDAFEDNOEI;                     // GameRichPresenceJoinRequested_t (k_iCallback 337)
using JoinCallback = Steamworks.KAFDLCLDHJI<Steamworks.LKDAFEDNOEI>; // Callback<GameRichPresenceJoinRequested_t>

namespace StbcRevival
{
    /// <summary>
    /// Steam friends integration, carrying the room code (Bridge Crew itself never used Steam for this):
    ///  - rich presence: "status" text, and a "connect" string while you're in a joinable room, which makes
    ///    Steam offer "Join Game" and "Invite to Game" for you;
    ///  - joining: Steam's join request (game running) or the connect string on the command line (Steam
    ///    launched the game) queues a join that runs through the same path as the room browser.
    /// Uses the game's bundled Steamworks.NET; the game's SteamManager pumps callbacks every frame.
    /// </summary>
    internal static class SteamIntegration
    {
        internal const string JoinArg = "+stbc_join";
        internal const string ServerArg = "+stbc_server";
        internal const string HostArg = "+stbc_host";
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        private static JoinCallback joinCallback; // keep alive: Steamworks.NET unregisters on finalize
        private static bool initialized;
        private static string pendingJoin;
        private static float nextPresence;
        private static string lastStatus, lastConnect, lastGroup;

        internal static void Update()
        {
            if (!Plugin.SteamPresence.Value) return;
            if (!initialized)
            {
                if (!SteamName.SteamInitialized()) return;
                Initialize();
            }
            TryPendingJoin();
            if (Time.realtimeSinceStartup >= nextPresence)
            {
                nextPresence = Time.realtimeSinceStartup + 3f;
                UpdatePresence();
            }
        }

        private static void Initialize()
        {
            initialized = true;
            try
            {
                joinCallback = JoinCallback.Create(OnJoinRequested);
                if (SteamMode.Enabled)
                    SteamMode.RegisterLobbyJoin((room, host) => HandleConnect($"{JoinArg} {room} {HostArg} {host}"));
                Plugin.Log.LogInfo("Steam: join requests enabled");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Steam: could not register join callback: " + e.Message);
            }

            // Launched by Steam from "Join Game" / an invite while the game wasn't running.
            var args = Environment.GetCommandLineArgs();
            var connect = string.Join(" ", args.SkipWhile(a => a != JoinArg).ToArray());
            if (connect.Length > 0)
            {
                Plugin.Log.LogInfo($"Steam: launched with join request: {connect}");
                HandleConnect(connect);
            }
        }

        private static void OnJoinRequested(JoinRequested request)
        {
            Plugin.Log.LogInfo($"Steam: join request: {request.m_rgchConnect}");
            HandleConnect(request.m_rgchConnect);
        }

        /// <summary>The connect string friends can join you with ("" when not joinable).</summary>
        internal static string CurrentConnect => lastConnect ?? "";

        /// <summary>Join a friend from their rich presence connect string (the in-game friends panel).</summary>
        internal static void JoinFromConnect(string connect) => HandleConnect(connect);

        /// <summary>Parse "+stbc_join eu|K7QX2M [+stbc_server host:port]".</summary>
        private static void HandleConnect(string connect)
        {
            var parts = (connect ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string room = null, server = null;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i] == JoinArg) room = parts[i + 1];
                else if (parts[i] == ServerArg) server = parts[i + 1];
            }
            var host = HostFromConnect(connect);
            if (string.IsNullOrEmpty(room))
            {
                Plugin.Log.LogWarning("Steam: join request without a room code");
                return;
            }
            if (SteamMode.Enabled)
            {
                if (host == null)
                {
                    RoomBrowser.Show($"That invite is from a server-based game ({server ?? "unknown server"}); switch Mode to SelfHosted to join it.");
                    return;
                }
                SteamMode.RememberRoom(room, host.Value);
            }
            else if (server != null && !SameServer(server))
            {
                RoomBrowser.Show($"Your friend is on server {server}, you're on {OurServer()}. " +
                                 $"Set Server in stbc.revival.cfg to {server} and restart, then join {RoomBrowser.Code(room)}.");
                Plugin.Log.LogWarning($"Steam: join request for another server ({server})");
                return;
            }
            pendingJoin = room;
            RoomBrowser.Show($"Joining your friend's room {RoomBrowser.Code(room)}…");
        }

        internal static ulong? HostFromConnect(string connect)
        {
            var parts = (connect ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length - 1; i++)
                if (parts[i] == HostArg && ulong.TryParse(parts[i + 1], out var id))
                    return id;
            return null;
        }

        private static string OurServer() => $"{Plugin.Server.Value.Trim()}:{Plugin.Port.Value}";

        private static bool SameServer(string server)
        {
            var ours = string.IsNullOrEmpty(Plugin.PublicServerAddress.Value) ? OurServer() : Plugin.PublicServerAddress.Value.Trim();
            return string.Equals(server, ours, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(server, OurServer(), StringComparison.OrdinalIgnoreCase);
        }

        // Join only once the game can: connected to Photon, past the intro, not loading a level.
        private static void TryPendingJoin()
        {
            if (pendingJoin == null) return;
            var phase = GameState.Phase();
            if (phase == null || phase == "GamePhase_Intro" || phase == "GamePhase_Loading") return;
            if (!GameState.ConnectedAndReady() || GameState.Loading()) return;
            var room = pendingJoin;
            pendingJoin = null;
            RoomBrowser.Join(room);
        }

        private static void UpdatePresence()
        {
            try
            {
                var room = GameState.RoomName();
                string status, connect = "", group = "", groupSize = "";
                if (room == null)
                {
                    status = GameState.Phase() == "GamePhase_MainMenu" ? "On the shuttle" : "Starting up";
                }
                else
                {
                    GameState.RoomCounts(out var players, out var max, out var open);
                    group = room;
                    groupSize = players.ToString();
                    status = $"In crew {RoomBrowser.Code(room)} · {PhaseText(GameState.Phase())} · {players}/{(max > 0 ? max.ToString() : "any")}";
                    if (open && (max == 0 || players < max))
                    {
                        connect = $"{JoinArg} {room}";
                        if (SteamMode.Enabled)
                        {
                            if (SteamMode.CurrentHost != 0) connect += $" {HostArg} {SteamMode.CurrentHost}";
                        }
                        else if (!string.IsNullOrEmpty(Plugin.PublicServerAddress.Value))
                            connect += $" {ServerArg} {Plugin.PublicServerAddress.Value.Trim()}";
                    }
                }
                if (status != lastStatus)
                {
                    // "status" shows under "View game info"; the friends list itself only shows text from
                    // the game's Steam rich presence tokens, which only the publisher can upload.
                    SteamFriends.SetRichPresence("status", status);
                    lastStatus = status;
                }
                if (group + "/" + groupSize != lastGroup)
                {
                    // Standard keys: the Steam friends list groups players in the same crew together.
                    SteamFriends.SetRichPresence("steam_player_group", group);
                    SteamFriends.SetRichPresence("steam_player_group_size", groupSize);
                    lastGroup = group + "/" + groupSize;
                }
                if (connect != lastConnect)
                {
                    SteamFriends.SetRichPresence("connect", connect); // "" removes the key
                    lastConnect = connect;
                    Plugin.Log.LogInfo(connect.Length > 0 ? $"Steam: friends can join ({connect})" : "Steam: not joinable");
                    // Read it back from Steam to confirm it was accepted.
                    var me = Steamworks.KFPNOEDJHAI.GetSteamID();
                    Plugin.Log.LogInfo($"Steam: presence as Steam sees it: status='{SteamFriends.GetFriendRichPresence(me, "status")}' " +
                                       $"connect='{SteamFriends.GetFriendRichPresence(me, "connect")}' group='{SteamFriends.GetFriendRichPresence(me, "steam_player_group")}'");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Steam: presence update failed: " + e.Message);
                nextPresence = Time.realtimeSinceStartup + 30f;
            }
        }

        private static string PhaseText(string phase) =>
            phase == "GamePhase_BriefingRoom" ? "Briefing room" :
            phase == "GamePhase_Bridge" ? "On a mission" :
            phase == "GamePhase_MainMenu" ? "Shuttle" : "Loading";

        /// <summary>Steam overlay's friends list; "Invite to Game" is available there while we're joinable.</summary>
        internal static void OpenInviteOverlay()
        {
            try
            {
                if (SteamMode.Enabled && SteamMode.CurrentLobby != 0)
                    SteamFriends.ActivateGameOverlayInviteDialog(new Steamworks.IMHJDCPKKHM(SteamMode.CurrentLobby));
                else
                    SteamFriends.ActivateGameOverlay("Friends");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Steam: overlay failed: " + e.Message); }
        }

        internal static bool Joinable => !string.IsNullOrEmpty(lastConnect);
    }

    /// <summary>Small read-only views of game state, by reflection on readable members.</summary>
    internal static class GameState
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static Type Photon => Patches.PhotonNetworkType;

        private static object Static(Type t, string getter) =>
            t?.GetMethod(getter, BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)?.Invoke(null, null);

        internal static string Phase()
        {
            var gpm = typeof(SVR.Network.NetworkCore).Assembly.GetType("GamePhaseManager");
            var instance = Static(gpm, "get_Instance");
            return instance == null ? null : (gpm.GetMethod("GetCurrentGamePhase", All)?.Invoke(instance, null) as Type)?.Name;
        }

        internal static bool ConnectedAndReady() => Static(Photon, "get_connectedAndReady") is true;

        internal static bool Loading() =>
            Static(typeof(SVR.Network.NetworkCore).Assembly.GetType("NetworkLoadLevel"), "IsCurrentlyLoading") is true;

        private static object Room() => Static(Photon, "get_room");

        internal static string RoomName()
        {
            var room = Room();
            return room?.GetType().GetMethod("get_name", All)?.Invoke(room, null) as string;
        }

        internal static void RoomCounts(out int players, out int max, out bool open)
        {
            players = 0; max = 0; open = false;
            var room = Room();
            if (room == null) return;
            var t = room.GetType();
            // Convert, don't unbox: PUN's Room hides RoomInfo's byte maxPlayers with an int one.
            players = Convert.ToInt32(t.GetMethod("get_playerCount", All).Invoke(room, null));
            max = Convert.ToInt32(t.GetMethod("get_maxPlayers", All).Invoke(room, null));
            open = Convert.ToBoolean(t.GetMethod("get_open", All).Invoke(room, null));
        }
    }
}
