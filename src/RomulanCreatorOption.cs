using System;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StbcRevival
{
    /// <summary>
    /// A ROMULAN species choice in the character creator, after Human, Vulcan (and Android), shown while
    /// Female is picked. It's a copy of the Vulcan toggle in the same group: picking it picks Vulcan for the
    /// game (the faces are the female Vulcan presets) and turns [Avatar] RomulanFace on; picking any other
    /// species turns it off. See RomulanFace for the face itself.
    /// </summary>
    internal static class RomulanCreatorOption
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static Toggle romulan;
        private static CustomizationManager creator;
        private static Toggle[] rows;           // the species toggles shown with ROMULAN, top to bottom
        private static Vector3[] rowPositions;  // their own positions (ROMULAN hidden)
        private static Vector3 rowStep;
        private static bool laidOutFor;
        private static bool applying;

        private static readonly FieldInfo Gender = AccessTools.Field(typeof(CustomizationManager), "EPNEDLICCMJ");
        private static readonly FieldInfo Species = AccessTools.Field(typeof(CustomizationManager), "COABBGKIHFL");
        private static readonly MethodInfo PickVulcan = AccessTools.Method(typeof(CustomizationManager), "PHHOOCMCLIJ");

        internal static void Patch(Harmony harmony)
        {
            var init = AccessTools.Method(typeof(CustomizationManager), "OGEINMNDPMI");    // first set-up: listeners, Android toggle
            var refresh = AccessTools.Method(typeof(CustomizationManager), "KPAOOOCNEKP"); // shows the current gender and species
            if (init == null || refresh == null || Gender == null || Species == null || PickVulcan == null)
            {
                Plugin.Log.LogWarning("Romulan face: character creator methods not found; no ROMULAN choice in the creator");
                return;
            }
            harmony.Patch(init, postfix: new HarmonyMethod(typeof(RomulanCreatorOption), nameof(Init_Postfix)));
            harmony.Patch(refresh, postfix: new HarmonyMethod(typeof(RomulanCreatorOption), nameof(Refresh_Postfix)));
        }

        private static Toggle Field(CustomizationManager creator, string name) =>
            AccessTools.Field(typeof(CustomizationManager), name)?.GetValue(creator) as Toggle;

        private static void Init_Postfix(CustomizationManager __instance)
        {
            if (romulan != null) return;
            try
            {
                var human = Field(__instance, "_humanRaceToggle");
                var vulcan = Field(__instance, "_vulcanRaceToggle");
                var android = Field(__instance, "_androidRaceToggle");
                if (human == null || vulcan == null) return;

                // A row after the last species shown (Android is only there with the TNG content).
                var last = android != null && android.gameObject.activeSelf ? android : vulcan;
                var copy = (GameObject)UnityEngine.Object.Instantiate(vulcan.gameObject, vulcan.transform.parent);
                copy.name = "Romulan Race Toggle";
                copy.transform.SetSiblingIndex(last.transform.GetSiblingIndex() + 1);
                romulan = copy.GetComponent<Toggle>();
                creator = __instance;
                rows = last == android ? new[] { human, vulcan, android } : new[] { human, vulcan };
                rowPositions = Array.ConvertAll(rows, r => r.transform.localPosition);
                rowStep = vulcan.transform.localPosition - human.transform.localPosition;
                laidOutFor = true;
                Layout(false);
                copy.SetActive(false); // Update shows it (and makes room) while Female is picked
                romulan.onValueChanged.RemoveAllListeners();
                romulan.isOn = false;
                foreach (var label in copy.GetComponentsInChildren<TMP_Text>(true))
                {
                    var text = GetText?.Invoke(label, null) as string;
                    label.SetLocalizedText(text != null && text == text.ToUpperInvariant() ? "ROMULAN" : "Romulan");
                }
                romulan.onValueChanged.AddListener(on => Toggled(__instance, on));
                Plugin.Log.LogInfo("Romulan face: added the ROMULAN choice to the character creator ("
                                   + string.Join(", ", Array.ConvertAll(copy.GetComponentsInChildren<Component>(true), c => c.GetType().Name)) + ")");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Romulan face: couldn't add the creator choice: " + e.Message);
            }
        }

        private static void Toggled(CustomizationManager creator, bool on)
        {
            if (applying) return;
            if (on)
            {
                Plugin.RomulanFace.Value = true;
                // Vulcan for the game; the Vulcan handler only acts on being switched on.
                if (Species.GetValue(creator).ToString() != "Vulcan") PickVulcan.Invoke(creator, new object[] { true });
                if (romulan.group == null) // not grouped with the others: clear them ourselves
                    foreach (var name in new[] { "_humanRaceToggle", "_vulcanRaceToggle", "_androidRaceToggle" })
                        if (Field(creator, name) is Toggle other && other.isOn) Set(other, false);
                Plugin.Log.LogInfo("Romulan face: Romulan picked in the creator");
            }
            else if (Plugin.RomulanFace.Value)
            {
                Plugin.RomulanFace.Value = false; // another species (or Male) picked
                Plugin.Log.LogInfo("Romulan face: another species picked in the creator");
            }
        }

        // ROMULAN only while Female is picked (checked every frame: the creator doesn't always refresh on a
        // gender change). Hidden while chosen: Vulcan shows instead; the choice is kept for Female.
        internal static void Update()
        {
            if (romulan == null || creator == null) return;
            bool female = Gender.GetValue(creator).ToString() == "Female";
            if (romulan.gameObject.activeSelf == female) return;
            if (!female && romulan.isOn && Field(creator, "_vulcanRaceToggle") is Toggle vulcan)
            {
                Set(vulcan, true);
                if (romulan.group == null) Set(romulan, false);
            }
            romulan.gameObject.SetActive(female);
            Layout(female);
        }

        // The species box holds its rows from half a row above the first to half a row below the last;
        // with ROMULAN, space one more row evenly in that span.
        private static void Layout(bool withRomulan)
        {
            if (rows == null || laidOutFor == withRomulan) return;
            laidOutFor = withRomulan;
            int n = rows.Length;
            if (!withRomulan)
            {
                for (int i = 0; i < n; i++) rows[i].transform.localPosition = rowPositions[i];
                return;
            }
            var step = rowStep * (n / (float)(n + 1));
            var first = rowPositions[0] - rowStep * 0.5f + step * 0.5f;
            for (int i = 0; i < n; i++) rows[i].transform.localPosition = first + step * i;
            romulan.transform.localPosition = first + step * n;
        }

        // After the game shows the current choice: ROMULAN on in place of Vulcan if chosen.
        private static void Refresh_Postfix(CustomizationManager __instance)
        {
            if (romulan == null) return;
            try
            {
                bool female = Gender.GetValue(__instance).ToString() == "Female";
                bool show = female && Species.GetValue(__instance).ToString() == "Vulcan" && Plugin.RomulanFace.Value;
                if (romulan.isOn == show) return;
                Set(romulan, show);
                if (show && romulan.group == null && Field(__instance, "_vulcanRaceToggle") is Toggle vulcan) Set(vulcan, false);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Romulan face: creator refresh: " + e.Message);
                romulan = null;
            }
        }

        private static void Set(Toggle toggle, bool on)
        {
            applying = true;
            try { toggle.isOn = on; }
            finally { applying = false; }
        }

        private static readonly MethodInfo GetText = AccessTools.Method(typeof(TMP_Text), "get_text");
    }
}
