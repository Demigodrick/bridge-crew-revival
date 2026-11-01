using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StbcRevival
{
    /// <summary>
    /// Extra species choices in the character creator, after Human, Vulcan (and Android): ROMULAN while
    /// Female is picked, KLINGON while Male is. Each is a copy of the Vulcan toggle in the same group; picking
    /// it picks its base species for the game (Vulcan for Romulan: the female Vulcan presets; Human for
    /// Klingon: the male Human presets) and turns its setting on ([Avatar] RomulanFace, KlingonHead); picking
    /// any other species turns it off. See RomulanFace and KlingonHead for the heads themselves.
    /// </summary>
    internal static class SpeciesCreatorOptions
    {
        private sealed class Option
        {
            public string Label, Gender, BaseToggle, BaseSpecies, PickMethod, Log;
            public Func<bool> Chosen;
            public Action<bool> Choose;
            public Toggle Toggle;
            public MethodInfo Pick;
        }

        private static readonly Option[] Options =
        {
            new Option
            {
                Label = "Romulan", Gender = "Female", BaseToggle = "_vulcanRaceToggle", BaseSpecies = "Vulcan", PickMethod = "PHHOOCMCLIJ", Log = "Romulan face",
                Chosen = () => Plugin.RomulanFace.Value, Choose = on => Plugin.RomulanFace.Value = on,
            },
            new Option
            {
                Label = "Klingon", Gender = "Male", BaseToggle = "_humanRaceToggle", BaseSpecies = "Human", PickMethod = "DADHMIIKIME", Log = "Klingon head",
                Chosen = () => Plugin.KlingonHead.Value > 0, Choose = on => Plugin.KlingonHead.Value = on ? Math.Max(1, Plugin.KlingonHead.Value) : 0,
            },
        };

        private static readonly string[] SpeciesToggles = { "_humanRaceToggle", "_vulcanRaceToggle", "_androidRaceToggle" };

        private static CustomizationManager creator;
        private static Toggle[] rows;           // the species toggles shown with an extra one, top to bottom
        private static Vector3[] rowPositions;  // their own positions (no extra one shown)
        private static Vector3 rowStep;
        private static Option laidOutFor;      // the extra choice the rows are spaced for (null: their own places)
        private static bool applying;

        private static readonly FieldInfo Gender = AccessTools.Field(typeof(CustomizationManager), "EPNEDLICCMJ");
        private static readonly FieldInfo Species = AccessTools.Field(typeof(CustomizationManager), "COABBGKIHFL");

        internal static void Patch(Harmony harmony)
        {
            var init = AccessTools.Method(typeof(CustomizationManager), "OGEINMNDPMI");    // first set-up: listeners, Android toggle
            var refresh = AccessTools.Method(typeof(CustomizationManager), "KPAOOOCNEKP"); // shows the current gender and species
            foreach (var option in Options) option.Pick = AccessTools.Method(typeof(CustomizationManager), option.PickMethod);
            if (init == null || refresh == null || Gender == null || Species == null || Options.Any(o => o.Pick == null))
            {
                Plugin.Log.LogWarning("Creator species: character creator methods not found; no ROMULAN or KLINGON choice in the creator");
                return;
            }
            harmony.Patch(init, postfix: new HarmonyMethod(typeof(SpeciesCreatorOptions), nameof(Init_Postfix)));
            harmony.Patch(refresh, postfix: new HarmonyMethod(typeof(SpeciesCreatorOptions), nameof(Refresh_Postfix)));
            // A VR press ends in HandInteraction.OnPointerUp; pick the choice from there in case the toggle's
            // own click never arrives.
            var pressed = AccessTools.Method(typeof(HandInteraction), "OnPointerUp");
            if (pressed != null)
                harmony.Patch(pressed, postfix: new HarmonyMethod(typeof(SpeciesCreatorOptions), nameof(Pressed_Postfix)));
        }

        private static Toggle Field(CustomizationManager creator, string name) =>
            AccessTools.Field(typeof(CustomizationManager), name)?.GetValue(creator) as Toggle;

        private static void Init_Postfix(CustomizationManager __instance)
        {
            if (Options[0].Toggle != null) return;
            try
            {
                var human = Field(__instance, "_humanRaceToggle");
                var vulcan = Field(__instance, "_vulcanRaceToggle");
                var android = Field(__instance, "_androidRaceToggle");
                if (human == null || vulcan == null) return;

                // A row after the last species shown (Android is only there with the TNG content).
                var last = android != null && android.gameObject.activeSelf ? android : vulcan;
                creator = __instance;
                rows = last == android ? new[] { human, vulcan, android } : new[] { human, vulcan };
                rowPositions = Array.ConvertAll(rows, r => r.transform.localPosition);
                rowStep = vulcan.transform.localPosition - human.transform.localPosition;
                int sibling = last.transform.GetSiblingIndex();
                foreach (var option in Options)
                {
                    var copy = (GameObject)UnityEngine.Object.Instantiate(vulcan.gameObject, vulcan.transform.parent);
                    copy.name = option.Label + " Race Toggle";
                    // HandInteraction.Awake builds trigger helpers under its toggle; the copy brought Vulcan's along
                    // (and built its own). Drop Vulcan's so only the copy's own triggers are pressed.
                    foreach (Transform child in copy.transform)
                        if (child.name.StartsWith(vulcan.name + "_", StringComparison.Ordinal))
                            UnityEngine.Object.Destroy(child.gameObject);
                    copy.transform.SetSiblingIndex(++sibling);
                    var toggle = copy.GetComponent<Toggle>();
                    option.Toggle = toggle;
                    copy.SetActive(false); // Update shows it (and makes room) while its gender is picked
                    toggle.onValueChanged.RemoveAllListeners();
                    toggle.isOn = false;
                    foreach (var label in copy.GetComponentsInChildren<TMP_Text>(true))
                    {
                        var text = GetText?.Invoke(label, null) as string;
                        label.SetLocalizedText(text != null && text == text.ToUpperInvariant() ? option.Label.ToUpperInvariant() : option.Label);
                    }
                    var picked = option;
                    toggle.onValueChanged.AddListener(on => Toggled(__instance, picked, on));
                    Plugin.Log.LogInfo($"{option.Log}: added the {option.Label.ToUpperInvariant()} choice to the character creator");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Creator species: couldn't add the creator choices: " + e.Message);
            }
        }

        private static void Toggled(CustomizationManager creator, Option option, bool on)
        {
            if (applying) return;
            if (on)
            {
                option.Choose(true);
                // The base species for the game; its handler only acts on being switched on.
                if (Species.GetValue(creator).ToString() != option.BaseSpecies) option.Pick.Invoke(creator, new object[] { true });
                if (option.Toggle.group == null) // not grouped with the others: clear them ourselves
                {
                    foreach (var name in SpeciesToggles)
                        if (Field(creator, name) is Toggle other && other.isOn) Set(other, false);
                    foreach (var other in Options)
                        if (other != option && other.Toggle != null && other.Toggle.isOn) Set(other.Toggle, false);
                }
                Plugin.Log.LogInfo($"{option.Log}: {option.Label} picked in the creator");
            }
            else if (option.Chosen())
            {
                option.Choose(false); // another species picked
                Plugin.Log.LogInfo($"{option.Log}: another species picked in the creator");
            }
        }

        // Each choice only while its gender is picked (checked every frame: the creator doesn't always refresh
        // on a gender change). Hidden while chosen: its base species shows instead; the choice is kept.
        internal static void Update()
        {
            if (creator == null || Options[0].Toggle == null) return;
            string gender = Gender.GetValue(creator).ToString();
            Option shown = null;
            foreach (var option in Options)
            {
                bool show = gender == option.Gender;
                if (show) shown = option;
                if (option.Toggle.gameObject.activeSelf == show || show) continue;
                if (option.Toggle.isOn && Field(creator, option.BaseToggle) is Toggle baseToggle)
                {
                    Set(baseToggle, true);
                    if (option.Toggle.group == null) Set(option.Toggle, false);
                }
                option.Toggle.gameObject.SetActive(false);
            }
            if (shown != null && !shown.Toggle.gameObject.activeSelf)
            {
                Layout(shown);
                shown.Toggle.gameObject.SetActive(true);
                TellHands(shown);
            }
            Sync(creator);
        }

        // The VR hands only reach the controls they collected when the console loaded
        // (HandAnimator.UpdateHandInteractionsForConsole); the game re-collects with
        // HandAnimatorManager.NotifyAnimatorsForConsole when it adds buttons itself (warp panel, targeting).
        // Camera focus areas keep their own list too.
        private static void TellHands(Option option)
        {
            try
            {
                foreach (var area in option.Toggle.GetComponentsInParent<CameraFocusArea>(true)) area.RecacheChildHandInteractions();
                var console = option.Toggle.GetComponentInParent<ConsoleBase>();
                var manager = AccessTools.Method(AccessTools.TypeByName("HandAnimatorManager"), "get_Instance")?.Invoke(null, null);
                if (console == null || manager == null)
                {
                    Plugin.Log.LogWarning($"{option.Log}: can't tell the hands about {option.Label.ToUpperInvariant()} (console {console != null}, hand manager {manager != null})");
                    return;
                }
                AccessTools.Method(manager.GetType(), "NotifyAnimatorsForConsole").Invoke(manager, new object[] { console });
                Plugin.Log.LogInfo($"{option.Log}: hands told about {option.Label.ToUpperInvariant()} on {console.name}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{option.Log}: telling the hands about {option.Label.ToUpperInvariant()}: {e.Message}");
            }
        }

        // The species box holds its rows from half a row above the first to half a row below the last;
        // with an extra choice, space one more row evenly in that span.
        private static void Layout(Option extra)
        {
            if (rows == null || laidOutFor == extra) return;
            laidOutFor = extra;
            int n = rows.Length;
            if (extra == null)
            {
                for (int i = 0; i < n; i++) rows[i].transform.localPosition = rowPositions[i];
                return;
            }
            var step = rowStep * (n / (float)(n + 1));
            var first = rowPositions[0] - rowStep * 0.5f + step * 0.5f;
            for (int i = 0; i < n; i++) rows[i].transform.localPosition = first + step * i;
            extra.Toggle.transform.localPosition = first + step * n;
        }

        // After the game shows the current choice (only on Undo: the creator opens showing the saved species),
        // and every frame: an extra choice on in place of its base species if chosen, so the buttons say what
        // the avatar wears.
        private static void Refresh_Postfix(CustomizationManager __instance) => Sync(__instance);

        private static void Sync(CustomizationManager __instance)
        {
            if (Options[0].Toggle == null || __instance == null) return;
            try
            {
                string gender = Gender.GetValue(__instance).ToString();
                string species = Species.GetValue(__instance).ToString();
                foreach (var option in Options)
                {
                    bool show = gender == option.Gender && species == option.BaseSpecies && option.Chosen();
                    if (option.Toggle.isOn == show) continue;
                    Set(option.Toggle, show);
                    if (show && option.Toggle.group == null && Field(__instance, option.BaseToggle) is Toggle baseToggle) Set(baseToggle, false);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Creator species: creator refresh: " + e.Message);
                foreach (var option in Options) option.Toggle = null;
            }
        }

        private static void Pressed_Postfix(HandInteraction __instance)
        {
            if (__instance == null) return;
            foreach (var option in Options)
            {
                if (option.Toggle == null || __instance.gameObject != option.Toggle.gameObject) continue;
                Plugin.Log.LogInfo($"{option.Log}: {option.Label.ToUpperInvariant()} pressed (on: {option.Toggle.isOn})");
                Plugin.Instance.StartCoroutine(PickAfterClick(option.Toggle));
            }
        }

        // A frame later, so a click the toggle does get isn't undone by picking it twice.
        private static System.Collections.IEnumerator PickAfterClick(Toggle toggle)
        {
            yield return null;
            if (toggle != null && !toggle.isOn && toggle.IsInteractable()) toggle.isOn = true; // fires Toggled
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
