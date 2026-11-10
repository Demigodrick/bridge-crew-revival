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
    /// The briefing console's ship name button: a copy of its OPEN ROOM button (crew password), just above it,
    /// showing the crew's ship ("USS COSMO  NCC-91234"). A click opens one window for all of it: the registry's prefix
    /// (NCC or NX), its number and the ship's name (on SteamVR, its keyboard asks for the name, then the registry).
    /// The host or the captain changes it for the room; everyone sees it.
    /// </summary>
    internal static class ShipNameBox
    {
        private sealed class Box
        {
            public GameObject Root;
            public Button Button;
            public TMP_Text[] Labels;
        }

        private static readonly List<Box> boxes = new List<Box>();
        private static readonly Dictionary<TMP_Text, string> shown = new Dictionary<TMP_Text, string>();
        private static float nextRefresh;

        internal static void Patch(Harmony harmony)
        {
            var start = AccessTools.Method(typeof(ConsoleBriefingRoom), "Start");
            if (start == null) { Plugin.Log.LogWarning("Ship name box: the briefing console wasn't found"); return; }
            harmony.Patch(start, postfix: new HarmonyMethod(typeof(ShipNameBox), nameof(Start_Postfix)) { priority = Priority.Low });
        }

        /// <summary>The rectangle a transform's graphics cover, in another transform's space.</summary>
        private static Rect ExtentIn(Transform box, Transform space)
        {
            float xMin = float.MaxValue, yMin = float.MaxValue, xMax = float.MinValue, yMax = float.MinValue;
            var corners = new Vector3[4];
            foreach (var g in box.GetComponentsInChildren<Graphic>(true))
            {
                g.rectTransform.GetWorldCorners(corners);
                foreach (var c in corners)
                {
                    var p = space.InverseTransformPoint(c);
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
                var open = console.GetComponentsInChildren<Button>(true).FirstOrDefault(b => b.name == "OpenRoom");
                if (open == null) { Plugin.Log.LogWarning("Ship name box: the console's OpenRoom button wasn't found; no ship name button"); return; }
                var parent = open.transform.parent;
                var openRect = ExtentIn(open.transform, parent);

                var root = (GameObject)UnityEngine.Object.Instantiate(open.gameObject, parent, false);
                root.name = "Ship Name Button";
                root.SetActive(true);
                root.transform.localPosition = open.transform.localPosition + new Vector3(0f, openRect.height + 3f, 0f);
                root.transform.SetSiblingIndex(open.transform.GetSiblingIndex() + 1);
                var button = root.GetComponent<Button>();
                button.onClick = new Button.ButtonClickedEvent(); // not the room's own (persistent) handler
                button.onClick.AddListener(Clicked);
                var box = new Box { Root = root, Button = button, Labels = root.GetComponentsInChildren<TMP_Text>(true) };
                boxes.Add(box);
                Refresh(box);

                // Decorations under the new button (the console's circuit drawing there) go.
                var mine = ExtentIn(root.transform, parent);
                foreach (var image in console.GetComponentsInChildren<Image>(true))
                {
                    var t = image.transform;
                    if (t.IsChildOf(root.transform) || image.GetComponentInParent<Selectable>() != null || t.GetComponentsInChildren<Selectable>(true).Length > 0 || t.GetComponentsInChildren<TMP_Text>(true).Length > 0) continue;
                    var r = ExtentIn(t, parent);
                    float overlap = Mathf.Max(0f, Mathf.Min(r.xMax, mine.xMax) - Mathf.Max(r.xMin, mine.xMin)) * Mathf.Max(0f, Mathf.Min(r.yMax, mine.yMax) - Mathf.Max(r.yMin, mine.yMin));
                    if (r.width > 0f && overlap >= 0.5f * r.width * r.height && r.width < mine.width * 1.5f && r.height < mine.height * 1.5f)
                    {
                        Plugin.Log.LogInfo($"Ship name box: hiding the decoration {t.name} {r} under it");
                        t.gameObject.SetActive(false);
                    }
                }
                Plugin.Log.LogInfo($"Ship name box: button added at {mine}, above {open.name} {openRect}");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Ship name box: " + e); }
        }

        // ---- the ship name's parts ----

        private static readonly Regex RegistryParts = new Regex(@"^\s*(NCC|NX)?[\s\-]*(.*)$", RegexOptions.IgnoreCase);

        internal static void Parts(out string prefix, out string number, out string name)
        {
            name = ShipName.Name; string registry = ShipName.Registry;
            var m = RegistryParts.Match(registry);
            prefix = m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value.ToUpperInvariant() : "NCC";
            number = m.Groups[2].Value.Trim();
        }

        internal static void SetParts(string prefix, string number, string name)
        {
            name = (name ?? "").Trim();
            number = (number ?? "").Trim();
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

        private static void Clicked()
        {
            if (!CanEdit()) return;
            Parts(out var prefix, out var number, out var name);
            if (UnityEngine.VR.VRSettings.enabled && UnityEngine.VR.VRSettings.loadedDeviceName == "OpenVR")
            {
                // SteamVR's keyboard: the name, then the registry ("NX-74205"; without a prefix the current one).
                VrKeyboard.Open(Translation.T("Ship name (e.g. USS Cosmo)"), 30, newName =>
                    VrKeyboard.Open(Translation.T("Registry (e.g. NCC-91234)"), 16, registry =>
                    {
                        var m = RegistryParts.Match(registry ?? "");
                        string p = m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value.ToUpperInvariant() : prefix;
                        SetParts(p, m.Groups[2].Value, newName);
                    }));
                return;
            }
            ShipNamePrompt.Open(prefix, number, name);
        }

        // ---- keeping the button current ----

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
                string text = name.Length == 0 ? Translation.T("SHIP NAME") : (name + (number.Length > 0 ? $"  {prefix}-{number}" : "")).ToUpperInvariant();
                bool edit = CanEdit();
                if (b.Button.interactable != edit) b.Button.interactable = edit;
                foreach (var label in b.Labels)
                {
                    if (label == null) continue;
                    if (!shown.TryGetValue(label, out var was) || was != text) { label.SetLocalizedText(text); shown[label] = text; }
                }
            }
            catch (Exception e) { Plugin.Log.LogDebug("Ship name box: " + e.Message); }
        }
    }

    /// <summary>The desktop window for the ship's name: prefix (NCC / NX), number and name in one go.</summary>
    internal static class ShipNamePrompt
    {
        private static bool open;
        private static int prefix; // 0 NCC, 1 NX
        private static string number = "", name = "";
        private static Rect window = new Rect(0, 0, 380, 150);
        private static readonly string[] Prefixes = { "NCC", "NX" };

        internal static bool IsOpen => open;

        internal static void Open(string currentPrefix, string currentNumber, string currentName)
        {
            prefix = currentPrefix == "NX" ? 1 : 0;
            number = currentNumber ?? "";
            name = currentName ?? "";
            window.x = (Screen.width - window.width) / 2;
            window.y = (Screen.height - window.height) / 3;
            open = true;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            if (UnityEngine.VR.VRSettings.enabled)
                try { UI.PopupMgr.ShowPopup(Translation.T("Name the ship in the window on the desktop."), null, UI.PDHCCBFJODN.Notification, true); }
                catch { /* no popups here */ }
        }

        internal static void OnGUI()
        {
            if (!open) return;
            window = GUILayout.Window(0x5BC9, window, Draw, Translation.T("Ship name"));
        }

        private static void Draw(int id)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Translation.T("Name"), GUILayout.Width(80));
            GUI.SetNextControlName("shipname");
            name = GUILayout.TextField(name, 30);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(Translation.T("Registry"), GUILayout.Width(80));
            prefix = GUILayout.Toolbar(prefix, Prefixes, GUILayout.Width(100));
            number = GUILayout.TextField(number, 12);
            GUILayout.EndHorizontal();
            if (GUI.GetNameOfFocusedControl() == "") GUI.FocusControl("shipname");
            bool enter = Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
            GUILayout.BeginHorizontal();
            bool ok = GUILayout.Button(Translation.T("OK")) || enter;
            bool reset = GUILayout.Button(Translation.T("The game's"));
            bool cancel = GUILayout.Button(Translation.T("Cancel")) || (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape);
            GUILayout.EndHorizontal();
            if (ok) { open = false; ShipNameBox.SetParts(Prefixes[prefix], number, name); }
            else if (reset) { open = false; ShipNameBox.SetParts(Prefixes[prefix], "", ""); }
            else if (cancel) open = false;
            GUI.DragWindow();
        }
    }
}
