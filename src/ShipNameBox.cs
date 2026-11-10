using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StbcRevival
{
    /// <summary>
    /// The briefing console's SHIP NAME box: a copy of its SHIP box, above GAME TYPE, with three rows: the registry's
    /// prefix (NCC or NX, a click switches), its number and the ship's name (a click asks, on the desktop or on
    /// SteamVR's keyboard). The host or the captain changes it for the room; everyone sees it.
    /// </summary>
    internal static class ShipNameBox
    {
        private sealed class Box
        {
            public ConsoleBriefingRoom Console;
            public GameObject Root;
            public Toggle Prefix, Number, Name;
        }

        private static readonly List<Box> boxes = new List<Box>();
        private static float nextRefresh;
        private static readonly Dictionary<TMP_Text, string> shown = new Dictionary<TMP_Text, string>();
        private const float Scale = 0.75f; // the copy is smaller: the space above GAME TYPE is lower than the SHIP box

        internal static void Patch(Harmony harmony)
        {
            var start = AccessTools.Method(typeof(ConsoleBriefingRoom), "Start");
            if (start == null) { Plugin.Log.LogWarning("Ship name box: the briefing console wasn't found"); return; }
            harmony.Patch(start, postfix: new HarmonyMethod(typeof(ShipNameBox), nameof(Start_Postfix)) { priority = Priority.Low });
        }

        private static T Field<T>(ConsoleBriefingRoom console, string name) where T : class =>
            AccessTools.Field(typeof(ConsoleBriefingRoom), name)?.GetValue(console) as T;

        /// <summary>The highest ancestor of a that doesn't also hold b: a's whole box.</summary>
        private static Transform BoxOf(Transform a, Transform b)
        {
            var box = a;
            while (box.parent != null && !b.IsChildOf(box.parent)) box = box.parent;
            return box;
        }

        /// <summary>The rectangle a box's graphics cover, in its parent's space.</summary>
        private static Rect Extent(Transform box)
        {
            var parent = box.parent;
            float xMin = float.MaxValue, yMin = float.MaxValue, xMax = float.MinValue, yMax = float.MinValue;
            var corners = new Vector3[4];
            foreach (var g in box.GetComponentsInChildren<Graphic>(true))
            {
                g.rectTransform.GetWorldCorners(corners);
                foreach (var c in corners)
                {
                    var p = parent.InverseTransformPoint(c);
                    xMin = Mathf.Min(xMin, p.x); xMax = Mathf.Max(xMax, p.x); yMin = Mathf.Min(yMin, p.y); yMax = Mathf.Max(yMax, p.y);
                }
            }
            return xMin > xMax ? new Rect() : Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        private static void Start_Postfix(ConsoleBriefingRoom __instance)
        {
            var console = __instance;
            try
            {
                var aegis = Field<Toggle>(console, "_aegisToggle");
                var enterprise = Field<Toggle>(console, "_enterpriseToggle");
                var tng = Field<Toggle>(console, "_tngToggle");
                var campaign = Field<Toggle>(console, "_campaignMissionToggle");
                if (aegis == null || enterprise == null || tng == null || campaign == null) return;

                var shipBox = BoxOf(aegis.transform, campaign.transform);
                var typeBox = BoxOf(campaign.transform, aegis.transform);
                if (shipBox.parent != typeBox.parent) { Plugin.Log.LogWarning("Ship name box: SHIP and GAME TYPE aren't side by side; no box"); return; }
                Rect ship = Extent(shipBox), type = Extent(typeBox);
                Plugin.Log.LogInfo($"Ship name box: SHIP box {shipBox.name} {ship}, GAME TYPE box {typeBox.name} {type}");

                var root = (GameObject)UnityEngine.Object.Instantiate(shipBox.gameObject, shipBox.parent, false);
                root.name = "Ship Name Box";
                root.transform.localScale = shipBox.localScale * Scale;
                // Its bottom just above GAME TYPE's top, left edges lined up.
                Vector2 offset = new Vector2(ship.xMin, ship.yMin) - (Vector2)shipBox.localPosition;
                Vector2 at = new Vector2(type.xMin, type.yMax + 0.06f * type.height) - offset * Scale;
                root.transform.localPosition = new Vector3(at.x, at.y, shipBox.localPosition.z);

                // The copies of the three ship toggles: Aegis and Enterprise as they are, the Enterprise-D's (black and
                // orange) swapped for another plain one.
                Toggle Copy(Toggle original) => root.GetComponentsInChildren<Toggle>(true).FirstOrDefault(t => t.name == original.name && t.transform.localPosition == original.transform.localPosition);
                var first = Copy(aegis); var second = Copy(enterprise); var third = Copy(tng);
                if (first == null || second == null || third == null) { UnityEngine.Object.Destroy(root); Plugin.Log.LogWarning("Ship name box: the copied toggles weren't found"); return; }
                var plain = (GameObject)UnityEngine.Object.Instantiate(second.gameObject, third.transform.parent, false);
                plain.transform.localPosition = third.transform.localPosition;
                plain.transform.SetSiblingIndex(third.transform.GetSiblingIndex());
                UnityEngine.Object.Destroy(third.gameObject);

                var box = new Box { Console = console, Root = root, Prefix = first, Number = second, Name = plain.GetComponent<Toggle>() };
                var rows = new[] { box.Prefix, box.Number, box.Name };
                var actions = new Action[] { SwitchPrefix, AskNumber, AskName };
                for (int i = 0; i < rows.Length; i++)
                {
                    var toggle = rows[i]; var action = actions[i];
                    toggle.group = null;
                    toggle.onValueChanged.RemoveAllListeners();
                    toggle.isOn = false;
                    toggle.onValueChanged.AddListener(on => { if (!on) return; toggle.isOn = false; action(); });
                }
                // Its heading: the SHIP box's own text outside the toggles.
                foreach (var label in root.GetComponentsInChildren<TMP_Text>(true))
                    if (label.GetComponentInParent<Toggle>() == null) label.SetLocalizedText(Translation.T("SHIP NAME"));
                boxes.Add(box);
                Refresh(box);

                // Decorations under the new box (the console's circuit drawing there) go.
                var mine = Extent(root.transform);
                foreach (Transform sibling in shipBox.parent)
                {
                    if (sibling == root.transform || sibling == shipBox || sibling == typeBox || sibling.GetComponentInChildren<Selectable>(true) != null || sibling.GetComponentInChildren<TMP_Text>(true) != null) continue;
                    var r = Extent(sibling);
                    if (r.width > 0f && mine.Contains(r.center) && r.width < mine.width * 1.5f && r.height < mine.height * 1.5f)
                    {
                        Plugin.Log.LogInfo($"Ship name box: hiding the decoration {sibling.name} {r} under it");
                        sibling.gameObject.SetActive(false);
                    }
                }
                Plugin.Log.LogInfo($"Ship name box: added at {mine}");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Ship name box: " + e); }
        }

        // ---- the ship name's parts ----

        private static readonly Regex RegistryParts = new Regex(@"^\s*(NCC|NX)?[\s\-]*(.*)$", RegexOptions.IgnoreCase);

        private static void Parts(out string prefix, out string number, out string name)
        {
            name = ShipName.Name; string registry = ShipName.Registry;
            if (name.Length == 0) { name = "USS Aegis"; registry = "NX-1787"; } // the game's, until someone names it
            var m = RegistryParts.Match(registry);
            prefix = m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value.ToUpperInvariant() : "NCC";
            number = m.Groups[2].Value.Trim();
        }

        private static void SetParts(string prefix, string number, string name)
        {
            name = name.Trim();
            number = number.Trim();
            string value = name.Length == 0 ? "" : number.Length > 0 ? $"{name}, {prefix}-{number}" : name;
            ShipName.Set(value, true);
            Plugin.Log.LogInfo($"Ship name box: \"{value}\"");
            nextRefresh = 0f;
        }

        private static bool CanEdit()
        {
            if (Missions.RevivalBriefing.HostingOrAlone()) return true;
            try { var vrm = VoiceRecognitionManager.IEFBKOMBFGO; return vrm != null && vrm.CurrentRoleIsCaptain(); }
            catch { return false; }
        }

        private static void SwitchPrefix()
        {
            if (!CanEdit()) return;
            Parts(out var prefix, out var number, out var name);
            SetParts(prefix == "NCC" ? "NX" : "NCC", number, name);
        }

        private static void AskNumber()
        {
            if (!CanEdit()) return;
            Parts(out var prefix, out var number, out var name);
            Ask(Translation.T("Registry number (e.g. 91234)"), 12, text => SetParts(prefix, text ?? number, name));
        }

        private static void AskName()
        {
            if (!CanEdit()) return;
            Parts(out var prefix, out var number, out var name);
            Ask(Translation.T("Ship name (e.g. USS Cosmo); empty for the game's"), 30, text => SetParts(prefix, number, text ?? name));
        }

        private static void Ask(string what, int max, Action<string> done)
        {
            if (UnityEngine.VR.VRSettings.enabled && UnityEngine.VR.VRSettings.loadedDeviceName == "OpenVR") VrKeyboard.Open(what, (uint)max, done);
            else TextPrompt.Ask(what, false, done);
        }

        // ---- keeping the rows current ----

        internal static void Update()
        {
            if (boxes.Count == 0 || Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + 0.5f;
            if (boxes.RemoveAll(b => b.Root == null) > 0) foreach (var gone in shown.Keys.Where(k => k == null).ToList()) shown.Remove(gone);
            foreach (var b in boxes) Refresh(b);
        }

        private static void Refresh(Box b)
        {
            try
            {
                Parts(out var prefix, out var number, out var name);
                bool edit = CanEdit();
                Label(b.Prefix, Translation.T("PREFIX") + ": " + prefix, edit);
                Label(b.Number, Translation.T("NUMBER") + ": " + (number.Length > 0 ? number : "-"), edit);
                Label(b.Name, Translation.T("NAME") + ": " + name.ToUpperInvariant(), edit);
            }
            catch (Exception e) { Plugin.Log.LogDebug("Ship name box: " + e.Message); }
        }

        private static void Label(Toggle toggle, string text, bool interactable)
        {
            if (toggle == null) return;
            if (toggle.interactable != interactable) toggle.interactable = interactable;
            foreach (var label in toggle.GetComponentsInChildren<TMP_Text>(true))
                if (!shown.TryGetValue(label, out var was) || was != text) { label.SetLocalizedText(text); shown[label] = text; }
        }
    }
}
