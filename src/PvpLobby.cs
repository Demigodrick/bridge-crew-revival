using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using StbcRevival.Missions;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace StbcRevival
{
    /// <summary>
    /// PvP from the main menu: a PVP game mode next to Quick, Solo, Private and Tutorial ([Debug] PvpTest shows
    /// it). It runs the game's own Quick match with its own game mode and room size: rooms say their mode in the
    /// listed room property C3 (Quick 0, Solo 1, Private 2, Tutorial 3), and Quick match only joins rooms of its
    /// own mode and size, so PvP finds a waiting PvP crew (eight seats) or hosts one, and never a co-op crew.
    ///  - The menu: the toggle is a copy of Quick's (same toggle group). While it's picked the menu's own mode
    ///    is "none" (COUNT), so picking another mode afterwards works as before; Play runs Quick, and the
    ///    matchmaking it starts (OCCEGCFNLPH.BeginPublicMatchmaking) gets the PvP mode and eight players
    ///    (NovaPlayerManager.MaxNumPlayers, which it reads for the room it joins or makes).
    ///  - In a PvP room the host keeps the PvP mission picked in the briefing room (RevivalBriefing), and the
    ///    room stays at eight (PvpRoles.RoomSize).
    /// </summary>
    internal static class PvpLobby
    {
        internal const int GameMode = 7;
        private const string Description = "Ship against ship: two crews of up to four";
        private const BindingFlags Inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;

        /// <summary>PVP is the mode picked on the main menu (until another is).</summary>
        internal static bool Picked { get; private set; }

        private static readonly FieldInfo ModeField = AccessTools.Field(typeof(ConsoleMainMenu), "FNCLHAJMFJC");
        private static float nextCheck;

        internal static void Patch(Harmony harmony)
        {
            void Hook(Type type, string method, string prefix = null, string postfix = null)
            {
                var target = type == null ? null : AccessTools.Method(type, method);
                if (target == null)
                {
                    Plugin.Log.LogWarning($"PvP: {type?.Name}.{method} not found; no PVP on the main menu");
                    return;
                }
                harmony.Patch(target,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(PvpLobby), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(PvpLobby), postfix));
            }
            var menu = typeof(ConsoleMainMenu);
            Hook(menu, "Start", postfix: nameof(MenuStart_Postfix));
            Hook(menu, "MHOIHOKNNFC", prefix: nameof(ModePicked_Prefix)); // a mode's toggle turned on
            Hook(menu, "OnPlayGame", prefix: nameof(Play_Prefix));
            Hook(AccessTools.TypeByName("OCCEGCFNLPH"), "BeginPublicMatchmaking", prefix: nameof(Matchmaking_Prefix));
        }

        // ---- The main menu ----------------------------------------------------------------------------

        private static void MenuStart_Postfix(ConsoleMainMenu __instance)
        {
            if (!Plugin.PvpTest.Value) return;
            var menu = __instance;
            try
            {
                var infos = AccessTools.Field(typeof(ConsoleMainMenu), "_gameModeInfos")?.GetValue(menu) as Array;
                if (infos == null || infos.Length == 0) return;
                var toggles = infos.Cast<object>().Where(i => i != null)
                    .Select(i => new { Mode = Convert.ToInt32(Traverse.Create(i).Field("Mode").GetValue()), Toggle = Traverse.Create(i).Field("ModeSelectionToggle").GetValue<Toggle>() })
                    .Where(t => t.Toggle != null).ToList();
                var quick = toggles.FirstOrDefault(t => t.Mode == 0)?.Toggle;
                if (quick == null) return;

                // The GAME MODE box holds four rows, from half a row above the top one to half a row below the
                // bottom one (the AVATAR button is just under it); space five rows evenly in that span instead.
                var parent = quick.transform.parent;
                var rows = toggles.Select(t => t.Toggle.transform).Where(t => t.parent == parent).OrderByDescending(t => t.localPosition.y).ToList();
                Plugin.Log.LogInfo("PvP: main menu modes at " + string.Join(", ", toggles.Select(t => $"{t.Mode} {t.Toggle.name} {t.Toggle.transform.localPosition}").ToArray()));
                var copy = (GameObject)UnityEngine.Object.Instantiate(quick.gameObject, parent);
                copy.name = "PvP Mode Toggle";
                rows.Add(copy.transform);
                if (rows.Count >= 3)
                {
                    Vector3 top = rows[0].localPosition, bottom = rows[rows.Count - 2].localPosition;
                    var oldStep = (bottom - top) / (rows.Count - 2);
                    var newStep = oldStep * (rows.Count - 1) / rows.Count;
                    var first = top - oldStep / 2f + newStep / 2f;
                    for (int i = 0; i < rows.Count; i++) rows[i].localPosition = first + newStep * i;
                }
                copy.transform.SetAsLastSibling();
                var toggle = copy.GetComponent<Toggle>();
                toggle.onValueChanged.RemoveAllListeners();
                toggle.isOn = false;
                foreach (var label in copy.GetComponentsInChildren<TMP_Text>(true)) label.SetLocalizedText("PVP");
                toggle.onValueChanged.AddListener(on => { if (on) PickPvp(menu); });
                PvpRoles.TellHands(menu);
                Plugin.Log.LogInfo($"PvP: added PVP to the main menu's game modes, at {copy.transform.localPosition}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("PvP: couldn't add PVP to the main menu: " + e.Message);
            }
        }

        private static void PickPvp(ConsoleMainMenu menu)
        {
            Picked = true;
            ModeField?.SetValue(menu, Enum.ToObject(ModeField.FieldType, 4)); // COUNT: none of the game's modes
            if (AccessTools.Field(typeof(ConsoleMainMenu), "_gameModeDescriptionText")?.GetValue(menu) is TMP_Text text)
                text.SetLocalizedText(Description);
            Plugin.Log.LogInfo("PvP: PVP picked on the main menu");
        }

        // Another mode picked: PVP isn't any more. The menu also calls this every frame with its current mode
        // (BatchUpdate), so only a different mode counts.
        private static void ModePicked_Prefix(ConsoleMainMenu __instance, object __0)
        {
            if (!Picked || ModeField == null || Convert.ToInt32(__0) == Convert.ToInt32(ModeField.GetValue(__instance))) return;
            Picked = false;
            Plugin.Log.LogInfo("PvP: another game mode picked on the main menu");
        }

        private static void Play_Prefix(ConsoleMainMenu __instance)
        {
            if (!Picked) return;
            ModeField?.SetValue(__instance, Enum.ToObject(ModeField.FieldType, 0)); // Quick, as PvP
            Plugin.Log.LogInfo("PvP: play pressed with PVP picked");
        }

        private static void Matchmaking_Prefix(ref int __0)
        {
            if (!Picked) return;
            __0 = GameMode;
            // The menu's mode back to "none" (Play set Quick), so picking Quick afterwards counts as a change.
            foreach (var menu in UnityEngine.Object.FindObjectsOfType<ConsoleMainMenu>())
                ModeField?.SetValue(menu, Enum.ToObject(ModeField.FieldType, 4));
            if (NovaPlayerManager.IEFBKOMBFGO != null) NovaPlayerManager.IEFBKOMBFGO.MaxNumPlayers = PvpRoles.Slots;
            Plugin.Log.LogInfo($"PvP: looking for a PvP crew (game mode {GameMode}, {PvpRoles.Slots} players), or hosting one");
        }

        // ---- PvP rooms --------------------------------------------------------------------------------

        /// <summary>The room we're in is a PvP one (made from the main menu's PVP).</summary>
        internal static bool InPvpRoom
        {
            get
            {
                var room = Patches.PhotonNetworkType?.GetMethod("get_room", Static)?.Invoke(null, null);
                var props = room?.GetType().GetMethod("get_customProperties")?.Invoke(room, null) as IDictionary;
                return props != null && props.Contains("C3") && props["C3"] is int mode && mode == GameMode;
            }
        }

        // ---- The briefing room: two tables ----------------------------------------------------------

        // Ship A's table moves left and ship B's (a copy, with the station copies PvpConsoles makes) sits right,
        // along the room's width; what stands between them is put away.
        private static readonly Vector3 TableA = new Vector3(-1.9f, 0f, 0f), TableB = new Vector3(1.9f, 0f, 0f);
        private static readonly string[] TableParts = { "BriefingRoom_Table", "BriefingRoom_Table_ProjectorEffect", "Mdl_AegisBriefingRoom" };
        private static readonly string[] InTheWay =
        {
            "BriefingRoom_SideTable", "Communicator", "Glasses", "SaurianBrandy", "Platter001", "BriefingRoom_TriDChess", "BriefingRoom_LoungeChair",
        };
        private static readonly List<GameObject> putAway = new List<GameObject>();
        private static string dressedScene;

        /// <summary>A briefing station and its ship B copy (PvpConsoles), as the station wakes: to their tables.</summary>
        internal static void PlaceStations(NovaStation original, NovaStation copy)
        {
            original.transform.position += TableA;
            copy.transform.position += TableB;
            Plugin.Log.LogInfo($"PvP: briefing station {original.name} at table A, its copy at table B");
        }

        // The tables themselves, and what's in the way, once the briefing room's scene is in (stations wake first).
        private static void DressBriefingRoom()
        {
            var scene = SceneManager.GetSceneByName("Aegis_Briefing_Room");
            if (!scene.IsValid() || !scene.isLoaded || !InPvpRoom) { if (!scene.IsValid() || !scene.isLoaded) dressedScene = null; return; }
            var id = scene.GetHashCode().ToString();
            if (dressedScene == id)
            {
                foreach (var thing in putAway) if (thing != null && thing.activeSelf) thing.SetActive(false); // the decor's own code turns some on
                return;
            }
            dressedScene = id;
            putAway.Clear();
            int tables = 0;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    var name = t.name;
                    if (TableParts.Contains(name))
                    {
                        var copy = (GameObject)UnityEngine.Object.Instantiate(t.gameObject, t.parent, true);
                        copy.name = name + " (table B)";
                        t.position += TableA;
                        copy.transform.position = t.position - TableA + TableB;
                        tables++;
                    }
                    else if (InTheWay.Any(n => name.StartsWith(n)) && (t.parent == null || !InTheWay.Any(n => t.parent.name.StartsWith(n))))
                        putAway.Add(t.gameObject);
                }
            foreach (var thing in putAway) thing.SetActive(false);
            Plugin.Log.LogInfo($"PvP: briefing room set for two tables ({tables} table parts copied, {putAway.Count} things put away)");
        }

        /// <summary>Once a second: the host of a PvP room keeps the PvP mission picked in the briefing room.</summary>
        internal static void Update()
        {
            DressBriefingRoom();
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 1f;
            if (!InPvpRoom || !(Patches.PhotonNetworkType?.GetMethod("get_isMasterClient", Static)?.Invoke(null, null) is true)) return;
            if (GamePhaseManager.IEFBKOMBFGO == null || GamePhaseManager.IEFBKOMBFGO.GetCurrentGamePhase() != typeof(GamePhase_BriefingRoom)) return;
            if (RevivalBriefing.RoomSelection() == RevivalMissions.PvpTest) return;
            RevivalBriefing.Choose(RevivalMissions.PvpTest);
            Plugin.Log.LogInfo("PvP: PvP room, the PvP mission picked");
        }
    }
}
