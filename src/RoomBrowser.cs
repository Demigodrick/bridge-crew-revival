using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// Room browser and join-by-code, replacing Uplay invites:
    ///  - short room codes for new rooms ("eu|K7QX2M" instead of a GUID);
    ///  - joining a room by code through the same game event Uplay invites use;
    ///  - a desktop overlay (toggle key, default F7) listing public rooms from the server's lobby list.
    /// The overlay is Unity IMGUI: it draws on the desktop window, not inside a VR headset.
    /// </summary>
    internal static class RoomBrowser
    {
        private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O, 1/I
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        private static bool visible;
        private static string codeInput = "";
        private static string status = "";
        private static Vector2 scroll;
        private static Rect window = new Rect(40, 40, 620, 480);
        private static bool savedCursorVisible;
        private static CursorLockMode savedLockMode;
        private static Type photonNetwork;

        // ---- Short room codes ------------------------------------------------------------

        internal static void Patch(Harmony harmony)
        {
            if (!Plugin.ShortRoomCodes.Value) return;
            var makeNew = AccessTools.Method(typeof(SVR.Network.OHCFGLAFILF), nameof(SVR.Network.OHCFGLAFILF.MakeNew));
            if (makeNew == null)
            {
                Plugin.Log.LogError("Room name generator not found; short room codes disabled.");
                return;
            }
            harmony.Patch(makeNew, prefix: new HarmonyMethod(typeof(RoomBrowser), nameof(MakeNew_Prefix)));
        }

        // OHCFGLAFILF.MakeNew() = AddRegion(Guid). Keep the region prefix, use a 6-character code.
        // Respects the game's own -roomname launch option.
        private static bool MakeNew_Prefix(ref string __result)
        {
            if (Environment.GetCommandLineArgs().Any(a => a.IndexOf("roomname", StringComparison.OrdinalIgnoreCase) >= 0))
                return true;
            var rng = new System.Random();
            var code = new string(Enumerable.Range(0, 6).Select(_ => CodeAlphabet[rng.Next(CodeAlphabet.Length)]).ToArray());
            __result = SVR.Network.OHCFGLAFILF.AddRegion(code);
            Plugin.Log.LogInfo($"New room: {__result}");
            return false;
        }

        // ---- Joining -----------------------------------------------------------------------

        /// <summary>"K7QX2M" or a full room name ("eu|K7QX2M", "eu|guid").</summary>
        /// Steam mode: a bare code is looked up by code alone, as the host's region (its prefix) may not be ours.
        internal static string FullRoomName(string input)
        {
            input = (input ?? "").Trim();
            if (input.Length == 0) return "";
            if (input.Contains("|")) return input;
            if (SteamMode.Enabled && SteamMode.FindByCode(input) is string found) return found;
            return SVR.Network.OHCFGLAFILF.AddRegion(input.ToUpperInvariant());
        }

        /// <summary>The last message from Join (e.g. why a room couldn't be joined).</summary>
        internal static string Status => status;

        internal static void Join(string input)
        {
            // Our mod version and Steam ID go with the join request: a crew's server refuses a join without the
            // version, and the hand-off counts only players whose Steam ID it knows. They're otherwise set once
            // a second, which a join right after connecting (a Steam invite at launch) can beat.
            ModVersionCheck.PublishOwn();
            HostHandoff.PublishOwn();
            var name = FullRoomName(input);
            if (name.Length == 0)
            {
                status = "Enter a room code first.";
                return;
            }
            // Steam mode: the room must be a public Steam lobby, from an invite, or a friend's room.
            if (SteamMode.Enabled && !SteamMode.Knows(name) && !SteamMode.ResolveFromFriends(name))
            {
                // By its code alone: private crews and crews on another version are found this way too.
                var byCode = SteamMode.FindByCodeAnyVersion(Code(name), out var version);
                if (byCode == null)
                {
                    Show($"Room {Code(name)} not found. Check the code; if it's right, the crew may have closed, or its host is on a Revival version from before 0.7.10 and the crew is private (a Steam friend in it can still let you in).");
                    return;
                }
                if (version != Plugin.Version)
                {
                    Show($"Crew {Code(name)} is on Bridge Crew Revival {version}; you have {Plugin.Version}. Everyone in a crew needs the same version: update from the website.");
                    return;
                }
                name = byCode;
            }
            // Same event Uplay invites and the friends list send; GamePhaseManager handles the rest
            // (ignores the room you're already in, otherwise GPMTransitionManager.LeaveAndJoinRoom).
            CIFDOFLFGEA.Send(new SVR.Network.PBGHGLIFLAE(name));
            status = $"Joining {Code(name)}…";
            Plugin.Log.LogInfo($"Join requested: {name}");
        }

        private static string ContentLabel(CrewContent c) =>
            c == CrewContent.NextGenerationOnly ? "Content: TNG only" : c == CrewContent.NoNextGeneration ? "Content: no TNG" : "Content: any";

        internal static string Code(string roomName) =>
            roomName == null ? "" : roomName.Contains("|") ? roomName.Substring(roomName.IndexOf('|') + 1) : roomName;

        // ---- Overlay ------------------------------------------------------------------------

        internal static void Update()
        {
            if (!Input.GetKeyDown(Plugin.BrowserKey.Value)) return;
            SetVisible(!visible);
            if (visible) status = "";
        }

        /// <summary>Open the window with a message (Steam join notices and the like).</summary>
        /// <summary>
        /// A message for the player: in this window, and as the game's own popup, which shows in the headset
        /// too (the window is the desktop's only). The same message isn't popped up again within ten seconds.
        /// </summary>
        internal static void Show(string message)
        {
            SetVisible(true);
            status = message;
            if (message == lastPopup && Time.realtimeSinceStartup - lastPopupAt < 10f) return;
            lastPopup = message;
            lastPopupAt = Time.realtimeSinceStartup;
            try { UI.PopupMgr.ShowPopup(message.Replace('|', ' '), null, UI.PDHCCBFJODN.Notification, true); }
            catch { /* no popups in this scene (loading); the window still has it */ }
        }

        private static string lastPopup;
        private static float lastPopupAt;

        private static void SetVisible(bool show)
        {
            if (show == visible) return;
            visible = show;
            if (visible)
            {
                savedCursorVisible = Cursor.visible;
                savedLockMode = Cursor.lockState;
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }
            else
            {
                Cursor.visible = savedCursorVisible;
                Cursor.lockState = savedLockMode;
            }
        }

        internal static void OnGUI()
        {
            if (!visible) return;
            window = GUILayout.Window(0x5BC7, window, DrawWindow, "Bridge Crew Revival — Rooms");
        }

        private static void DrawWindow(int id)
        {
            photonNetwork = photonNetwork ?? Patches.PhotonNetworkType;
            string current = CurrentRoomName();

            GUILayout.Label(current != null
                ? $"You are in room  {Code(current)}   (share this code with your crew)"
                : InLobby() ? "Connected. Pick a room or enter a code." : "Not connected to the server yet.");
            if (current != null)
            {
                if (SteamMode.Enabled) GUILayout.Label(SteamMode.RoomStatus(current, CurrentRoomVisible()));
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Copy my room code", GUILayout.Width(180)))
                {
                    GUIUtility.systemCopyBuffer = Code(current);
                    status = "Room code copied.";
                }
                if (Plugin.SteamPresence.Value && SteamIntegration.Joinable && GUILayout.Button("Invite Steam friends", GUILayout.Width(180)))
                {
                    SteamIntegration.OpenInviteOverlay();
                    status = "In the Steam overlay, right-click a friend and choose \"Invite to Game\".";
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Room code:", GUILayout.Width(80));
            codeInput = GUILayout.TextField(codeInput, 64, GUILayout.Width(220));
            if (GUILayout.Button("Paste", GUILayout.Width(60))) codeInput = (GUIUtility.systemCopyBuffer ?? "").Trim();
            if (GUILayout.Button("Join", GUILayout.Width(70))) Join(codeInput);
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            GUILayout.Label(SteamMode.Enabled
                ? "Public rooms (Steam, worldwide):" + (SteamMode.OnlineText.Length > 0 ? "   · " + SteamMode.OnlineText : "")
                : "Public rooms on this server:");
            GUILayout.BeginHorizontal();
            Plugin.HideFullCrews.Value = GUILayout.Toggle(Plugin.HideFullCrews.Value, " Hide full", GUILayout.Width(110));
            Plugin.HideCrewsInMission.Value = GUILayout.Toggle(Plugin.HideCrewsInMission.Value, " Hide in mission", GUILayout.Width(140));
            if (GUILayout.Button(ContentLabel(Plugin.CrewContentFilter.Value), GUILayout.Width(170)))
                Plugin.CrewContentFilter.Value = (CrewContent)(((int)Plugin.CrewContentFilter.Value + 1) % 3);
            GUILayout.EndHorizontal();
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(270));
            var rooms = RoomList();
            if (rooms.Length == 0)
                GUILayout.Label(SteamMode.Enabled || InLobby() ? "  None right now. Start a Quick match to host one." : "  (room list appears once you're connected, on the shuttle)");
            foreach (var r in rooms)
            {
                GUILayout.BeginHorizontal("box");
                GUILayout.Label(r.Mine ? r.Code + " (you)" : r.Code, GUILayout.Width(90));
                GUILayout.Label($"{r.Mode} · {r.Phase}", GUILayout.Width(220));
                GUILayout.Label($"{r.Players}/{(r.MaxPlayers > 0 ? r.MaxPlayers.ToString() : "any")}", GUILayout.Width(50));
                GUI.enabled = r.Joinable && r.Name != current && !r.Mine;
                if (GUILayout.Button(r.Joinable ? "Join" : "Full", GUILayout.Width(70))) Join(r.Name);
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();

            if (status.Length > 0) GUILayout.Label(status);
            GUILayout.Label(SteamMode.Enabled
                ? $"Press {Plugin.BrowserKey.Value} to close. Public rooms are Steam lobbies, worldwide."
                : $"Press {Plugin.BrowserKey.Value} to close. Also at http://{Plugin.Server.Value}:8080/ on a self-hosted server.");
            GUI.DragWindow();
        }

        internal sealed class Entry
        {
            public string Name, Code, Mode, Phase;
            public int Players, MaxPlayers;
            public bool Joinable, Mine, InMission, NextGeneration;
        }

        private static Entry MakeEntry(string name, IDictionary props, int players, int max, bool open, bool mine) => new Entry
        {
            Name = name,
            Code = Code(name),
            Mode = ModeName(Int(props, "C3")),
            Phase = PhaseName(Int(props, "C6")),
            Players = players,
            MaxPlayers = max,
            Joinable = open && (max == 0 || players < max),
            Mine = mine,
            // C6 game phase: 1 shuttle, 2 briefing room, 4 intro; anything else is a mission.
            InMission = Int(props, "C6") is int phase && phase != 1 && phase != 2 && phase != 4,
            NextGeneration = Int(props, "C9") == 1,
        };

        // [Rooms] HideFullCrews / HideCrewsInMission / Content. Your own room is always listed.
        private static bool PassesFilters(Entry e)
        {
            if (e.Mine) return true;
            if (Plugin.HideFullCrews.Value && !e.Joinable) return false;
            if (Plugin.HideCrewsInMission.Value && e.InMission) return false;
            switch (Plugin.CrewContentFilter.Value)
            {
                case CrewContent.NextGenerationOnly: return e.NextGeneration;
                case CrewContent.NoNextGeneration: return !e.NextGeneration;
                default: return true;
            }
        }

        /// <summary>Public rooms (your own first), for this window and the VR panels.</summary>
        internal static Entry[] RoomList()
        {
            photonNetwork = photonNetwork ?? Patches.PhotonNetworkType;
            try
            {
                // Steam mode: Steam lobbies (Photon's lobby list is empty while you're in a room).
                var entries = SteamMode.Enabled
                    ? SteamMode.PublicRooms().Select(r => MakeEntry(r.Name, (IDictionary)r.Properties, r.Players, r.MaxPlayers, r.Open, r.Mine))
                    : PhotonRooms();
                return entries.Where(e => e.Name != null && PassesFilters(e))
                    .OrderByDescending(e => e.Mine).ThenByDescending(e => e.Joinable).ThenBy(e => e.Code).ToArray();
            }
            catch (Exception e)
            {
                status = "Room list unavailable: " + e.Message;
                return new Entry[0];
            }
        }

        private static System.Collections.Generic.IEnumerable<Entry> PhotonRooms()
        {
            var list = photonNetwork?.GetMethod("GetRoomList", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) as Array;
            if (list == null) return new Entry[0];
            return list.Cast<object>().Select(info =>
            {
                var t = info.GetType();
                object Get(string getter) => t.GetMethod(getter, All).Invoke(info, null);
                return MakeEntry((string)Get("get_name"), (IDictionary)Get("get_customProperties"),
                    Convert.ToInt32(Get("get_playerCount")), Convert.ToInt32(Get("get_maxPlayers")), Convert.ToBoolean(Get("get_open")), false);
            });
        }

        // Room.visible: Custom lobbies start hidden; "open for public matchmaking" makes them visible.
        internal static bool CurrentRoomVisible()
        {
            photonNetwork = photonNetwork ?? Patches.PhotonNetworkType;
            var room = photonNetwork?.GetMethod("get_room", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
            return room?.GetType().GetMethod("get_visible", All)?.Invoke(room, null) is true;
        }

        internal static string CurrentRoomName()
        {
            photonNetwork = photonNetwork ?? Patches.PhotonNetworkType;
            var room = photonNetwork?.GetMethod("get_room", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
            return room?.GetType().GetMethod("get_name", All)?.Invoke(room, null) as string;
        }

        private static bool InLobby() =>
            photonNetwork?.GetMethod("get_insideLobby", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) is true;

        private static int? Int(IDictionary props, string key)
        {
            if (props == null || !props.Contains(key)) return null;
            var v = props[key];
            return v is int i ? i : v is byte b ? b : v is short s ? (int?)s : null;
        }

        // Room properties as Bridge Crew sets them (OOLKIMEFIPC): C3 GameMode, C6 CurrentGamePhase.
        private static string ModeName(int? mode) =>
            mode == 0 ? "Public" : mode == 1 ? "Solo" : mode == 2 ? "Private" : mode == 3 ? "Tutorial" : "Unknown";

        private static string PhaseName(int? phase) =>
            phase == 1 ? "Shuttle" : phase == 2 ? "Briefing room" : phase == 4 ? "Intro" : phase == null ? "?" : "In mission";
    }
}
