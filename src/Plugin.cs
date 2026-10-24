using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace StbcRevival
{
    public enum PhotonMode
    {
        /// <summary>Leave the game's Photon settings alone (official Ubisoft Photon app).</summary>
        Default,
        /// <summary>Connect to a self-hosted Photon Server (LoadBalancing) at Server:Port.</summary>
        SelfHosted,
        /// <summary>Connect to Photon Cloud using your own App ID.</summary>
        Cloud,
        /// <summary>No server: each game runs one in-process; play over Steam P2P, rooms listed as Steam lobbies.</summary>
        Steam,
    }

    public enum PhotonProtocol { Udp, Tcp }

    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "stbc.revival";
        public const string Name = "Bridge Crew Revival";
        public const string Version = "0.6.7";

        internal static ManualLogSource Log;

        internal static ConfigEntry<PhotonMode> Mode;
        internal static ConfigEntry<string> Server;
        internal static ConfigEntry<int> Port;
        internal static ConfigEntry<PhotonProtocol> Protocol;
        internal static ConfigEntry<bool> RewriteGameServerAddress;
        internal static ConfigEntry<string> AppId;
        internal static ConfigEntry<string> Region;
        internal static ConfigEntry<bool> StripSteamAuth;
        internal static ConfigEntry<string> MatchmakingTag;
        internal static ConfigEntry<bool> SkipUplay;
        internal static ConfigEntry<bool> SkipIntro;
        internal static ConfigEntry<bool> LocalSave;
        internal static ConfigEntry<bool> FetchUbisoftSave;
        internal static ConfigEntry<bool> CheckForUpdates;
        internal static ConfigEntry<bool> TestLastStand;
        internal static ConfigEntry<bool> SkipMicCheck;
        internal static ConfigEntry<bool> HideFullCrews;
        internal static ConfigEntry<bool> HideCrewsInMission;
        internal static ConfigEntry<CrewContent> CrewContentFilter;
        internal static ConfigEntry<bool> PlayerJoinedAlert;
        internal static ConfigEntry<UnityEngine.KeyCode> BugReportKey;
        internal static ConfigEntry<UnityEngine.KeyCode> AvatarDumpKey;
        internal static ConfigEntry<bool> RomulanFace;
        internal static ConfigEntry<string> MapOverride;
        internal static ConfigEntry<int> FleetBattleSize;
        internal static ConfigEntry<string> UpdateUrl;
        internal static ConfigEntry<bool> ShuttleWasd;
        internal static ConfigEntry<float> ShuttleSpeed;
        internal static ConfigEntry<float> ShuttleTurnSpeed;
        internal static Plugin Instance { get; private set; }
        internal static ConfigEntry<string> PlayerName;
        internal static ConfigEntry<string> ExtraDevConfig;
        internal static ConfigEntry<bool> Diagnostics;
        internal static ConfigEntry<string> TraceTypes;
        internal static ConfigEntry<UnityEngine.KeyCode> BrowserKey;
        internal static ConfigEntry<bool> ShortRoomCodes;
        internal static ConfigEntry<bool> VrRoomPanels;
        internal static ConfigEntry<bool> SteamPresence;
        internal static ConfigEntry<string> PublicServerAddress;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            Mode = Config.Bind("Photon", "Mode", PhotonMode.Steam,
                "Steam = no server needed: play with anyone over Steam (rooms listed as Steam lobbies, friends join via Steam). " +
                "SelfHosted = a dedicated stbc-server (or Photon Server). Cloud = Photon Cloud with your own App ID. " +
                "Default = official Ubisoft servers (offline since the shutdown).");
            Server = Config.Bind("Photon", "Server", "127.0.0.1",
                "SelfHosted: hostname or IP of the server (the stbc-server container, or a Photon Server).");
            Port = Config.Bind("Photon", "Port", 4530,
                "SelfHosted: server port (stbc-server: 4530 TCP; Photon Server defaults: 5055 UDP, 4530 TCP).");
            Protocol = Config.Bind("Photon", "Protocol", PhotonProtocol.Tcp,
                "SelfHosted: transport protocol. stbc-server only speaks TCP.");
            RewriteGameServerAddress = Config.Bind("Photon", "RewriteGameServerAddress", true,
                "SelfHosted: connect to Server:Port for the game server too, ignoring the address the master hands out. " +
                "Makes Docker port mapping and NAT work without configuring the server's public address.");
            AppId = Config.Bind("Photon", "AppId", "",
                "Cloud: your Photon Realtime/PUN App ID from dashboard.photonengine.com.");
            Region = Config.Bind("Photon", "Region", "",
                "Cloud: eu, us, asia, jp or au. Empty = the game's saved/default region.");
            StripSteamAuth = Config.Bind("Photon", "StripSteamAuth", true,
                "Don't send the Steam auth ticket to Photon. Needed for Cloud apps without Steam auth configured; harmless for SelfHosted.");
            MatchmakingTag = Config.Bind("Photon", "MatchmakingTag", "",
                "Optional. Replaces the environment part of the matchmaking version (game's 'environmentOverride'), so only players with the same tag see each other's rooms.");

            SkipUplay = Config.Bind("Login", "SkipUplay", true,
                "Use the game's built-in no-platform login mode: no Uplay client, no Ubisoft account, no Ubisoft online services.");
            SkipIntro = Config.Bind("Startup", "SkipIntro", true,
                "Skip the logo and movie screens at launch and go straight to the shuttle. Connection, controller and headset checks still run.");
            LocalSave = Config.Bind("Game", "LocalSave", true,
                "Keep the game's settings (avatar appearance, options, progress flags) in BepInEx/config/stbc.revival.save.json. The game only ever saved these to Ubisoft's servers.");
            FetchUbisoftSave = Config.Bind("Import", "FetchUbisoftSave", false,
                "One run only (the installer sets this): log in to Ubisoft, copy your Ubisoft cloud save into the local save, then quit. Needs Ubisoft Connect signed in. Switches itself off afterwards.");
            SkipMicCheck = Config.Bind("Startup", "SkipMicCheck", false,
                "Skip the microphone check the game shows when you start. Your mic still works; set it up in the game's options.");
            HideFullCrews = Config.Bind("Rooms", "HideFullCrews", false,
                "Crew lists (shuttle, escape menu, F7): leave out crews with no free seat.");
            HideCrewsInMission = Config.Bind("Rooms", "HideCrewsInMission", false,
                "Crew lists: leave out crews already in a mission (only list those in a briefing room or shuttle).");
            CrewContentFilter = Config.Bind("Rooms", "Content", CrewContent.Any,
                "Crew lists: Any, NextGenerationOnly (crews with The Next Generation content on) or NoNextGeneration.");
            PlayerJoinedAlert = Config.Bind("Rooms", "PlayerJoinedAlert", true,
                "When someone joins your crew: a chime, and the game's taskbar entry flashes if the window isn't in front. Handy while waiting with the headset off.");
            BugReportKey = Config.Bind("Debug", "BugReportKey", UnityEngine.KeyCode.F10,
                "Saves the logs and settings needed for a bug report as one zip in the game folder.");
            RomulanFace = Config.Bind("Avatar", "RomulanFace", false,
                "Wear the Romulan captain's face. Female Vulcan character presets only; crewmates with the mod see it too.");
            AvatarDumpKey = Config.Bind("Debug", "AvatarDumpKey", UnityEngine.KeyCode.F9,
                "Testing only: describes the hail-screen characters and the player avatars to BepInEx/avatar-dump.txt.");
            FleetBattleSize = Config.Bind("Revival", "FleetBattleSize", 30,
                "Fleet Battle: ships on each side (1-60). Big battles are heavy on the host's computer; lower it if the game stutters.");
            MapOverride = Config.Bind("Debug", "MapOverride", "",
                "Testing only: the sector map Free Roam and Ongoing Voyages load, as a path in the game's Resources, e.g. \"Mission Data/Maps/Sector - E3\" "
                + "or \"Mission Data/Maps/Sector - SpaceStationRescue\". Applies to your whole crew when you host. Off the Trench there are no "
                + "mission encounters: you start in one of the map's systems and can warp to its others. Empty: the game's own map.");
            TestLastStand = Config.Bind("Debug", "TestLastStand", false,
                "Testing only: every Ongoing Voyage you host runs the Revival mission Last Stand instead.");
            CheckForUpdates = Config.Bind("Updates", "CheckForUpdates", true,
                "Once per launch, on the shuttle, check the website for a newer version and offer to open it. Nothing is downloaded; if the check fails, nothing is shown.");
            UpdateUrl = Config.Bind("Updates", "VersionUrl", "",
                "Where to read the latest version number. Empty = the official website's version.txt.");
            ShuttleWasd = Config.Bind("Shuttle", "WasdControls", false,
                "Experimental, doesn't work yet: fly the main-menu shuttle with the keyboard (W/S forward/back, A/D turn, Q/E down/up, Shift faster).");
            ShuttleSpeed = Config.Bind("Shuttle", "Speed", 20f, "Shuttle keyboard speed (scene units per second).");
            ShuttleTurnSpeed = Config.Bind("Shuttle", "TurnSpeed", 30f, "Shuttle keyboard turn speed (degrees per second).");
            PlayerName = Config.Bind("Login", "PlayerName", "",
                "Name shown to other players. Empty = your Steam persona name (falls back to the OS user name).");

            ExtraDevConfig = Config.Bind("Game", "ExtraDevConfig", "",
                "Extra values for the game's leftover developer config, as key=value;key=value. " +
                "Known keys include maxnumplayers, mutevoip, spectator, hideNameTag, skipMicCheck, language, renderscale.");

            BrowserKey = Config.Bind("Rooms", "BrowserKey", UnityEngine.KeyCode.F7,
                "Key that opens the room browser / join-by-code window (desktop window; not visible inside a VR headset).");
            VrRoomPanels = Config.Bind("Rooms", "VrPanels", true,
                "Show public crews in VR: on the shuttle's newsfeed console, and (with your crew's code and status) in the escape menu's More pane.");
            ShortRoomCodes = Config.Bind("Rooms", "ShortRoomCodes", true,
                "Give new rooms short 6-character codes (e.g. K7QX2M) instead of GUIDs, so they're easy to share for private matches.");

            SteamPresence = Config.Bind("Steam", "Presence", true,
                "Show your crew in Steam (rich presence) and let Steam friends use Join Game / Invite to Game. " +
                "Friends see your room code, including for private matches.");
            PublicServerAddress = Config.Bind("Steam", "PublicServerAddress", "",
                "Optional host:port of your server as friends reach it (e.g. 203.0.113.10:4530). Added to Steam joins " +
                "so a friend on a different server is told which one to use.");

            Diagnostics = Config.Bind("Debug", "Diagnostics", true,
                "Log game network errors (with stack traces), Photon status changes and failed operations to BepInEx/LogOutput.log.");
            TraceTypes = Config.Bind("Debug", "TraceTypes", "",
                "Diagnostics: comma-separated game type names whose method calls are logged (first 10 calls each).");

            DevConfig.Build();

            harmony = new Harmony(Guid);
            Patches.Apply(harmony);
            SteamMode.Install(harmony);
            ShuttleControls.Install();

            Log.LogInfo($"{Name} {Version} loaded. Photon mode: {Mode.Value}, skip Uplay: {SkipUplay.Value}");
        }

        private Harmony harmony;
        private bool deferredApplied;

        private void Update()
        {
            if (!deferredApplied)
            {
                deferredApplied = true;
                Patches.ApplyDeferred(harmony);
            }
            // Each subsystem on its own: one failing must not stop the others (or spam every frame).
            if (Diagnostics.Value)
                Safely("diagnostics", () => StbcRevival.Diagnostics.Heartbeat(Patches.PhotonNetworkType));
            Safely("steam mode", SteamMode.Update);
            Safely("room browser", RoomBrowser.Update);
            Safely("steam integration", SteamIntegration.Update);
            Safely("steam friends panel", SteamFriendsPanel.Update);
            Safely("local save", StbcRevival.LocalSave.Update);
            Safely("vr room panels", StbcRevival.VrRoomPanels.Update);
            Safely("update check", UpdateCheck.Update);
            Safely("player joined alert", StbcRevival.PlayerJoinedAlert.Update);
            Safely("bug report", BugReport.Update);
            Safely("avatar dump", AvatarDump.Update);
            Safely("romulan face", StbcRevival.RomulanFace.Update);
            Safely("romulan creator choice", StbcRevival.RomulanCreatorOption.Update);
            Safely("mission maps", StbcRevival.Missions.MissionMaps.Update);
            Safely("ubisoft save import", UbisoftSaveImport.Update);
        }

        private readonly System.Collections.Generic.HashSet<string> failedOnce = new System.Collections.Generic.HashSet<string>();

        private void Safely(string what, System.Action action)
        {
            try { action(); }
            catch (System.Exception e)
            {
                if (failedOnce.Add(what)) Log.LogError($"{what} failed (logged once): {e}");
            }
        }

        private void OnApplicationQuit() => Safely("local save on quit", StbcRevival.LocalSave.OnQuit);

        private void OnGUI() => Safely("room browser window", RoomBrowser.OnGUI);
    }

    /// <summary>
    /// Values answered on behalf of the game's (stripped) developer config file, SVR.ConfigFile.
    /// The game still asks for keys like "server" / "serverport" / "protocol" / "region" / "name";
    /// in retail nothing ever fills them, so it falls back to the official Photon cloud.
    /// </summary>
    internal enum CrewContent { Any, NextGenerationOnly, NoNextGeneration }

    internal static class DevConfig
    {
        internal static readonly Dictionary<string, string> Values = new Dictionary<string, string>();

        internal static void Build()
        {
            Values.Clear();

            foreach (var pair in Plugin.ExtraDevConfig.Value.Split(';'))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                Values[pair.Substring(0, eq).Trim().ToLower()] = pair.Substring(eq + 1).Trim();
            }

            switch (Plugin.Mode.Value)
            {
                case PhotonMode.SelfHosted:
                    // Read by the game's own connect step (CGLHLIGPPKH.IAIIDDKFMHB), which switches
                    // PhotonServerSettings to SelfHosted. Order matters there: "protocol" resets the
                    // port to the protocol default, then "serverport" overrides it.
                    Values["server"] = Plugin.Server.Value.Trim();
                    Values["protocol"] = Plugin.Protocol.Value == PhotonProtocol.Tcp ? "tcp" : "udp";
                    Values["serverport"] = Plugin.Port.Value.ToString();
                    break;
                case PhotonMode.Steam:
                    // The game's own connect step then targets the in-game server (see SteamMode).
                    Values["server"] = "local";
                    Values["protocol"] = "tcp";
                    Values["serverport"] = "1";
                    break;
                case PhotonMode.Cloud:
                    if (!string.IsNullOrEmpty(Plugin.Region.Value))
                        Values["region"] = Plugin.Region.Value.Trim();
                    break;
            }

            if (!string.IsNullOrEmpty(Plugin.MatchmakingTag.Value))
                Values["environmentoverride"] = Plugin.MatchmakingTag.Value.Trim();
            if (!string.IsNullOrEmpty(Plugin.PlayerName.Value))
                Values["name"] = Plugin.PlayerName.Value.Trim();
            // Read by MicCheckUI ("skipMicCheck", bool).
            if (Plugin.SkipMicCheck.Value)
                Values["skipmiccheck"] = "true";

            foreach (var kv in Values)
                Plugin.Log.LogInfo($"dev config: {kv.Key} = {kv.Value}");
        }
    }
}
