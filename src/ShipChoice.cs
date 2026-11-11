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
    /// The briefing console's SHIP box with more ships than its three rows: NOVA under the Next Generation (and room
    /// for more), and a slider on its left to scroll the list. The box still shows three rows; the game's own toggles
    /// (Aegis, original Enterprise, Next Generation) move up and out of sight as the list scrolls, the added ones come
    /// in below. NOVA flies on the Next Generation's ship when the crew can choose it (it's DLC), else on the Aegis's:
    /// choosing it turns that toggle on and puts "Nova" on the room (NovaShip), so the bridge, consoles and crew are
    /// that ship's and the hull the Nova's. That row then shows no mark; NOVA does. Choosing any of the game's ships
    /// (a click on the one already on too) takes "Nova" off the room again. NOVA is there only with the Nova's model
    /// on this PC, and can be chosen whenever the Aegis or the Next Generation can.
    /// </summary>
    internal static class ShipChoice
    {
        private sealed class Box
        {
            public ConsoleBriefingRoom Console;
            public Toggle Aegis, Tos, Tng, Nova;
            public List<Transform> TngExtras = new List<Transform>();  // the TNG row's orange backgrounds (not its children)
            public List<Graphic> NovaExtras = new List<Graphic>();     // NOVA's copies of them
            public Vector3 Step;              // from one row to the next, as the game lays out its three
            public GameObject NovaHologram;   // on the holotable, under the Next Generation's hologram
            public Renderer[] TngHologram;    // that hologram's own parts (hidden while the Nova shows)
            public int HologramParts;         // how many of the Nova's pieces it was made of (more later: made again)
            public int HologramVersion = -1;
            public bool Applying;
        }

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
                if (!NovaShip.Available) return; // nothing to add (more ships later: each with its own check)
                var console = __instance;
                var aegis = Field<Toggle>(console, "_aegisToggle");
                var tos = Field<Toggle>(console, "_enterpriseToggle");
                var tng = Field<Toggle>(console, "_tngToggle");
                if (aegis == null || tos == null || tng == null) return;

                var box = new Box { Console = console, Aegis = aegis, Tos = tos, Tng = tng };
                box.Step = tng.transform.localPosition - tos.transform.localPosition;

                // NOVA: a copy of the Next Generation's row, its orange look included (the extra ships' colour),
                // under it.
                var copy = (GameObject)UnityEngine.Object.Instantiate(tng.gameObject, tng.transform.parent, false);
                copy.name = "Nova Toggle";
                var nova = copy.GetComponent<Toggle>();
                nova.group = null;
                nova.onValueChanged = new Toggle.ToggleEvent(); // not the Next Generation's own (persistent) handler
                nova.isOn = false;
                foreach (var label in copy.GetComponentsInChildren<TMP_Text>(true)) label.SetLocalizedText("NOVA");
                foreach (var t in copy.GetComponents<EventTrigger>()) UnityEngine.Object.Destroy(t);
                nova.onValueChanged.AddListener(on => Chose(box, on));
                box.Nova = nova;

                foreach (var image in new[] { Field<Image>(console, "_tngToggleBackground1"), Field<Image>(console, "_tngToggleBackground2") })
                    if (image != null && !image.transform.IsChildOf(tng.transform))
                    {
                        box.TngExtras.Add(image.transform);
                        var extra = (GameObject)UnityEngine.Object.Instantiate(image.gameObject, image.transform.parent, false);
                        extra.name = image.name + " (Nova)";
                        box.NovaExtras.Add(extra.GetComponent<Graphic>());
                    }

                var rows = Rows(box);

                // Choosing one of the game's ships takes the Nova off the room.
                foreach (var own in new[] { aegis, tos, tng })
                {
                    var t = own;
                    t.onValueChanged.AddListener(on => { if (on && !box.Applying && NovaShip.Active) NovaShip.SetForRoom(false); });
                }

                // A click on the row the Nova flies on while the Nova is chosen: back to that ship (its toggle is on
                // already, so it says nothing itself).
                foreach (var under in new[] { aegis, tng })
                {
                    var u = under;
                    var trigger = u.gameObject.GetComponent<EventTrigger>() ?? u.gameObject.AddComponent<EventTrigger>();
                    var click = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
                    click.callback.AddListener(_ => { if (NovaShip.Active && u.isOn) NovaShip.SetForRoom(false); });
                    if (trigger.triggers == null) trigger.triggers = new List<EventTrigger.Entry>();
                    trigger.triggers.Add(click);
                }

                boxes.Add(box);
                Layout(box);
                Plugin.Log.LogInfo($"Ship choice: NOVA added to the ship box ({rows.Length} ships in its three rows' room); the row's parts: "
                    + string.Join(", ", copy.GetComponentsInChildren<Transform>(true).Select(t => t.name + (t.gameObject.activeSelf ? "" : " (off)")).ToArray()));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Ship choice: " + e); }
        }

        private static Toggle[] Rows(Box box) => new[] { box.Aegis, box.Tos, box.Tng, box.Nova };

        private static void Chose(Box box, bool on)
        {
            if (box.Applying || !on) return;
            box.Applying = true;
            try
            {
                NovaShip.SetForRoom(true);
                // The game's own choice under it: the Next Generation's ship if the crew can have it, else the Aegis's.
                var under = box.Tng.interactable || box.Tng.isOn ? box.Tng : box.Aegis;
                if (!under.isOn) under.isOn = true;
            }
            finally { box.Applying = false; }
        }

        /// <summary>
        /// All four rows in the room the game's three take, no scrolling: the rows a little smaller and closer together.
        /// The space from the top of the first row to the bottom of the third is shared by four; a row is scaled down
        /// only as far as it must be to keep a gap. The Next Generation's orange backgrounds move with its row, NOVA's
        /// copies with NOVA's.
        /// </summary>
        private static void Layout(Box box)
        {
            var rows = Rows(box);
            var top = box.Aegis.transform.localPosition;
            float step = box.Step.magnitude;
            if (step < 1e-4f) return;
            var down = box.Step / step;
            var rect = (RectTransform)box.Aegis.transform;
            float rowHeight = Mathf.Max(1e-3f, rect.rect.height * rect.localScale.y);
            float span = 2f * step + rowHeight;                      // the game's three rows, top to bottom
            float scale = Mathf.Min(1f, span / (4f * rowHeight * 1.12f)); // a little gap between rows
            float h = rowHeight * scale, newStep = (span - h) / 3f;
            var tngHome = box.Tng.transform.localPosition;
            var extraOffsets = box.TngExtras.Select(t => t.localPosition - tngHome).ToArray();
            var extraScales = box.TngExtras.Select(t => t.localScale).ToArray();
            var baseScale = box.Aegis.transform.localScale;
            for (int i = 0; i < rows.Length; i++)
            {
                var row = rows[i].transform;
                row.localPosition = top + down * ((rowHeight - h) / 2f + i * newStep);
                row.localScale = baseScale * scale;
            }
            for (int k = 0; k < box.TngExtras.Count; k++)
            {
                box.TngExtras[k].localPosition = box.Tng.transform.localPosition + extraOffsets[k] * scale;
                box.TngExtras[k].localScale = extraScales[k] * scale;
                var n = box.NovaExtras[k].transform;
                n.localPosition = box.Nova.transform.localPosition + extraOffsets[k] * scale;
                n.localScale = extraScales[k] * scale;
            }
            Plugin.Log.LogInfo($"Ship choice: rows {rowHeight:0.##} high every {step:0.##}; four at {scale:0.00} size every {newStep:0.##}");
        }

        private static readonly System.Reflection.FieldInfo TngHologramField = AccessTools.Field(typeof(ConsoleBriefingRoom), "IMHCKEMPKJA");

        /// <summary>
        /// The holotable: the console shows one ship hologram at a time (SetHologramModels gives it the Aegis's, the
        /// original Enterprise's and the Enterprise-D's). The Nova's goes under the Enterprise-D's, which is the one shown
        /// with NOVA chosen: its meshes and materials (the Nova body's own, lit like the other ships' models), as long;
        /// the Enterprise-D's parts are hidden while it shows.
        /// </summary>
        private static void Hologram(Box box, bool nova)
        {
            // A piece put on the Nova since (its name decal): the hologram is made again with it.
            if (box.NovaHologram != null && box.HologramVersion != NovaShip.Version && NovaShip.ModelParts.Length != box.HologramParts)
            {
                UnityEngine.Object.Destroy(box.NovaHologram);
                box.NovaHologram = null;
                if (box.TngHologram != null) foreach (var r in box.TngHologram) if (r != null) r.enabled = true;
            }
            if (box.NovaHologram == null)
            {
                var tng = TngHologramField?.GetValue(box.Console) as GameObject;
                var parts = NovaShip.ModelParts;
                if (tng == null || parts.Length == 0) return;
                box.TngHologram = tng.GetComponentsInChildren<Renderer>(true).Where(r => r is MeshRenderer).ToArray();
                if (box.TngHologram.Length == 0) return;
                // The ship itself among its parts (there are window layers and effects too): the hologram shader's (it
                // has a noise texture), the most vertices.
                var first = box.TngHologram.Where(r => r.GetComponent<MeshFilter>()?.sharedMesh != null)
                    .OrderByDescending(r => r.sharedMaterial != null && r.sharedMaterial.HasProperty("_Noise") ? 1 : 0)
                    .ThenByDescending(r => r.GetComponent<MeshFilter>().sharedMesh.vertexCount).FirstOrDefault();
                if (first == null) return;
                var space = first.transform.parent ?? tng.transform;
                // That ship's size, in the space its mesh hangs in (forward z, up y, as the ships' bodies).
                Vector3 lo = Vector3.one * float.MaxValue, hi = Vector3.one * float.MinValue;
                foreach (var r in new[] { first })
                {
                    var f = r.GetComponent<MeshFilter>(); if (f == null || f.sharedMesh == null) continue;
                    var b = f.sharedMesh.bounds;
                    for (int i = 0; i < 8; i++)
                    {
                        var c = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                        var p = space.InverseTransformPoint(r.transform.TransformPoint(c));
                        lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p);
                    }
                }
                Vector3 novaLo = Vector3.one * float.MaxValue, novaHi = Vector3.one * float.MinValue;
                foreach (var f in parts) { var b = f.sharedMesh.bounds; novaLo = Vector3.Min(novaLo, b.min); novaHi = Vector3.Max(novaHi, b.max); }
                float length = Mathf.Max(hi.z - lo.z, hi.x - lo.x);
                float scale = length / Mathf.Max(1e-3f, novaHi.z - novaLo.z);

                var holo = new GameObject("Nova Hologram");
                holo.layer = first.gameObject.layer;
                holo.transform.SetParent(space, false);
                holo.transform.localPosition = (lo + hi) / 2f - (novaLo + novaHi) / 2f * scale;
                holo.transform.localScale = Vector3.one * scale;
                var material = new UnityEngine.Material(first.sharedMaterial) { name = "Nova Hologram" };
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
                box.NovaHologram = holo;
                box.HologramParts = parts.Length;
                Plugin.Log.LogInfo($"Ship choice: Nova hologram under {tng.name} ({parts.Length} parts, scale {scale:0.###}, {box.TngHologram.Length} Enterprise-D parts)");
            }
            box.HologramVersion = NovaShip.Version;
            if (box.NovaHologram == null) return;
            if (box.NovaHologram.activeSelf != nova) box.NovaHologram.SetActive(nova);
            foreach (var r in box.TngHologram) if (r != null && r.enabled == nova) r.enabled = !nova;
        }

        /// <summary>Each frame: NOVA's mark and whether it can be clicked follow the room; the TNG row hides its mark under it.</summary>
        private static float nextUpdate;

        internal static void Update()
        {
            if (boxes.Count == 0 || Time.unscaledTime < nextUpdate) return;
            nextUpdate = Time.unscaledTime + 0.2f; // five times a second is plenty for a mark and a hologram
            boxes.RemoveAll(b => b.Console == null || b.Nova == null);
            bool nova = NovaShip.ActiveNow;
            foreach (var box in boxes)
            {
                try
                {
                    Hologram(box, nova);
                    box.Applying = true;
                    bool on = nova && (box.Tng.isOn || box.Aegis.isOn);
                    if (box.Nova.isOn != on) box.Nova.isOn = on;
                    // Choosable when the game's own choice is (the host's or captain's; not while it's locked).
                    bool canChoose = box.Tng.interactable || box.Aegis.interactable;
                    if (box.Nova.interactable != canChoose) box.Nova.interactable = canChoose;
                    foreach (var g in box.NovaExtras) if (g != null) g.canvasRenderer.SetAlpha(canChoose ? 1f : 0.35f);
                    // The row the Nova flies on shows no mark of its own while NOVA's shows.
                    foreach (var under in new[] { box.Tng, box.Aegis })
                        if (under.graphic != null) under.graphic.canvasRenderer.SetAlpha(on && under.isOn ? 0f : (under.isOn ? 1f : 0f));
                    box.Applying = false;
                }
                catch (Exception e) { box.Applying = false; Plugin.Log.LogDebug("Ship choice: " + e.Message); }
            }
        }
    }
}
