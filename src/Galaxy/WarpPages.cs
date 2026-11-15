using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival.Galaxy
{
    /// <summary>
    /// The warp map's second page: with a galaxy region on (GalaxyRegion.Active), every warp panel (the helm console's,
    /// the viewscreen's, the captain's) shows either the Trench's system buttons (page 1) or the region's (page 2), with
    /// a page button to flip between them. Without a region the panels are left exactly as the game builds them.
    ///
    /// - The page button is a clone of the panel's own location template (a WarpMapLocation), so it takes VR hand pokes
    ///   and the desktop mouse exactly like a system button. It is not in the panel's Locations, so none of the game's
    ///   own button code (selection, availability, current location, cycling) ever sees it. Its press reaches
    ///   WarpMapLocation.OnClicked (the button's persistent onClick); a prefix there recognises our clones and flips the
    ///   page instead of plotting a course. A runtime onClick listener does the same in case the press arrives another
    ///   way; both are guarded to one flip per frame. Without that prefix in place no button is ever made.
    /// - A page shows by activating its own location GameObjects and deactivating the other page's. Nothing in the game
    ///   activates or deactivates location GameObjects (ShowMap and the rest go through UIVisibilityController, which
    ///   only touches canvas alpha, layers, renderers and component enables), so hidden buttons stay hidden; every
    ///   game loop over Locations works on inactive objects too.
    /// - Page 2's background: the panel's map art (GalaxyCatalogue.FindMapArt) darkened to 40%, or a PNG next to the
    ///   galaxy file when the file's page background names one. Page 1 restores it.
    /// - The page follows the ship: when the map is shown, two frames after it's built, and on arrival. The ship icon
    ///   shows only on its own page; on a warp to the other page the map shows the ship's page and the icon flies to
    ///   the page's edge toward the destination. A viewscreen cycle (CycleTargetEvent) that lands on the other page
    ///   flips to it.
    ///
    /// Every piece is in try/catch; a failure on a panel puts that panel back as the game made it (all buttons
    /// active, art and ship icon restored, our button removed).
    /// </summary>
    internal static class WarpPages
    {
        private const string P = "Galaxy pages: ";
        private const string Trench = "The Trench";
        private const float Darken = 0.4f;

        private sealed class PanelState
        {
            public WarpPanel Panel;
            public string Name;
            public WarpMapLocation Button;
            public int Page;                 // 0 until first shown
            public int BuiltFrame;
            public bool Warping;             // between the Flying phase and Deactivated
            public int WarpFromPage;
            public int LastFlipFrame = -1;
            public UnityEngine.UI.Graphic Art;
            public Color ArtColor;
            public Sprite ArtSprite;
            public Texture ArtTexture;
            public Rect ArtUv;
        }

        private static readonly Dictionary<WarpPanel, PanelState> panels = new Dictionary<WarpPanel, PanelState>();
        private static readonly Dictionary<WarpMapLocation, PanelState> buttons = new Dictionary<WarpMapLocation, PanelState>();
        private static bool clickPatched;
        private static float lastCycle = -10f;

        // A loaded page background PNG, kept for the session (by path and write time).
        private static string pngKey;
        private static Texture2D pngTexture;
        private static Sprite pngSprite;

        // Game members (obfuscated names first, readable ones as a fallback).
        private static FieldInfo fLocations, fTemplate, fMapBase, fMapRect, fPlayerShip, fViewScreen, fLerpTarget;
        private static MethodInfo mCurrentLocation;

        internal static void Patch(Harmony harmony)
        {
            var wp = typeof(WarpPanel);
            fLocations = Field(wp, "JBDJGIKEICO", "Locations");
            fTemplate = Field(wp, "_locationTemplate");
            fMapBase = Field(wp, "_mapBase");
            fMapRect = Field(wp, "_mapRect");
            fPlayerShip = Field(wp, "_playerShip");
            fViewScreen = Field(wp, "_viewScreenPanel");
            fLerpTarget = Field(wp, "BMOBDOGKJBG");
            mCurrentLocation = Method(wp, "HBKEIMBBMHI", "CurrentLocation");
            if (fLocations == null || fTemplate == null || fMapBase == null || fMapRect == null || fPlayerShip == null || mCurrentLocation == null)
            {
                Plugin.Log.LogWarning(P + "a WarpPanel member is missing; no second page");
                return;
            }

            var build = Method(wp, "KBDEILBCKEP", "BuildLocations");
            var click = AccessTools.Method(typeof(WarpMapLocation), "OnClicked");
            if (build == null || click == null)
            {
                Plugin.Log.LogWarning(P + "WarpPanel.BuildLocations or WarpMapLocation.OnClicked not found; no second page");
                return;
            }
            harmony.Patch(click, prefix: new HarmonyMethod(typeof(WarpPages), nameof(Clicked_Prefix)));
            clickPatched = true;
            harmony.Patch(build, postfix: new HarmonyMethod(typeof(WarpPages), nameof(Build_Postfix)));

            Optional(harmony, Method(wp, "ShowMap"), nameof(ShowMap_Postfix), "ShowMap (follow the ship when shown, button visibility)");
            Optional(harmony, Method(wp, "Update"), nameof(Update_Postfix), "Update (button visibility, first page)");
            Optional(harmony, Method(wp, "CPHKHCOGNKB", "OnWarpStateChangedEvent"), nameof(WarpState_Postfix), "OnWarpStateChangedEvent (warps across pages)");
            Optional(harmony, Method(wp, "EACKJFFJDGH", "TrySelectLocation"), nameof(TrySelect_Postfix), "TrySelectLocation (cycling to the other page)");
            Optional(harmony, Method(wp, "AGILIGPOHHB", "OnCycleTargetEvent"), nameof(Cycle_Postfix), "OnCycleTargetEvent");
            Optional(harmony, Method(wp, "LockInteraction"), nameof(Lock_Postfix), "LockInteraction (lock the page button too)");
            if (fLerpTarget == null) Plugin.Log.LogWarning(P + "the ship icon's warp target field is missing; it flies to the system on the other page");
        }

        private static void Optional(Harmony harmony, MethodInfo m, string patch, string what)
        {
            if (m == null)
            {
                Plugin.Log.LogWarning(P + "not found: WarpPanel." + what);
                return;
            }
            try { harmony.Patch(m, postfix: new HarmonyMethod(typeof(WarpPages), patch)); }
            catch (Exception e) { Plugin.Log.LogWarning(P + "patching WarpPanel." + what + ": " + e.Message); }
        }

        private static FieldInfo Field(Type t, params string[] names)
        {
            foreach (var n in names)
            {
                var f = AccessTools.Field(t, n);
                if (f != null) return f;
            }
            return null;
        }

        private static MethodInfo Method(Type t, params string[] names)
        {
            foreach (var n in names)
            {
                var m = AccessTools.Method(t, n);
                if (m != null) return m;
            }
            return null;
        }

        private static object Member(object o, params string[] names)
        {
            var t = o is Type type ? Traverse.Create(type) : Traverse.Create(o);
            foreach (var n in names)
            {
                var p = t.Property(n);
                if (p.PropertyExists()) return p.GetValue();
                var f = t.Field(n);
                if (f.FieldExists()) return f.GetValue();
            }
            return null;
        }

        private static Dictionary<int, WarpMapLocation> Locations(WarpPanel panel) =>
            fLocations.GetValue(panel) as Dictionary<int, WarpMapLocation>;

        private static int PageOf(int systemId) => GalaxyGate.IsRegionSystem(systemId, GalaxyRegion.TrenchCount) ? 2 : 1;

        private static string PageName(int page)
        {
            if (page == 1) return Trench;
            var label = GalaxyRegion.BuiltPage?.Label;
            return !string.IsNullOrEmpty(label) ? label : GalaxyRegion.RegionName;
        }

        private static PanelState StateOf(WarpPanel panel) =>
            panels.Count > 0 && panel != null && panels.TryGetValue(panel, out var s) ? s : null;

        // ---- Building ---------------------------------------------------------------------------------------------

        private static void Build_Postfix(WarpPanel __instance)
        {
            if (!GalaxyRegion.Active || !clickPatched) return;
            try
            {
                Prune();
                if (panels.ContainsKey(__instance)) return;
                Setup(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(P + "setting up a warp panel failed; it stays as the game made it: " + e);
                Restore(__instance);
            }
        }

        private static void Prune()
        {
            var gone = new List<WarpPanel>();
            foreach (var kv in panels) if (kv.Key == null) gone.Add(kv.Key);
            foreach (var p in gone)
            {
                var s = panels[p];
                panels.Remove(p);
                if (s.Button != null) buttons.Remove(s.Button);
            }
            var deadButtons = new List<WarpMapLocation>();
            foreach (var kv in buttons) if (kv.Key == null) deadButtons.Add(kv.Key);
            foreach (var b in deadButtons) buttons.Remove(b);
        }

        private static void Setup(WarpPanel panel)
        {
            var locations = Locations(panel);
            if (locations == null || locations.Count == 0) return;
            int region = 0, trench = 0;
            foreach (var kv in locations) if (PageOf(kv.Key) == 2) region++; else trench++;
            var s = new PanelState { Panel = panel, Name = NameOf(panel), BuiltFrame = Time.frameCount };
            if (region == 0)
            {
                Plugin.Log.LogInfo(P + $"{s.Name}: no region system among its {locations.Count} buttons; no second page");
                return;
            }

            var mapBase = fMapBase.GetValue(panel) as GameObject;
            var mapRect = fMapRect.GetValue(panel) as RectTransform;
            var template = fTemplate.GetValue(panel) as WarpMapLocation;
            if (mapBase == null || mapRect == null) throw new InvalidOperationException("no _mapBase or _mapRect");

            // The page button: made the way BuildLocations makes a system button (visible template, instantiate under
            // _mapBase, Initialize), then relabelled.
            WarpMapLocation first = null;
            foreach (var l in locations.Values) { first = l; break; }
            var source = template != null ? template : first;
            if (source == template) template.SetVisible(true);
            WarpMapLocation button;
            try { button = UnityEngine.Object.Instantiate(source, mapBase.transform, false) as WarpMapLocation; }
            finally { if (source == template) template.SetVisible(false); }
            if (button == null) throw new InvalidOperationException("cloning the location template gave nothing");
            s.Button = button;
            panels[panel] = s; // from here on Restore() can undo it
            buttons[button] = s;
            button.gameObject.name = "Galaxy Page Button";
            var anySystem = Member(first, "PBCKCOJGAEI", "System") as Map.SystemRoot;
            if (anySystem == null) throw new InvalidOperationException("no system to initialise the page button with");
            button.Initialize(anySystem, panel);
            try
            {
                // Locked like the system buttons (BuildLocations sets IsLocked from the panel's lock flag).
                var locked = AccessTools.Field(typeof(WarpPanel), "JILJODOODBM");
                if (locked != null) Traverse.Create(button).Property("OBBDBEFMGGG").SetValue((bool)locked.GetValue(panel));
            }
            catch (Exception e) { Plugin.Log.LogWarning(P + $"{s.Name}: locking the page button like the others: {e.Message}"); }

            var ui = button.GetComponentInChildren<UnityEngine.UI.Button>(true);
            if (ui != null) ui.onClick.AddListener(() => Pressed(s, "onClick"));

            var rect = mapRect.rect;
            Vector2 corner = Corner(rect, locations);
            button.transform.localPosition = new Vector3(corner.x, corner.y, 0f);
            button.transform.SetAsLastSibling();
            var ship = fPlayerShip.GetValue(panel) as GameObject;
            if (ship != null) ship.transform.SetAsLastSibling(); // the ship icon stays on top, as BuildLocations leaves it

            try
            {
                // As BuildLocations does after making its buttons (HandAnimatorManager is internal: by reflection).
                var type = AccessTools.TypeByName("HandAnimatorManager");
                var hands = type != null ? Member(type, "IEFBKOMBFGO", "Instance") : null;
                var console = Member(panel, "KHDENOMKIJE", "Console");
                var notify = type != null ? AccessTools.Method(type, "NotifyAnimatorsForConsole") : null;
                if (hands is UnityEngine.Object o && o != null && console != null && notify != null) notify.Invoke(hands, new[] { console });
            }
            catch (Exception e) { Plugin.Log.LogWarning(P + $"{s.Name}: telling the hand animators about the page button: {e.Message}"); }

            try
            {
                s.Art = GalaxyCatalogue.FindMapArt(panel, false);
                foreach (var other in panels.Values)
                    if (other != s && other.Art != null && other.Art == s.Art) s.Art = null; // another panel already owns it
                if (s.Art != null)
                {
                    s.ArtColor = s.Art.color;
                    if (s.Art is UnityEngine.UI.Image img) s.ArtSprite = img.sprite;
                    else if (s.Art is UnityEngine.UI.RawImage raw) { s.ArtTexture = raw.texture; s.ArtUv = raw.uvRect; }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning(P + $"{s.Name}: finding the map art: {e.Message}"); s.Art = null; }

            SetLabel(s, PageName(2));
            Plugin.Log.LogInfo(P + $"{s.Name}: {trench} Trench and {region} region buttons; page button at ({corner.x:0}, {corner.y:0}) "
                + $"in a map of {rect.width:0} x {rect.height:0}; map art {(s.Art != null ? GalaxyCatalogue.PathOf(s.Art.transform) : "not found (page 2 keeps the Trench art)")}");
        }

        private static string NameOf(WarpPanel panel)
        {
            try
            {
                if (fViewScreen != null && (bool)fViewScreen.GetValue(panel)) return "viewscreen panel";
                var console = Member(panel, "KHDENOMKIJE", "Console");
                if (console != null) return console.GetType().Name + " panel";
            }
            catch (Exception) { }
            return "warp panel " + panel.GetInstanceID();
        }

        // The corner of the map (inset so the button and its label stay inside) farthest from every system button.
        private static Vector2 Corner(Rect r, Dictionary<int, WarpMapLocation> locations)
        {
            float ix = r.width * 0.1f, iy = r.height * 0.1f;
            var corners = new[]
            {
                new Vector2(r.xMin + ix, r.yMin + iy), new Vector2(r.xMax - ix * 2f, r.yMin + iy),
                new Vector2(r.xMin + ix, r.yMax - iy), new Vector2(r.xMax - ix * 2f, r.yMax - iy),
            };
            Vector2 best = corners[0];
            float bestDist = -1f;
            foreach (var c in corners)
            {
                float nearest = float.MaxValue;
                foreach (var l in locations.Values)
                {
                    if (l == null) continue;
                    Vector2 p = l.transform.localPosition;
                    nearest = Math.Min(nearest, (p - c).sqrMagnitude);
                }
                if (nearest > bestDist) { bestDist = nearest; best = c; }
            }
            return best;
        }

        private static void SetLabel(PanelState s, string text)
        {
            var label = Member(s.Button, "_systemNameLabel") as Component;
            if (label == null) return;
            Traverse.Create(label).Property("text").SetValue(text);
            var bg = Member(s.Button, "_systemNameBG") as UnityEngine.UI.Image;
            if (bg != null)
            {
                var width = Traverse.Create(label).Property("preferredWidth").GetValue();
                if (width is float w) bg.rectTransform.sizeDelta = new Vector2(w + 10f, bg.rectTransform.sizeDelta.y);
            }
        }

        // ---- The button ---------------------------------------------------------------------------------------------

        private static bool Clicked_Prefix(WarpMapLocation __instance)
        {
            if (buttons.Count == 0 || __instance == null || !buttons.TryGetValue(__instance, out var s)) return true;
            Pressed(s, "press");
            return false; // never plot a course to the system the button was initialised with
        }

        private static void Pressed(PanelState s, string how)
        {
            try
            {
                if (s.Panel == null || s.LastFlipFrame == Time.frameCount) return;
                ShowPage(s, s.Page == 2 ? 1 : 2, how);
            }
            catch (Exception e) { Fail(s, "flipping the page", e); }
        }

        // ---- Showing a page -------------------------------------------------------------------------------------------

        private static void ShowPage(PanelState s, int page, string why)
        {
            var panel = s.Panel;
            var locations = Locations(panel);
            if (locations == null) return;
            foreach (var kv in locations)
            {
                if (kv.Value == null) continue;
                bool on = PageOf(kv.Key) == page;
                if (kv.Value.gameObject.activeSelf != on) kv.Value.gameObject.SetActive(on);
            }
            Background(s, page);
            SetLabel(s, PageName(page == 2 ? 1 : 2));
            bool changed = s.Page != page;
            s.Page = page;
            s.LastFlipFrame = Time.frameCount;
            ShipIcon(s);
            if (changed) Plugin.Log.LogInfo(P + $"{s.Name}: page {page} ({PageName(page)}), {why}");
        }

        private static void Background(PanelState s, int page)
        {
            var art = s.Art;
            if (art == null) return;
            if (page == 1)
            {
                art.color = s.ArtColor;
                if (art is UnityEngine.UI.Image img) { if (img.sprite != s.ArtSprite) img.sprite = s.ArtSprite; }
                else if (art is UnityEngine.UI.RawImage raw) { raw.texture = s.ArtTexture; raw.uvRect = s.ArtUv; }
                return;
            }
            var png = PagePng();
            if (png != null)
            {
                art.color = new Color(1f, 1f, 1f, s.ArtColor.a);
                if (art is UnityEngine.UI.Image img) img.sprite = pngSprite;
                else if (art is UnityEngine.UI.RawImage raw) { raw.texture = png; raw.uvRect = new Rect(0f, 0f, 1f, 1f); }
            }
            else art.color = new Color(s.ArtColor.r * Darken, s.ArtColor.g * Darken, s.ArtColor.b * Darken, s.ArtColor.a);
        }

        // The file's page background when it names a PNG next to the galaxy file; null for the darkened Trench art.
        private static Texture2D PagePng()
        {
            var name = GalaxyRegion.BuiltPage?.Background;
            var folder = GalaxyRegion.BuiltFolder;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(folder) || name == "trench-darkened") return null;
            if (name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name.Contains("..")) return null;
            if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) name += ".png";
            var path = Path.Combine(folder, name);
            try
            {
                if (!File.Exists(path))
                {
                    if (pngKey != path) Plugin.Log.LogWarning(P + $"page background {name} not found next to the galaxy file; the Trench art darkened");
                    pngKey = path;
                    return null;
                }
                var key = path + "|" + File.GetLastWriteTime(path).Ticks;
                if (key == pngKey && pngTexture != null) return pngTexture;
                pngKey = key;
                var tex = new Texture2D(2, 2, TextureFormat.ARGB32, false);
                if (!tex.LoadImage(File.ReadAllBytes(path)))
                {
                    Plugin.Log.LogWarning(P + $"page background {name} isn't a readable PNG; the Trench art darkened");
                    UnityEngine.Object.Destroy(tex);
                    return null;
                }
                tex.wrapMode = TextureWrapMode.Clamp;
                // The previous one (the file changed) is left alone: a panel may still show it until its next flip.
                pngTexture = tex;
                pngSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                Plugin.Log.LogInfo(P + $"page background {name}: {tex.width} x {tex.height}");
                return pngTexture;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning(P + $"page background {name}: {e.Message}; the Trench art darkened");
                return null;
            }
        }

        // The page the ship icon belongs on: where the ship is, or where it left from while warping. 0 = unknown.
        private static int ShipPage(PanelState s)
        {
            if (s.Warping) return s.WarpFromPage;
            var here = mCurrentLocation.Invoke(s.Panel, null) as WarpMapLocation;
            if (here == null) return 0;
            var locations = Locations(s.Panel);
            if (locations != null) foreach (var kv in locations) if (kv.Value == here) return PageOf(kv.Key);
            return 0;
        }

        private static void ShipIcon(PanelState s)
        {
            var ship = fPlayerShip.GetValue(s.Panel) as GameObject;
            if (ship == null) return;
            int page = ShipPage(s);
            bool on = page == 0 || page == s.Page;
            if (ship.activeSelf != on) ship.SetActive(on);
        }

        // ---- Following the game -------------------------------------------------------------------------------------

        private static void ShowMap_Postfix(WarpPanel __instance, bool __0)
        {
            var s = StateOf(__instance);
            if (s == null) return;
            try
            {
                if (s.Button != null) s.Button.SetVisible(__0);
                if (__0) ShowPage(s, Follow(s), "the map was shown");
            }
            catch (Exception e) { Fail(s, "showing the map", e); }
        }

        private static int Follow(PanelState s)
        {
            int page = ShipPage(s);
            return page != 0 ? page : (s.Page != 0 ? s.Page : 1);
        }

        private static void Update_Postfix(WarpPanel __instance)
        {
            var s = StateOf(__instance);
            if (s == null) return;
            try
            {
                if (s.Button == null) { Fail(s, "the page button is gone", null); return; }
                // The game shows and hides its system buttons together (ShowMap, and two frames after building);
                // the page button follows them.
                var locations = Locations(__instance);
                WarpMapLocation any = null;
                if (locations != null) foreach (var l in locations.Values) { any = l; break; }
                if (any != null && any.KNPHKBGGHOC != s.Button.KNPHKBGGHOC) s.Button.SetVisible(any.KNPHKBGGHOC); // IsVisible
                if (s.Page == 0 && Time.frameCount >= s.BuiltFrame + 3) ShowPage(s, Follow(s), "built");
            }
            catch (Exception e) { Fail(s, "keeping the page button in step", e); }
        }

        private static void WarpState_Postfix(WarpPanel __instance, object __0)
        {
            var s = StateOf(__instance);
            if (s == null || __0 == null) return;
            try
            {
                var phase = Member(__0, "LEMJOCBJHEB", "NewPhase");
                string name = phase != null ? phase.ToString() : "";
                if (name == "Flying") Departing(s);
                else if (name == "Deactivated")
                {
                    s.Warping = false;
                    ShowPage(s, Follow(s), "arrived");
                }
            }
            catch (Exception e) { Fail(s, "following a warp", e); }
        }

        // The warp's Flying phase: the game has just set the ship icon's lerp from where it is to the destination's
        // button. A destination on the other page: show the ship's page and fly the icon to its edge instead.
        private static void Departing(PanelState s)
        {
            int from = ShipPage(s);
            s.WarpFromPage = from;
            s.Warping = true;
            if (from == 0 || fLerpTarget == null) return;
            var locations = Locations(s.Panel);
            var mapRect = fMapRect.GetValue(s.Panel) as RectTransform;
            var ship = fPlayerShip.GetValue(s.Panel) as GameObject;
            if (locations == null || mapRect == null || ship == null) return;

            var target = (Vector3)fLerpTarget.GetValue(s.Panel);
            int to = 0;
            WarpMapLocation dest = null;
            foreach (var kv in locations)
                if (kv.Value != null && (kv.Value.transform.position - target).sqrMagnitude < 1e-8f) { to = PageOf(kv.Key); dest = kv.Value; break; }
            ShowPage(s, from, "warp started");
            if (dest == null || to == from) return;

            Vector3 start = mapRect.InverseTransformPoint(ship.transform.position);
            Vector3 end = mapRect.InverseTransformPoint(target);
            Vector2 dir = new Vector2(end.x - start.x, end.y - start.y);
            if (dir.sqrMagnitude < 1f && s.Button != null)
            {
                Vector3 b = mapRect.InverseTransformPoint(s.Button.transform.position);
                dir = new Vector2(b.x - start.x, b.y - start.y);
            }
            if (dir.sqrMagnitude < 1f) dir = Vector2.right;
            Vector2 edge = EdgeToward(mapRect.rect, new Vector2(start.x, start.y), dir.normalized);
            var world = mapRect.TransformPoint(new Vector3(edge.x, edge.y, start.z));
            fLerpTarget.SetValue(s.Panel, world);
            Plugin.Log.LogInfo(P + $"{s.Name}: warp to page {to}; the ship icon flies to ({edge.x:0}, {edge.y:0}), the edge of page {from}");
        }

        // Where a ray from a point inside the rectangle leaves it.
        private static Vector2 EdgeToward(Rect r, Vector2 from, Vector2 dir)
        {
            from = new Vector2(Mathf.Clamp(from.x, r.xMin, r.xMax), Mathf.Clamp(from.y, r.yMin, r.yMax));
            float t = float.MaxValue;
            if (dir.x > 1e-4f) t = Math.Min(t, (r.xMax - from.x) / dir.x);
            else if (dir.x < -1e-4f) t = Math.Min(t, (r.xMin - from.x) / dir.x);
            if (dir.y > 1e-4f) t = Math.Min(t, (r.yMax - from.y) / dir.y);
            else if (dir.y < -1e-4f) t = Math.Min(t, (r.yMin - from.y) / dir.y);
            return t == float.MaxValue ? from : from + dir * t;
        }

        private static void Cycle_Postfix() => lastCycle = Time.time;

        // The viewscreen's cycling picked a system: show its page (on every panel when the crew cycled just now, so
        // the helm's own map follows too; otherwise only on the panel that picked it, as when the map is first shown).
        private static void TrySelect_Postfix(WarpPanel __instance, int __0, bool __result)
        {
            if (!__result || panels.Count == 0) return;
            try
            {
                int page = PageOf(__0);
                bool cycled = Time.time - lastCycle < 1f;
                foreach (var s in new List<PanelState>(panels.Values))
                {
                    if (s.Panel == null || s.Warping || s.Page == page) continue;
                    if (s.Panel != __instance && !cycled) continue;
                    try { ShowPage(s, page, "a cycled warp target is there"); }
                    catch (Exception e) { Fail(s, "showing a cycled target's page", e); }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning(P + "cycling: " + e.Message); }
        }

        private static void Lock_Postfix(WarpPanel __instance, bool __0)
        {
            var s = StateOf(__instance);
            if (s == null || s.Button == null) return;
            try { Traverse.Create(s.Button).Property("OBBDBEFMGGG").SetValue(__0); }
            catch (Exception e) { Plugin.Log.LogWarning(P + $"{s.Name}: locking the page button: {e.Message}"); }
        }

        // ---- Failure ------------------------------------------------------------------------------------------------

        private static void Fail(PanelState s, string what, Exception e)
        {
            Plugin.Log.LogError(P + $"{s?.Name}: {what} failed; this panel goes back to the Trench map with every button"
                + (e != null ? ": " + e : ""));
            if (s != null) Restore(s.Panel);
        }

        // Back to the panel as the game made it: every location active, art and ship icon as they were, our button gone.
        private static void Restore(WarpPanel panel)
        {
            if (panel == null) return;
            panels.TryGetValue(panel, out var s);
            panels.Remove(panel);
            try
            {
                var locations = Locations(panel);
                if (locations != null)
                    foreach (var l in locations.Values)
                        if (l != null && !l.gameObject.activeSelf) l.gameObject.SetActive(true);
            }
            catch (Exception e) { Plugin.Log.LogError(P + "restoring the system buttons: " + e.Message); }
            try
            {
                var ship = fPlayerShip.GetValue(panel) as GameObject;
                if (ship != null && !ship.activeSelf) ship.SetActive(true);
            }
            catch (Exception e) { Plugin.Log.LogError(P + "restoring the ship icon: " + e.Message); }
            if (s == null) return;
            try { if (s.Art != null) Background(s, 1); }
            catch (Exception e) { Plugin.Log.LogError(P + "restoring the map art: " + e.Message); }
            try
            {
                if (s.Button != null)
                {
                    buttons.Remove(s.Button);
                    UnityEngine.Object.Destroy(s.Button.gameObject);
                }
            }
            catch (Exception e) { Plugin.Log.LogError(P + "removing the page button: " + e.Message); }
        }
    }
}
