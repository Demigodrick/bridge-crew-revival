using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace StbcRevival
{
    /// <summary>
    /// The briefing console's SHIP box with more ships than its three rows: a row for each ship class of ours whose model
    /// is on this PC (NOVA, SOVEREIGN, ENTERPRISE (KELVIN), DEFIANT) under the Next Generation's, and buttons on its left to scroll the list. The box
    /// still shows three rows; the game's own toggles (Aegis, original Enterprise, Next Generation) move up and out of
    /// sight as the list scrolls, ours come in below. Each is a copy of the Aegis's row (always choosable, never locked).
    /// A class of ours flies on the Aegis's ship (and its own copy of the Aegis's bridge): choosing one turns that toggle on and puts the class's name on the room (CustomShip), so the bridge, consoles and
    /// crew are that ship's and the hull the class's. It shows as a ship of its own: that row looks unchosen. Choosing
    /// any of the game's ships (a press on the one underneath too) takes ours off the room again. Ours can be chosen by
    /// whoever chooses the ship (the captain), not for the campaign.
    /// </summary>
    internal static class ShipChoice
    {
        private sealed class Box
        {
            public ConsoleBriefingRoom Console;
            public Toggle Aegis, Tos, Tng;
            public readonly List<Toggle> Ours = new List<Toggle>();          // one row per class of ours
            public readonly List<CustomShip> OursShips = new List<CustomShip>();
            public List<Transform> TngExtras = new List<Transform>(); // the TNG row's orange backgrounds, moved with it
            public Vector3[] Home;            // each row's place at the top of the list (row 0..n-1)
            public Vector3[] TngExtraHome;
            public Vector3 Step;              // from one row to the next
            public Toggle Up, Down;           // the scroll buttons on the box's left edge
            public int Offset = -1, LastChosen = -1;
            public readonly Dictionary<Toggle, KeyValuePair<TMP_Text[], Color[]>> Labels = new Dictionary<Toggle, KeyValuePair<TMP_Text[], Color[]>>();
            public bool Applying;
        }

        private const int Visible = 3;
        private static readonly List<Box> boxes = new List<Box>();

        internal static void Patch(Harmony harmony)
        {
            var start = AccessTools.Method(typeof(ConsoleBriefingRoom), "Start");
            if (start == null) { Plugin.Log.LogWarning("Ship choice: the briefing console wasn't found"); return; }
            harmony.Patch(start, postfix: new HarmonyMethod(typeof(ShipChoice), nameof(Start_Postfix)) { priority = Priority.Low });
            // A press on a row that's already on (the one the Nova flies on) changes nothing the game reports: the
            // toggle is set off and its group puts it straight back on. That "off" is the press, unless the game is
            // setting its toggles itself (SyncBridgeToggles, every frame, from the ship in use).
            var sync = AccessTools.Method(typeof(ConsoleBriefingRoom), "KHFHCAELMKD");
            var setOn = AccessTools.PropertySetter(typeof(Toggle), "isOn");
            if (sync != null && setOn != null)
            {
                harmony.Patch(sync, prefix: new HarmonyMethod(typeof(ShipChoice), nameof(Sync_Prefix)), postfix: new HarmonyMethod(typeof(ShipChoice), nameof(Sync_Postfix)));
                harmony.Patch(setOn, prefix: new HarmonyMethod(typeof(ShipChoice), nameof(SetOn_Prefix)));
            }
            else Plugin.Log.LogWarning("Ship choice: the toggles' sync wasn't found; pressing the ship under one of ours won't take it off");
            // The console makes the chosen ship's row unpressable, every frame, through this helper (a chosen row needs
            // no press). With NOVA chosen, the ship it borrows is chosen underneath, so that row went grey (the
            // disabled tint) and ignored presses. It's given the value going in (as RevivalBriefing does: setting it
            // afterwards restarts the fade each frame): pressable for whoever chooses, like any row not chosen.
            var setInteractable = AccessTools.Method(AccessTools.TypeByName("KHDFGIINMKJ"), "SetInteractable", new[] { typeof(Selectable), typeof(bool) });
            if (setInteractable != null)
                harmony.Patch(setInteractable, prefix: new HarmonyMethod(typeof(ShipChoice), nameof(Interactable_Prefix)) { priority = Priority.Low });
            else Plugin.Log.LogWarning("Ship choice: the console's SetInteractable wasn't found; the ship under ours shows greyed");
        }

        private static T Field<T>(ConsoleBriefingRoom console, string name) where T : class =>
            AccessTools.Field(typeof(ConsoleBriefingRoom), name)?.GetValue(console) as T;

        private static void Start_Postfix(ConsoleBriefingRoom __instance)
        {
            try
            {
                var classes = CustomShip.All.Where(c => c.Available).ToList();
                if (classes.Count == 0) return; // nothing to add
                var console = __instance;
                var aegis = Field<Toggle>(console, "_aegisToggle");
                var tos = Field<Toggle>(console, "_enterpriseToggle");
                var tng = Field<Toggle>(console, "_tngToggle");
                if (aegis == null || tos == null || tng == null) return;

                var box = new Box { Console = console, Aegis = aegis, Tos = tos, Tng = tng };
                box.Step = tng.transform.localPosition - tos.transform.localPosition;

                // Ours: copies of the Aegis's row, the one ship every crew can choose (a copy of a row that's locked,
                // such as the original Enterprise or the Next Generation without the DLC, carries its lock and its
                // look), under the Next Generation's.
                GameObject copy = null;
                foreach (var ship in classes)
                {
                    copy = (GameObject)UnityEngine.Object.Instantiate(aegis.gameObject, aegis.transform.parent, false);
                    copy.name = ship.Name + " Toggle";
                    var row = copy.GetComponent<Toggle>();
                    row.group = null;
                    row.onValueChanged = new Toggle.ToggleEvent(); // not the Aegis's own (persistent) handler
                    row.isOn = false;
                    foreach (var label in copy.GetComponentsInChildren<TMP_Text>(true)) label.SetLocalizedText(ship.Label);
                    var chosen = ship;
                    row.onValueChanged.AddListener(on => Chose(box, chosen, on));
                    box.Ours.Add(row);
                    box.OursShips.Add(ship);
                }

                foreach (var image in new[] { Field<Image>(console, "_tngToggleBackground1"), Field<Image>(console, "_tngToggleBackground2") })
                    if (image != null && !image.transform.IsChildOf(tng.transform)) box.TngExtras.Add(image.transform);
                box.TngExtraHome = box.TngExtras.Select(t => t.localPosition).ToArray();

                var rows = Rows(box);
                box.Home = new Vector3[rows.Length];
                box.Home[0] = aegis.transform.localPosition;
                for (int i = 1; i < rows.Length; i++) box.Home[i] = box.Home[0] + box.Step * i;

                // Choosing one of the game's ships takes the Nova off the room. The game sets these toggles itself too,
                // every frame and on all four consoles (SyncBridgeToggles, from the ship in use), so a change of toggle
                // alone isn't a choice: the Nova flies on the Aegis or the Next Generation, whichever is in use, so only
                // the original Enterprise coming on (which it can't fly on) takes it off; a press on any ship's row
                // does too (below).
                tos.onValueChanged.AddListener(on => { if (on && !box.Applying && !JustPressed && CustomShip.FlownNow != null) CustomShip.Choose(null); });

                // A press on a ship's row while the Nova is chosen: that ship instead (a press on the one the Nova flies
                // on changes no toggle, so it's caught here).
                foreach (var row in new[] { aegis, tos, tng })
                {
                    var u = row;
                    var trigger = u.gameObject.GetComponent<EventTrigger>() ?? u.gameObject.AddComponent<EventTrigger>();
                    var click = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
                    click.callback.AddListener(_ => { if (!JustPressed && CustomShip.FlownNow != null && u.interactable) CustomShip.Choose(null); });
                    if (trigger.triggers == null) trigger.triggers = new List<EventTrigger.Entry>();
                    trigger.triggers.Add(click);
                }

                MakeArrows(box, rows.Length - Visible);
                boxes.Add(box);
                Apply(box, 0);
                Plugin.Log.LogInfo($"Ship choice: {string.Join(", ", classes.Select(c => c.Label).ToArray())} added to the ship box ({rows.Length} ships, scroll buttons {(box.Up != null ? "on" : "off")})");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Ship choice: " + e); }
        }

        /// <summary>A briefing console's ship box is about (the briefing room is open).</summary>
        internal static bool InBriefing => boxes.Count > 0 && boxes.Exists(b => b.Console != null);

        /// <summary>The row of the ship one of ours borrows: pressable (and so not greyed) for whoever chooses.</summary>
        private static void Interactable_Prefix(Selectable __0, ref bool __1)
        {
            if (__1 || boxes.Count == 0 || !(__0 is Toggle t) || !t.isOn || CustomShip.FlownNow == null) return;
            foreach (var box in boxes)
                if (t == box.Aegis)
                {
                    if (box.Console != null && CanChoose(box)) __1 = true;
                    return;
                }
        }

        /// <summary>Ours can be chosen on this console: by whoever chooses the ship (the captain), not for the campaign.</summary>
        private static bool CanChoose(Box box) => !CustomShip.Campaign && Missions.RevivalBriefing.IsChooser(box.Console);

        private static bool syncing;
        private static void Sync_Prefix() => syncing = true;
        private static void Sync_Postfix() => syncing = false;

        private static void SetOn_Prefix(Toggle __instance, bool value)
        {
            if (value || syncing || boxes.Count == 0 || !__instance.isOn || CustomShip.FlownNow == null || JustPressed || !__instance.interactable) return;
            foreach (var box in boxes)
                if (!box.Applying && (__instance == box.Aegis || __instance == box.Tos || __instance == box.Tng))
                {
                    Plugin.Log.LogInfo($"Ship choice: {__instance.name} pressed while the {CustomShip.FlownNow.Name} is chosen: back to that ship");
                    CustomShip.Choose(null);
                    return;
                }
        }

        private static Toggle[] Rows(Box box) => new[] { box.Aegis, box.Tos, box.Tng }.Concat(box.Ours).ToArray();

        // When one of ours was last pressed (presses and toggles changing just after it are its own doing).
        private static float pressedAt = -10f;
        private static bool JustPressed => Time.unscaledTime - pressedAt < 1.5f;

        private static void Chose(Box box, CustomShip ship, bool on)
        {
            if (box.Applying || !on) return;
            Plugin.Log.LogInfo($"Ship choice: {ship.Label} pressed");
            pressedAt = Time.unscaledTime;
            box.Applying = true;
            try
            {
                CustomShip.Choose(ship);
                // The game's own choice under it: the Aegis's.
                var under = box.Aegis;
                if (!under.isOn) under.isOn = true;
            }
            finally { box.Applying = false; }
        }

        /// <summary>
        /// Two round buttons right of the box's heading, up and down: copies of the Aegis's toggle (so the console's hands
        /// and mouse press them as any of its buttons), the check mark hidden, an arrow in the circle.
        /// </summary>
        private static void MakeArrows(Box box, int maxOffset)
        {
            if (maxOffset <= 0) return;
            box.Up = Arrow(box, true);
            box.Down = Arrow(box, false);
        }

        private static Sprite pill, triangle;

        /// <summary>A white shape on a clear square, smoothed at its edge: a rounded bar or a triangle pointing up.</summary>
        private static Sprite Shape(bool bar)
        {
            const int w = 32, h = 64;
            int width = bar ? w : 48, height = bar ? h : 48;
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = bar ? "Ship list bar" : "Ship list arrow" };
            var px = new Color32[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f, inside;
                    if (bar)
                    {
                        // A capsule: half a width round at both ends.
                        float r = width / 2f, cy = Mathf.Clamp(fy, r, height - r);
                        inside = r - Mathf.Sqrt((fx - r) * (fx - r) + (fy - cy) * (fy - cy));
                    }
                    else
                    {
                        // A triangle, point up: inside all three edges.
                        Vector2 p0 = new Vector2(width * 0.5f, height * 0.85f), p1 = new Vector2(width * 0.1f, height * 0.2f), p2 = new Vector2(width * 0.9f, height * 0.2f);
                        float Edge(Vector2 a2, Vector2 b2) { var d = (b2 - a2).normalized; return (fx - a2.x) * -d.y + (fy - a2.y) * d.x; }
                        inside = Mathf.Min(Edge(p0, p1), Mathf.Min(Edge(p1, p2), Edge(p2, p0)));
                    }
                    byte alpha = (byte)Mathf.RoundToInt(Mathf.Clamp01(inside + 0.5f) * 255f);
                    px[y * width + x] = new Color32(255, 255, 255, alpha);
                }
            tex.SetPixels32(px); tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f);
        }

        /// <summary>
        /// A scroll button on the box's left edge: a copy of the Aegis's toggle (so the console's hands and mouse press it
        /// as any of its buttons), drawn as a rounded bar in the frame's colour with a dark triangle, up or down.
        /// </summary>
        private static Toggle Arrow(Box box, bool up)
        {
            if (pill == null) { pill = Shape(true); triangle = Shape(false); }
            var source = box.Aegis;
            var go = (GameObject)UnityEngine.Object.Instantiate(source.gameObject, source.transform.parent, false);
            go.name = up ? "Ship List Up" : "Ship List Down";
            var toggle = go.GetComponent<Toggle>();
            toggle.group = null;
            toggle.onValueChanged = new Toggle.ToggleEvent(); // not the Aegis's own (persistent) handler
            toggle.isOn = false;
            if (toggle.graphic != null) toggle.graphic.gameObject.SetActive(false);
            toggle.graphic = null;
            foreach (var label in go.GetComponentsInChildren<TMP_Text>(true)) label.gameObject.SetActive(false);

            // The bar: the toggle's own image, a capsule a row and a half tall; the frame's light blue.
            var frame = new Color(0.45f, 0.82f, 1f, 1f);
            var bar = toggle.targetGraphic as Image ?? go.GetComponent<Image>();
            if (bar != null)
            {
                bar.sprite = pill; bar.type = Image.Type.Simple; bar.preserveAspect = false; bar.color = frame;
                var rt = bar.rectTransform;
                rt.sizeDelta = new Vector2(9f, Mathf.Abs(box.Step.y) * 1.25f);
            }
            var arrow = new GameObject("Arrow", typeof(RectTransform));
            arrow.layer = go.layer;
            arrow.transform.SetParent(go.transform, false);
            var art = (RectTransform)arrow.transform;
            art.anchorMin = art.anchorMax = art.pivot = new Vector2(0.5f, 0.5f);
            art.sizeDelta = new Vector2(7f, 7f);
            art.localPosition = Vector3.zero;
            art.localRotation = Quaternion.Euler(0f, 0f, up ? 0f : 180f);
            var arrowImage = arrow.AddComponent<Image>();
            arrowImage.sprite = triangle; arrowImage.color = new Color(0.03f, 0.08f, 0.13f, 1f); arrowImage.raycastTarget = false;

            // On the box's left edge, level with its top row (up) and its bottom row (down).
            var at = up ? box.Home[0] : box.Home[Visible - 1];
            var sourceRect = (RectTransform)source.transform;
            go.transform.localPosition = new Vector3(at.x + sourceRect.rect.xMin * sourceRect.localScale.x - 10f, at.y, at.z);
            go.transform.localScale = source.transform.localScale;

            FitHitAreas(go, bar != null ? bar.rectTransform : (RectTransform)go.transform, up ? "up" : "down");

            toggle.onValueChanged.AddListener(on =>
            {
                if (!on || box.Applying) return;
                box.Applying = true;
                try { toggle.isOn = false; } finally { box.Applying = false; }
                Apply(box, box.Offset + (up ? -1 : 1));
            });
            return toggle;
        }

        /// <summary>
        /// A scroll button is a copy of a whole row, so its hit areas (the console's hand and mouse triggers, box
        /// colliders under it) are as wide as a row and lay over the rows beside it, taking their presses. They're cut
        /// to the button's own size here.
        /// </summary>
        private static void FitHitAreas(GameObject go, RectTransform visual, string what)
        {
            var corners = new Vector3[4];
            visual.GetWorldCorners(corners); // bottom left, top left, top right, bottom right
            var fitted = new List<string>();
            foreach (var c in go.GetComponentsInChildren<BoxCollider>(true))
            {
                var t = c.transform;
                Vector3 a = t.InverseTransformPoint(corners[0]), b = t.InverseTransformPoint(corners[2]);
                // The button's rectangle in the collider's own axes: two of them span it, the third (where it has no
                // extent) is the trigger's depth, kept as it is.
                var ext = new Vector3(Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y), Mathf.Abs(b.z - a.z));
                int depth = ext.x <= ext.y && ext.x <= ext.z ? 0 : ext.y <= ext.z ? 1 : 2;
                string was = $"{c.size.x:0.##}x{c.size.y:0.##}x{c.size.z:0.##}";
                Vector3 centre = c.center, size = c.size;
                for (int i = 0; i < 3; i++)
                    if (i != depth) { centre[i] = (a[i] + b[i]) / 2f; size[i] = Mathf.Max(1e-3f, ext[i]); }
                c.center = centre; c.size = size;
                fitted.Add($"{t.name} {was} -> {c.size.x:0.##}x{c.size.y:0.##}x{c.size.z:0.##} (depth {"xyz"[depth]})");
            }
            foreach (var g in go.GetComponentsInChildren<Graphic>(true))
                if (g.rectTransform != visual && !g.transform.IsChildOf(visual)) g.raycastTarget = false; // only the button itself takes the mouse
            Plugin.Log.LogInfo($"Ship choice: scroll button {what}: hit areas {(fitted.Count == 0 ? "none found" : string.Join("; ", fitted.ToArray()))}");
        }

        // A scroll button at the end of the list: faint and not pressable.
        private static void Enable(Toggle button, bool on)
        {
            if (button == null) return;
            button.interactable = on;
            foreach (var image in button.GetComponentsInChildren<Image>(true)) image.canvasRenderer.SetAlpha(on ? 1f : 0.3f);
        }

        /// <summary>Shows rows offset..offset+2 in the box's three places; the others are out of sight.</summary>
        private static void Apply(Box box, int offset)
        {
            var rows = Rows(box);
            offset = Mathf.Clamp(offset, 0, Mathf.Max(0, rows.Length - Visible));
            if (offset == box.Offset) return;
            box.Offset = offset;
            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i] == null) continue;
                int place = i - offset;
                bool shown = place >= 0 && place < Visible;
                rows[i].gameObject.SetActive(shown);
                if (shown) rows[i].transform.localPosition = box.Home[0] + box.Step * place;
                if (rows[i] == box.Tng)
                    for (int k = 0; k < box.TngExtras.Count; k++)
                    {
                        box.TngExtras[k].gameObject.SetActive(shown);
                        box.TngExtras[k].localPosition = box.TngExtraHome[k] + box.Step * (place - 2);
                    }
            }
            Enable(box.Up, offset > 0);
            Enable(box.Down, offset < rows.Length - Visible);
        }

        /// <summary>Five times a second: our rows' marks and whether they can be pressed follow the room; the row underneath hides its mark.</summary>
        private static float nextUpdate;

        internal static void Update()
        {
            if (boxes.Count == 0 || Time.unscaledTime < nextUpdate) return;
            nextUpdate = Time.unscaledTime + 0.2f; // five times a second is plenty for a mark and a hologram
            boxes.RemoveAll(b => b.Console == null || b.Ours.Count == 0 || b.Ours[0] == null);
            var flown = CustomShip.FlownNow;
            foreach (var box in boxes)
            {
                try
                {
                    box.Applying = true;
                    bool under = box.Aegis.isOn; // ours fly on the Aegis's ship
                    bool on = flown != null && under;
                    int chosen = box.Tng.isOn ? 2 : box.Tos.isOn ? 1 : 0;
                    // Choosable whenever the game's own choice is (the host's or captain's), greyed like the others when not,
                    // and not for the campaign (its missions are the Aegis's own).
                    bool campaign = CustomShip.Campaign;
                    bool canChoose = !campaign && CanChoose(box);
                    var greyed = box.Tos.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault();
                    var grey = greyed != null && !box.Tos.interactable ? greyed.color : new Color(0.5f, 0.5f, 0.5f, 0.6f);
                    for (int r = 0; r < box.Ours.Count; r++)
                    {
                        var row = box.Ours[r];
                        bool rowOn = on && box.OursShips[r] == flown;
                        if (rowOn) chosen = 3 + r;
                        if (row.isOn != rowOn) row.isOn = rowOn;
                        // The game colours its rows' labels itself (a locked row's goes grey): ours follow the same.
                        if (!box.Labels.TryGetValue(row, out var labels))
                        {
                            var texts = row.GetComponentsInChildren<TMP_Text>(true);
                            box.Labels[row] = labels = new KeyValuePair<TMP_Text[], Color[]>(texts, texts.Select(l => l.color).ToArray());
                        }
                        for (int k = 0; k < labels.Key.Length; k++)
                            if (labels.Key[k] != null) labels.Key[k].color = canChoose ? labels.Value[k] : grey;
                        if (row.interactable != canChoose) row.interactable = canChoose;
                    }
                    if (campaign && flown != null && Missions.RevivalBriefing.IsChooser(box.Console) && !JustPressed) { CustomShip.Choose(null); flown = null; on = false; }
                    // Ours are ships of their own as far as the box shows (the Aegis they borrow for now is to be
                    // replaced): that row looks unchosen while ours is, as when another ship is chosen.
                    foreach (var row in new[] { box.Tng, box.Aegis })
                        if (row.graphic != null) row.graphic.canvasRenderer.SetAlpha(on && row == box.Aegis && row.isOn ? 0f : (row.isOn ? 1f : 0f));
                    box.Applying = false;
                    // Scrolled so the ship chosen is in sight, when the choice changes (scrolling away is free after that).
                    if (chosen != box.LastChosen)
                    {
                        box.LastChosen = chosen;
                        if (chosen < box.Offset || chosen >= box.Offset + Visible) Apply(box, chosen - Visible + 1);
                    }
                }
                catch (Exception e) { box.Applying = false; Plugin.Log.LogDebug("Ship choice: " + e.Message); }
            }
        }
    }
}
