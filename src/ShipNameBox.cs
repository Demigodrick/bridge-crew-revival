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
        private const float Scale = 0.7f; // the copy is smaller: the space above GAME TYPE is lower than the SHIP box

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
        private static Rect Extent(Transform box) => ExtentIn(box, box.parent);

        /// <summary>The rectangle a box's graphics cover, in another transform's space.</summary>
        private static Rect ExtentIn(Transform box, Transform parent)
        {
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

        /// <summary>
        /// The parent's children that make up the box around these toggles: anything (frame, heading, lines) lying
        /// within their area widened a little, and a heading's height above it, that isn't another control.
        /// </summary>
        private static List<Transform> Members(Transform parent, IList<Transform> core, ICollection<Transform> skip, out Rect area)
        {
            area = core.Select(Extent).Aggregate((a, b) => Rect.MinMaxRect(Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin), Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax)));
            float row = core.Count > 1 ? area.height / core.Count : area.height;
            var wide = Rect.MinMaxRect(area.xMin - 0.25f * area.width, area.yMin - 0.8f * row, area.xMax + 0.25f * area.width, area.yMax + 1.8f * row);
            var members = new List<Transform>(core);
            foreach (Transform child in parent)
            {
                if (members.Contains(child) || skip.Contains(child) || !child.gameObject.activeSelf) continue;
                if (child.GetComponentsInChildren<Selectable>(true).Length > 0) continue;
                var r = Extent(child);
                if (r.width <= 0f || !wide.Contains(r.min) || !wide.Contains(r.max)) continue;
                members.Add(child);
            }
            foreach (var m in members)
            {
                var r = Extent(m);
                area = Rect.MinMaxRect(Mathf.Min(area.xMin, r.xMin), Mathf.Min(area.yMin, r.yMin), Mathf.Max(area.xMax, r.xMax), Mathf.Max(area.yMax, r.yMax));
            }
            return members;
        }

        private static string Describe(IEnumerable<Transform> ts) => string.Join(", ", ts.Select(t => $"{t.name} {Extent(t)}").ToArray());

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

                // The SHIP box's pieces are side by side in one panel: its three toggles and whatever lies around them.
                var parent = aegis.transform.parent;
                if (enterprise.transform.parent != parent || tng.transform.parent != parent) { Plugin.Log.LogWarning("Ship name box: the ship toggles aren't side by side; no box"); return; }
                var typeBox = BoxOf(campaign.transform, aegis.transform);
                var tngBacks = new[] { Field<Image>(console, "_tngToggleBackground1"), Field<Image>(console, "_tngToggleBackground2") }.Where(i => i != null).Select(i => i.transform).ToList();
                var skip = new HashSet<Transform>();
                foreach (Transform child in parent) if (tngBacks.Any(b => b == child || b.IsChildOf(child))) skip.Add(child);
                var core = new List<Transform> { aegis.transform, enterprise.transform, tng.transform };
                var shipParts = Members(parent, core, skip, out var ship);
                if (typeBox.parent != parent) { Plugin.Log.LogWarning($"Ship name box: GAME TYPE ({typeBox.name}) isn't in the ship toggles' panel; no box"); return; }
                skip.UnionWith(shipParts);
                var typeParts = Members(parent, new List<Transform> { typeBox }, skip, out var type);
                Plugin.Log.LogInfo($"Ship name box: SHIP {ship}: {Describe(shipParts)}");
                Plugin.Log.LogInfo($"Ship name box: GAME TYPE {type}: {Describe(typeParts)}");

                // A copy of the pieces in a holder of its own, at the panel's origin; then moved and shrunk.
                var holder = new GameObject("Ship Name Box", typeof(RectTransform));
                holder.layer = parent.gameObject.layer;
                holder.transform.SetParent(parent, false);
                holder.transform.SetSiblingIndex(aegis.transform.GetSiblingIndex());
                Transform copyOfTng = null, copyOfEnterprise = null, copyOfAegis = null;
                foreach (var part in shipParts)
                {
                    var copy = (GameObject)UnityEngine.Object.Instantiate(part.gameObject, holder.transform, false);
                    copy.name = part.name;
                    copy.transform.localPosition = part.localPosition; copy.transform.localRotation = part.localRotation; copy.transform.localScale = part.localScale;
                    if (part == tng.transform) copyOfTng = copy.transform;
                    if (part == enterprise.transform) copyOfEnterprise = copy.transform;
                    if (part == aegis.transform) copyOfAegis = copy.transform;
                }
                // The third row: another plain toggle where the Enterprise-D's black and orange one was.
                var plain = (GameObject)UnityEngine.Object.Instantiate(copyOfEnterprise.gameObject, holder.transform, false);
                plain.name = "Ship Name Toggle 3";
                plain.transform.localPosition = copyOfTng.localPosition; plain.transform.localScale = copyOfTng.localScale;
                UnityEngine.Object.DestroyImmediate(copyOfTng.gameObject);

                // A frame like GAME TYPE's around the rows (the SHIP box's own is part of the panel's backdrop).
                var rowsRect = new[] { copyOfAegis, copyOfEnterprise, plain.transform }.Select(Extent)
                    .Aggregate((a, b) => Rect.MinMaxRect(Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin), Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax)));
                var frameSource = typeBox.GetComponentsInChildren<Image>(true).Where(i => i.GetComponentInParent<Toggle>() == null)
                    .OrderByDescending(i => i.rectTransform.rect.width * i.rectTransform.rect.height).FirstOrDefault();
                if (frameSource != null)
                {
                    var frame = (GameObject)UnityEngine.Object.Instantiate(frameSource.gameObject, holder.transform, false);
                    frame.name = "Ship Name Frame";
                    foreach (var c in frame.transform.Cast<Transform>().ToList()) UnityEngine.Object.DestroyImmediate(c.gameObject);
                    foreach (var extra in frame.GetComponents<Component>().Where(c => !(c is Transform) && !(c is Graphic) && !(c is CanvasRenderer)).ToList()) UnityEngine.Object.DestroyImmediate(extra);
                    var rt = (RectTransform)frame.transform;
                    rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.localRotation = Quaternion.identity; rt.localScale = Vector3.one;
                    const float pad = 4f;
                    rt.sizeDelta = new Vector2(rowsRect.width + 2f * pad, rowsRect.height + 2f * pad);
                    rt.localPosition = new Vector3(rowsRect.center.x, rowsRect.center.y, 0f);
                    frame.transform.SetAsFirstSibling();
                    Plugin.Log.LogInfo($"Ship name box: frame from {frameSource.name}");
                }
                var full = Extent(holder.transform);

                // Above GAME TYPE and whatever control sits on top of it (the crew password button).
                float baseY = type.yMax;
                bool found = false;
                foreach (var control in console.GetComponentsInChildren<Selectable>(true))
                {
                    var t = control.transform;
                    if (t.IsChildOf(holder.transform) || shipParts.Any(p => t.IsChildOf(p)) || typeParts.Any(p => t.IsChildOf(p))) continue;
                    var r = ExtentIn(t, parent);
                    if (r.width <= 0f || r.xMax < type.xMin || r.xMin > type.xMax || r.yMin < type.yMax - 2f || r.yMin > type.yMax + 40f) continue;
                    Plugin.Log.LogInfo($"Ship name box: above {t.name} {r}");
                    baseY = Mathf.Max(baseY, r.yMax);
                    found = true;
                }

                // Bottom just above GAME TYPE (with its heading), left edges lined up, as large as fits up to the
                // panel's top row of decorations.
                holder.transform.localScale = Vector3.one * Scale;
                if (!found) baseY += 20f; // the crew password button's height, wherever it is
                Vector2 at = new Vector2(type.xMin - full.xMin * Scale, baseY + 3f - full.yMin * Scale);
                holder.transform.localPosition = new Vector3(at.x, at.y, 0f);

                var box = new Box { Console = console, Root = holder, Name = copyOfAegis.GetComponent<Toggle>(), Prefix = copyOfEnterprise.GetComponent<Toggle>(), Number = plain.GetComponent<Toggle>() }; // the name on top
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
                // The heading: the copied texts outside the toggles.
                foreach (var label in holder.GetComponentsInChildren<TMP_Text>(true))
                    if (label.GetComponentInParent<Toggle>() == null) label.SetLocalizedText(Translation.T("SHIP NAME"));
                boxes.Add(box);
                Refresh(box);

                // Decorations under the new box (the console's circuit drawing there) go.
                var mine = Extent(holder.transform);
                foreach (Transform sibling in parent)
                {
                    if (sibling == holder.transform || shipParts.Contains(sibling) || typeParts.Contains(sibling) || sibling.GetComponentsInChildren<Selectable>(true).Length > 0) continue;
                    var r = Extent(sibling);
                    float overlap = Mathf.Max(0f, Mathf.Min(r.xMax, mine.xMax) - Mathf.Max(r.xMin, mine.xMin)) * Mathf.Max(0f, Mathf.Min(r.yMax, mine.yMax) - Mathf.Max(r.yMin, mine.yMin));
                    if (r.width > 0f && overlap >= 0.4f * r.width * r.height && r.width < mine.width * 1.6f && r.height < mine.height * 1.6f)
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
