using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using PhotonHashtable = ExitGames.Client.Photon.Hashtable;

namespace StbcRevival.Missions
{
    /// <summary>
    /// The briefing room's third mission category, "REVIVAL MISSIONS", next to Campaign and Ongoing Voyages.
    /// The toggle is a copy of the Ongoing Voyages one (same toggle group, so picking any category clears the
    /// others). The choice is a room property ("RV" = mission id), so every player's console shows it and the
    /// host, who generates the mission, knows to run it. While it's selected the game's own mission type is
    /// Procedural, which is how Revival missions launch (see RevivalMissions); ship choice works as for an
    /// Ongoing Voyage. Everything else on the console is re-applied after the console's own per-frame
    /// refresh (BatchUpdate).
    /// </summary>
    internal static class RevivalBriefing
    {
        internal const string RoomKey = "RV";
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // The briefing room has a console per station view; each gets its own Revival toggle.
        private static readonly System.Collections.Generic.Dictionary<ConsoleBriefingRoom, Toggle> toggles =
            new System.Collections.Generic.Dictionary<ConsoleBriefingRoom, Toggle>();
        private static bool applying;

        // Ship toggles → their console, and the consoles where this player is choosing a Revival mission.
        private static readonly System.Collections.Generic.Dictionary<Selectable, ConsoleBriefingRoom> shipToggles =
            new System.Collections.Generic.Dictionary<Selectable, ConsoleBriefingRoom>();
        private static readonly System.Collections.Generic.HashSet<ConsoleBriefingRoom> choosingRevival =
            new System.Collections.Generic.HashSet<ConsoleBriefingRoom>();
        private static readonly string[] ShipFields = { "_aegisToggle", "_enterpriseToggle", "_tngToggle" };

        internal static void Patch(Harmony harmony)
        {
            var start = AccessTools.Method(typeof(ConsoleBriefingRoom), "Start");
            var refresh = AccessTools.Method(typeof(ConsoleBriefingRoom), "BatchUpdate");
            if (start == null || refresh == null)
            {
                Plugin.Log.LogWarning("Revival missions: briefing console methods not found; no Revival Missions category.");
                return;
            }
            harmony.Patch(start, postfix: new HarmonyMethod(typeof(RevivalBriefing), nameof(Start_Postfix)));
            harmony.Patch(refresh, postfix: new HarmonyMethod(typeof(RevivalBriefing), nameof(Refresh_Postfix)));
            // The console disables the Enterprise and Enterprise-D unless Ongoing Voyages is selected, every
            // frame, through this helper; setting them back afterwards restarts the button's fade each frame,
            // leaving them greyed. Change the value going in instead.
            var setInteractable = AccessTools.Method(AccessTools.TypeByName("KHDFGIINMKJ"), "SetInteractable", new[] { typeof(Selectable), typeof(bool) });
            if (setInteractable != null)
                harmony.Patch(setInteractable, prefix: new HarmonyMethod(typeof(RevivalBriefing), nameof(SetInteractable_Prefix)));
            else
                Plugin.Log.LogWarning("Revival missions: KHDFGIINMKJ.SetInteractable not found; ship choice may be greyed out");
        }

        // ---- The selection, shared through the room ---------------------------------------------------

