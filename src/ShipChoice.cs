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
    /// The briefing console's SHIP box with more ships than its three rows: our classes (NOVA, SOVEREIGN) under the
    /// Next Generation, and scroll buttons on its left. The box still shows three rows; the game's own toggles (Aegis,
    /// original Enterprise, Next Generation) move up and out of sight as the list scrolls, the added ones come in below.
    /// Our classes fly on the Next Generation's ship: choosing one turns the game's TNG toggle on and puts its name on
    /// the room (CustomShip), so the bridge, consoles and crew are the Enterprise-D's and the hull the class's. The TNG
    /// row then shows no mark; the class's row does. Choosing any of the game's ships takes it off the room again. A
    /// class is there only with its model on this PC, and can be chosen whenever the TNG toggle can.
    /// </summary>
    internal static class ShipChoice
    {
        private sealed class Box
        {
            public ConsoleBriefingRoom Console;
            public Toggle Aegis, Tos, Tng;
            public readonly List<CustomShip> Ships = new List<CustomShip>();  // our classes in the box, one row each
            public readonly List<Toggle> Custom = new List<Toggle>();
            public List<Transform> TngExtras = new List<Transform>(); // the TNG row's orange backgrounds, moved with it
            public Vector3[] Home;            // each row's place at the top of the list (row 0..n-1)
            public Vector3[] TngExtraHome;
            public Vector3 Step;              // from one row to the next
            public Toggle Up, Down;           // the scroll buttons on the box's left edge
            public readonly Dictionary<CustomShip, GameObject> Holograms = new Dictionary<CustomShip, GameObject>(); // on the holotable, under the Next Generation's hologram
            public readonly Dictionary<CustomShip, int> HologramParts = new Dictionary<CustomShip, int>(); // how many of the class's pieces it was made of (more later: made again)
            public Renderer[] TngHologram;    // that hologram's own parts (hidden while one of ours shows)
            public int Offset = -1, LastChosen = -1;
            public bool Applying;
        }

        private const int Visible = 3;
        private static readonly List<Box> boxes = new List<Box>();

        internal static void Patch(Harmony harmony)
        {
            var start = AccessTools.Method(typeof(ConsoleBriefingRoom), "Start");
            if (start == null) { Plugin.Log.LogWarning("Ship choice: the briefing console wasn't found"); return; }
            harmony.Patch(start, postfix: new HarmonyMethod(typeof(ShipChoice), nameof(Start_Postfix)) { priority = Priority.Low });
        }

        private static T Field<T>(ConsoleBriefingRoom console, string name) where T : class =>
            AccessTools.Field(typeof(ConsoleBriefingRoom), name)?.GetValue(console) as T;

        private static void Start_Postfix(ConsoleBriefingRoom __instance)
        {
            try
            {
                var ours = CustomShip.All.Where(s => s.Available).ToList();
                if (ours.Count == 0) return; // nothing to add
                var console = __instance;
                var aegis = Field<Toggle>(console, "_aegisToggle");
                var tos = Field<Toggle>(console, "_enterpriseToggle");
                var tng = Field<Toggle>(console, "_tngToggle");
                if (aegis == null || tos == null || tng == null) return;

                var box = new Box { Console = console, Aegis = aegis, Tos = tos, Tng = tng };
                box.Step = tng.transform.localPosition - tos.transform.localPosition;

                // Each class: a plain row (the original Enterprise's look) under the Next Generation's.
                foreach (var ship in ours)
                {
                    var copy = (GameObject)UnityEngine.Object.Instantiate(tos.gameObject, tos.transform.parent, false);
                    copy.name = ship.Name + " Toggle";
                    var row = copy.GetComponent<Toggle>();
                    row.group = null;
                    row.onValueChanged = new Toggle.ToggleEvent(); // not the original Enterprise's own (persistent) handler
                    row.isOn = false;
                    foreach (var label in copy.GetComponentsInChildren<TMP_Text>(true)) label.SetLocalizedText(ship.Label);
                    var chosen = ship;
                    row.onValueChanged.AddListener(on => Chose(box, chosen, on));
                    box.Ships.Add(ship);
                    box.Custom.Add(row);
                }

                foreach (var image in new[] { Field<Image>(console, "_tngToggleBackground1"), Field<Image>(console, "_tngToggleBackground2") })
                    if (image != null && !image.transform.IsChildOf(tng.transform)) box.TngExtras.Add(image.transform);
                box.TngExtraHome = box.TngExtras.Select(t => t.localPosition).ToArray();

                var rows = Rows(box);
                box.Home = new Vector3[rows.Length];
                box.Home[0] = aegis.transform.localPosition;
                for (int i = 1; i < rows.Length; i++) box.Home[i] = box.Home[0] + box.Step * i;

                // Choosing one of the game's ships takes ours off the room.
                foreach (var own in new[] { aegis, tos, tng })
                {
                    var t = own;
                    t.onValueChanged.AddListener(on => { if (on && !box.Applying && CustomShip.Flown != null && (t != tng || !box.Custom.Any(c => c.isOn))) CustomShip.Choose(null); });
                }

                // A click on the Next Generation's row while one of ours is chosen: back to the Next Generation (the toggle is
                // on already, so it says nothing itself).
                var trigger = tng.gameObject.GetComponent<EventTrigger>() ?? tng.gameObject.AddComponent<EventTrigger>();
                var click = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
                click.callback.AddListener(_ => { if (CustomShip.Flown != null && (tng.interactable || tng.isOn)) CustomShip.Choose(null); });
                if (trigger.triggers == null) trigger.triggers = new List<EventTrigger.Entry>();
                trigger.triggers.Add(click);

                MakeArrows(box, rows.Length - Visible);
                boxes.Add(box);
                Apply(box, 0);
                Plugin.Log.LogInfo($"Ship choice: {string.Join(", ", ours.Select(o => o.Label).ToArray())} added to the ship box ({rows.Length} ships, scroll buttons {(box.Up != null ? "on" : "off")})");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Ship choice: " + e); }
        }

        private static Toggle[] Rows(Box box) => new[] { box.Aegis, box.Tos, box.Tng }.Concat(box.Custom).ToArray();

        private static void Chose(Box box, CustomShip ship, bool on)
        {
            if (box.Applying || !on) return;
            box.Applying = true;
            try
            {
                CustomShip.Choose(ship);
                for (int i = 0; i < box.Custom.Count; i++) if (box.Ships[i] != ship && box.Custom[i].isOn) box.Custom[i].isOn = false;
                if (!box.Tng.isOn) box.Tng.isOn = true; // the game's own choice: the Next Generation's ship
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

            toggle.onValueChanged.AddListener(on =>
            {
                if (!on || box.Applying) return;
                box.Applying = true;
                try { toggle.isOn = false; } finally { box.Applying = false; }
                Apply(box, box.Offset + (up ? -1 : 1));
            });
            return toggle;
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

        private static readonly System.Reflection.FieldInfo TngHologramField = AccessTools.Field(typeof(ConsoleBriefingRoom), "IMHCKEMPKJA");

        /// <summary>
        /// The holotable: the console shows one ship hologram at a time (SetHologramModels gives it the Aegis's, the
        /// original Enterprise's and the Enterprise-D's). Our classes' go under the Enterprise-D's, which is the one shown
        /// with one of them chosen: its meshes and materials (the class body's own, lit like the other ships' models), as
        /// long; the Enterprise-D's parts are hidden while one of ours shows.
        /// </summary>
        private static void Hologram(Box box, CustomShip flown)
        {
            var tng = TngHologramField?.GetValue(box.Console) as GameObject;
            if (tng == null) return;
            if (box.TngHologram == null) box.TngHologram = tng.GetComponentsInChildren<Renderer>(true).Where(r => r is MeshRenderer).ToArray();
            if (box.TngHologram.Length == 0) return;
            if (flown != null && box.Ships.Contains(flown))
            {
                // A piece put on the class since (its name decals): the hologram is made again with it.
                if (box.Holograms.TryGetValue(flown, out var old) && old != null && flown.ModelParts.Length != box.HologramParts[flown])
                {
                    UnityEngine.Object.Destroy(old);
                    box.Holograms.Remove(flown);
                }
                if (!box.Holograms.TryGetValue(flown, out var made) || made == null)
                {
                    made = MakeHologram(box, tng, flown);
                    if (made != null)
                    {
                        box.Holograms[flown] = made;
                        box.HologramParts[flown] = flown.ModelParts.Length;
                    }
                }
            }
            bool ours = false;
            foreach (var kv in box.Holograms)
            {
                if (kv.Value == null) continue;
                bool show = kv.Key == flown;
                ours |= show;
                if (kv.Value.activeSelf != show) kv.Value.SetActive(show);
            }
            foreach (var r in box.TngHologram) if (r != null && r.enabled == ours) r.enabled = !ours;
        }

        private static GameObject MakeHologram(Box box, GameObject tng, CustomShip ship)
        {
            var parts = ship.ModelParts;
            if (parts.Length == 0) return null;
            // The ship itself among the Enterprise-D hologram's parts (there are window layers and effects too): the hologram
            // shader's (it has a noise texture), the most vertices.
            var first = box.TngHologram.Where(r => r != null && r.GetComponent<MeshFilter>()?.sharedMesh != null)
                .OrderByDescending(r => r.sharedMaterial != null && r.sharedMaterial.HasProperty("_Noise") ? 1 : 0)
                .ThenByDescending(r => r.GetComponent<MeshFilter>().sharedMesh.vertexCount).FirstOrDefault();
            if (first == null) return null;
            var space = first.transform.parent ?? tng.transform;
            // That ship's size, in the space its mesh hangs in (forward z, up y, as the ships' bodies).
            Vector3 lo = Vector3.one * float.MaxValue, hi = Vector3.one * float.MinValue;
            var bounds = first.GetComponent<MeshFilter>().sharedMesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                var c = new Vector3((i & 1) == 0 ? bounds.min.x : bounds.max.x, (i & 2) == 0 ? bounds.min.y : bounds.max.y, (i & 4) == 0 ? bounds.min.z : bounds.max.z);
                var p = space.InverseTransformPoint(first.transform.TransformPoint(c));
                lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p);
            }
            Vector3 shipLo = Vector3.one * float.MaxValue, shipHi = Vector3.one * float.MinValue;
            foreach (var f in parts) { var b = f.sharedMesh.bounds; shipLo = Vector3.Min(shipLo, b.min); shipHi = Vector3.Max(shipHi, b.max); }
            float length = Mathf.Max(hi.z - lo.z, hi.x - lo.x);
            float scale = length / Mathf.Max(1e-3f, shipHi.z - shipLo.z);

            var holo = new GameObject(ship.Name + " Hologram");
            holo.layer = first.gameObject.layer;
            holo.transform.SetParent(space, false);
            holo.transform.localPosition = (lo + hi) / 2f - (shipLo + shipHi) / 2f * scale;
            holo.transform.localScale = Vector3.one * scale;
            var material = new UnityEngine.Material(first.sharedMaterial) { name = ship.Name + " Hologram" };
            foreach (var f in parts)
            {
                var go = new GameObject(f.name);
                go.layer = holo.layer;
                go.transform.SetParent(holo.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = f.sharedMesh;
                // The ship's own materials: the other ships on the table are their lit models too, not a wireframe.
                var own = f.GetComponent<MeshRenderer>()?.sharedMaterial;
                go.AddComponent<MeshRenderer>().sharedMaterial = own ?? material;
            }
            Plugin.Log.LogInfo($"Ship choice: {ship.Name} hologram under {tng.name} ({parts.Length} parts, scale {scale:0.###}, {box.TngHologram.Length} Enterprise-D parts)");
            return holo;
        }

        /// <summary>Each frame: NOVA's mark and whether it can be clicked follow the room; the TNG row hides its mark under it.</summary>
        internal static void Update()
        {
            if (boxes.Count == 0) return;
            boxes.RemoveAll(b => b.Console == null || b.Custom.Count == 0 || b.Custom[0] == null);
            var flown = CustomShip.Flown;
            foreach (var box in boxes)
            {
                try
                {
                    Hologram(box, flown);
                    box.Applying = true;
                    bool canChoose = box.Tng.interactable || (box.Tng.isOn && (box.Aegis.interactable || box.Tos.interactable));
                    int ours = -1;
                    for (int i = 0; i < box.Custom.Count; i++)
                    {
                        bool on = flown == box.Ships[i] && box.Tng.isOn;
                        if (on) ours = i;
                        if (box.Custom[i].isOn != on) box.Custom[i].isOn = on;
                        if (box.Custom[i].interactable != canChoose) box.Custom[i].interactable = canChoose;
                    }
                    if (box.Tng.graphic != null) box.Tng.graphic.canvasRenderer.SetAlpha(ours >= 0 ? 0f : (box.Tng.isOn ? 1f : 0f));
                    box.Applying = false;
                    // Scrolled so the ship chosen is in sight, when the choice changes (scrolling away is free after that).
                    int chosen = ours >= 0 ? 3 + ours : box.Tng.isOn ? 2 : box.Tos.isOn ? 1 : 0;
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