        /// <summary>The Revival mission chosen in this room's briefing, or null.</summary>
        internal static string RoomSelection()
        {
            var room = Room();
            var props = room?.GetType().GetMethod("get_customProperties", All)?.Invoke(room, null) as IDictionary;
            var value = props != null && props.Contains(RoomKey) ? props[RoomKey] as string : null;
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private static void SetRoomSelection(string mission)
        {
            var room = Room();
            if (room == null) return;
            var set = room.GetType().GetMethod("SetCustomProperties", All);
            set?.Invoke(room, new object[] { new PhotonHashtable { [RoomKey] = mission ?? "" }, null, false });
            Plugin.Log.LogInfo($"Revival missions: briefing selection {(mission ?? "cleared")}");
        }

        private static object Room() =>
            Patches.PhotonNetworkType?.GetMethod("get_room", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);

        // ---- The toggle ------------------------------------------------------------------------------

        private static T Field<T>(ConsoleBriefingRoom console, string name) where T : class =>
            AccessTools.Field(typeof(ConsoleBriefingRoom), name)?.GetValue(console) as T;

        private static void Start_Postfix(ConsoleBriefingRoom __instance)
        {
            var console = __instance;
            try
            {
                var procedural = Field<Toggle>(console, "_proceduralMissionToggle");
                var campaign = Field<Toggle>(console, "_campaignMissionToggle");
                if (procedural == null || campaign == null) return;

                // Same look, same group. The GAME TYPE box holds two rows, from half a row above Campaign to half
                // a row below Ongoing Voyages; space three rows evenly in that span instead.
                var copy = (GameObject)UnityEngine.Object.Instantiate(procedural.gameObject, procedural.transform.parent);
                copy.name = "Revival Missions Toggle";
                Vector3 first = campaign.transform.localPosition, step = procedural.transform.localPosition - first;
                campaign.transform.localPosition = first + step * (-1f / 6f);
                procedural.transform.localPosition = first + step * 0.5f;
                copy.transform.localPosition = first + step * (7f / 6f);
                copy.transform.SetSiblingIndex(procedural.transform.GetSiblingIndex() + 1);
                var revivalToggle = copy.GetComponent<Toggle>();
                revivalToggle.onValueChanged.RemoveAllListeners();
                revivalToggle.isOn = false;
                foreach (var label in copy.GetComponentsInChildren<TMP_Text>(true)) label.SetLocalizedText("REVIVAL MISSIONS");
                revivalToggle.onValueChanged.AddListener(on => OnToggled(console, on));
                toggles[console] = revivalToggle;
                foreach (var ship in ShipFields)
                {
                    var toggle = Field<Toggle>(console, ship);
                    if (toggle != null) shipToggles[toggle] = console;
                }
                Plugin.Log.LogInfo("Revival missions: added the REVIVAL MISSIONS category to the briefing console");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Revival missions: couldn't add the briefing category: " + e.Message);
            }
        }

        // The captain picks; for everyone else the toggle just shows the room's choice.
        private static void OnToggled(ConsoleBriefingRoom console, bool on)
        {
            if (applying || !IsChooser(console)) return;
            if (on)
            {
                SetRoomSelection(RevivalMissions.LastStand);
                Loader.SetMissionType(Loader.DAPMGBKPDNM.Procedural);
                if (Loader.EJLNDBGLIFL.GetMissionTableName() == null) Loader.SelectNextProceduralMissionTable();
            }
            else if (RoomSelection() != null)
                SetRoomSelection(null);
        }

        // Whoever can change the category: the console makes exactly the unselected categories interactable
        // for them (ConsoleBriefingRoom.KJCBOFBCMAN), so read it from there.
        private static bool IsChooser(ConsoleBriefingRoom console)
        {
            var procedural = Field<Toggle>(console, "_proceduralMissionToggle");
            var campaign = Field<Toggle>(console, "_campaignMissionToggle");
            return procedural != null && campaign != null
                   && ((!procedural.isOn && procedural.interactable) || (!campaign.isOn && campaign.interactable));
        }

        private static void Refresh_Postfix(ConsoleBriefingRoom __instance)
        {
            var console = __instance;
            if (!toggles.TryGetValue(console, out var revivalToggle) || revivalToggle == null) return;
            try
            {
                var selected = RoomSelection();
                bool chooser = IsChooser(console);
                applying = true;
                if (revivalToggle.isOn != (selected != null)) revivalToggle.isOn = selected != null;
                revivalToggle.interactable = chooser && selected == null;
                var procedural = Field<Toggle>(console, "_proceduralMissionToggle");
                var campaign = Field<Toggle>(console, "_campaignMissionToggle");
                CopyLabelColour(console, revivalToggle, selected != null || chooser);
                if (selected != null && chooser) choosingRevival.Add(console); else choosingRevival.Remove(console);
                if (selected == null) return;

                if (procedural.isOn) procedural.isOn = false;
                if (campaign.isOn) campaign.isOn = false;
                procedural.interactable = chooser;
                campaign.interactable = chooser;
                if (chooser && Loader.GetMissionType() != Loader.DAPMGBKPDNM.Procedural)
                    Loader.SetMissionType(Loader.DAPMGBKPDNM.Procedural);

                // Ships as for an Ongoing Voyage (the console greys their names unless Ongoing Voyages is
                // selected); no next/previous mission (there's one Revival mission so far).
                foreach (var ship in ShipFields)
                {
                    var toggle = Field<Toggle>(console, ship);
                    if (toggle == null) continue;
                    var label = Field<TMP_Text>(console, ship + "Text");
                    bool tng = ship == "_tngToggle";
                    if (label != null)
                        label.color = Colour(console, (toggle.isOn || chooser)
                            ? (tng ? "_enabledOptionsColorTNG" : "_enabledOptionsColor")
                            : (tng ? "_disabledOptionsColorTNG" : "_disabledOptionsColor"));
                }
                foreach (var button in new[] { "_nextMissionButton", "_previousMissionButton" })
                {
                    var b = Field<Button>(console, button);
                    if (b != null) b.interactable = false;
                }
                Field<TMP_Text>(console, "_experienceWarningText")?.gameObject.SetActive(false);
                SetText(Field<TMP_Text>(console, "_missionName"), RevivalMissions.Name(selected));
                SetText(Field<TMP_Text>(console, "_missionDescriptionText"), RevivalMissions.Description(selected));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Revival missions: briefing refresh: " + e.Message);
                toggles[console] = null; // don't repeat every frame
            }
            finally
            {
                applying = false;
            }
        }

        // A ship toggle on a console where this player is choosing a Revival mission: choosable unless it's
        // the current ship (as the console does for an Ongoing Voyage).
        private static void SetInteractable_Prefix(Selectable __0, ref bool __1)
        {
            if (__0 != null && !__1 && shipToggles.TryGetValue(__0, out var console) && choosingRevival.Contains(console))
                __1 = !((Toggle)__0).isOn;
        }

        private static Color Colour(ConsoleBriefingRoom console, string field) =>
            (Color)AccessTools.Field(typeof(ConsoleBriefingRoom), field).GetValue(console);

        private static void CopyLabelColour(ConsoleBriefingRoom console, Toggle to, bool enabled)
        {
            foreach (var label in to.GetComponentsInChildren<TMP_Text>(true))
                label.color = enabled ? (Color)AccessTools.Field(typeof(ConsoleBriefingRoom), "_enabledOptionsColor").GetValue(console)
                                      : (Color)AccessTools.Field(typeof(ConsoleBriefingRoom), "_disabledOptionsColor").GetValue(console);
        }

        private static readonly MethodInfo GetText = AccessTools.Method(typeof(TMP_Text), "get_text");

        private static void SetText(TMP_Text text, string value)
        {
            if (text != null && GetText?.Invoke(text, null) as string != value) text.SetLocalizedText(value);
        }
    }
}
