using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace StbcRevival.Galaxy
{
    /// <summary>
    /// The warp map's second page: with a galaxy region on (GalaxyRegion.Active), every warp panel (the helm console's,
    /// the captain's, the viewscreen's) shows either the Trench's system buttons (page 1) or the region's (page 2), with
    /// a page button to flip between them. Without a region the panels are left exactly as the game builds them.
    ///
    /// - The page button is a copy of the helm's IMPULSE map-mode button (a console button: HandInteraction for VR pokes
    ///   and the desktop mouse, a Toggle, its own look), labelled with the page it switches to. On the helm it sits to
    ///   the right of WARP; on other panels at the right end of the map's title bar, scaled to the same share of the
    ///   map's width. Before the copy wakes up (it is made under an inactive holder) every UnityEvent on it is replaced
    ///   by an empty one and its Toggle leaves the mode buttons' group, so it can never change the map mode, and the
    ///   copied HandInteraction helper objects are removed (its Awake makes its own). A press reaches our own
    ///   IPointerClickHandler on the HandInteraction's object, and the Toggle's and any Button's events as a backup;
    ///   presses within 0.3 s count once. It is not a WarpMapLocation and not in Locations, so it can't plot a course.
    ///   It is shown only while the panel's system buttons are (so on the helm only in WARP mode).
    /// - A page shows by activating its own location GameObjects and deactivating the other page's. Nothing in the game
    ///   activates or deactivates location GameObjects (ShowMap and the rest go through UIVisibilityController, which
    ///   only touches canvas alpha, layers, renderers and component enables), so hidden buttons stay hidden; every
    ///   game loop over Locations works on inactive objects too.
    /// - Page 2's background: the panel's own map art (GalaxyCatalogue.FindMapArt within the panel) darkened to 40%, or
    ///   a PNG next to the galaxy file when the file's page background names one. Page 1 restores it.
    /// - The page follows the ship: when the map is shown, three frames after it's built, and on arrival. The ship icon
    ///   shows only on its own page; on a warp to the other page the map shows the ship's page and the icon flies to
    ///   the page's edge toward the destination. A crew cycle (CycleTargetEvent) that lands on the other page flips to it.
    ///
    /// Every piece is in try/catch; a failure on a panel puts that panel back as the game made it (all buttons
    /// active, art and ship icon restored, our button hidden and inert; never destroyed, as the game holds references).
    /// </summary>
    internal static class WarpPages
    {
        private const string P = "Galaxy pages: ";
        private const string Trench = "The Trench";
        private const float Darken = 0.4f;
        private const string Helper = "_ScaleAndAxesHelper"; // HandInteraction.Awake's own child object

        private sealed class PanelState
        {
            public WarpPanel Panel;
            public string Name;
            public GameObject Button;
            public readonly List<Component> Texts = new List<Component>();
            public string Label;             // what the button's text should say
            public int Page;                 // 0 until first shown
            public int BuiltFrame;
            public bool Warping;             // between the Flying phase and Deactivated
            public int WarpFromPage;
            public int LastFlipFrame = -1;
            public float LastPress = -10f;
            public bool LabelResetLogged;
            public bool Failed;              // restored to the vanilla map: the (hidden) button does nothing
            public UnityEngine.UI.Graphic Art;
            public Color ArtColor;
            public Sprite ArtSprite;
            public Texture ArtTexture;
            public Rect ArtUv;
        }

        private static readonly Dictionary<WarpPanel, PanelState> panels = new Dictionary<WarpPanel, PanelState>();
        private static float lastCycle = -10f;

        // A loaded page background PNG, kept for the session (by path and write time).
        private static string pngKey;
        private static Texture2D pngTexture;
        private static Sprite pngSprite;

        // Game members (obfuscated names first, readable ones as a fallback).
        private static FieldInfo fLocations, fTemplate, fMapRect, fPlayerShip, fViewScreen, fLerpTarget, fLocationLabel;
        private static MethodInfo mCurrentLocation;

        internal static void Patch(Harmony harmony)
        {
            var wp = typeof(WarpPanel);
            fLocations = Field(wp, "JBDJGIKEICO", "Locations");
            fTemplate = Field(wp, "_locationTemplate");
            fMapRect = Field(wp, "_mapRect");
            fPlayerShip = Field(wp, "_playerShip");
            fViewScreen = Field(wp, "_viewScreenPanel");
            fLerpTarget = Field(wp, "BMOBDOGKJBG");
            fLocationLabel = Field(wp, "_currentLocationLabel");
            mCurrentLocation = Method(wp, "HBKEIMBBMHI", "CurrentLocation");
            if (fLocations == null || fMapRect == null || fPlayerShip == null || mCurrentLocation == null)
            {
                Plugin.Log.LogWarning(P + "a WarpPanel member is missing; no second page");
                return;
            }

            var build = Method(wp, "KBDEILBCKEP", "BuildLocations");
            if (build == null)
            {
                Plugin.Log.LogWarning(P + "WarpPanel.BuildLocations not found; no second page");
                return;
            }
            harmony.Patch(build, postfix: new HarmonyMethod(typeof(WarpPages), nameof(Build_Postfix)));

            Optional(harmony, Method(wp, "ShowMap"), nameof(ShowMap_Postfix), "ShowMap (follow the ship when shown, button visibility)");
            Optional(harmony, Method(wp, "Update"), nameof(Update_Postfix), "Update (button visibility and label, first page)");
            Optional(harmony, Method(wp, "CPHKHCOGNKB", "OnWarpStateChangedEvent"), nameof(WarpState_Postfix), "OnWarpStateChangedEvent (warps across pages)");
            Optional(harmony, Method(wp, "EACKJFFJDGH", "TrySelectLocation"), nameof(TrySelect_Postfix), "TrySelectLocation (cycling to the other page)");
            Optional(harmony, Method(wp, "AGILIGPOHHB", "OnCycleTargetEvent"), nameof(Cycle_Postfix), "OnCycleTargetEvent");
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
            if (!GalaxyRegion.Active) return;
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
            foreach (var p in gone) panels.Remove(p);
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
            var mapRect = fMapRect.GetValue(panel) as RectTransform;
            if (mapRect == null) throw new InvalidOperationException("no _mapRect");

            panels[panel] = s; // from here on Restore() can undo it
            MakeButton(s, mapRect);

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
                s.Art = GalaxyCatalogue.FindMapArt(panel, false, true);
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

            Plugin.Log.LogInfo(P + $"{s.Name}: {trench} Trench and {region} region buttons; map art "
                + (s.Art != null ? GalaxyCatalogue.PathOf(s.Art.transform) : "not found under the panel (page 2 keeps the Trench art)"));
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

        // ---- The page button -------------------------------------------------------------------------------------------

        private static void MakeButton(PanelState s, RectTransform mapRect)
        {
            var panel = s.Panel;
            var ownConsole = Member(panel, "KHDENOMKIJE", "Console") as Component;
            var helm = ownConsole as ConsoleHelm;
            if (helm == null) helm = UnityEngine.Object.FindObjectOfType<ConsoleHelm>();
            if (helm == null) throw new InvalidOperationException("no helm console to take the button's look from");
            var impulse = ModeRoot(helm, "_impulseButton", "_systemToggle", "_localButton", "_warpButton");
            var warp = ModeRoot(helm, "_warpButton", "_warpToggle", "_localButton", "_impulseButton");
            if (impulse == null) throw new InvalidOperationException("the helm's IMPULSE button wasn't found");
            bool onHelm = ReferenceEquals(ownConsole, helm);
            Plugin.Log.LogInfo(P + $"{s.Name}: page button copied from {GalaxyCatalogue.PathOf(impulse.transform)}");

            // Our own frame, the size of the original button, holds the copy: a HandInteraction whose trigger box
            // follows its parent's rectangle then gets the button's size, wherever the copy goes.
            Transform parent = onHelm ? impulse.transform.parent : (Transform)mapRect;
            var source = impulse.transform as RectTransform;
            Vector2 size = source != null ? source.rect.size : new Vector2(100f, 30f);
            var frame = new GameObject("Galaxy Page Button", typeof(RectTransform));
            frame.SetActive(false);
            var frameRect = (RectTransform)frame.transform;
            frameRect.SetParent(parent, false);
            frameRect.anchorMin = frameRect.anchorMax = frameRect.pivot = new Vector2(0.5f, 0.5f);
            frameRect.sizeDelta = size;
            frameRect.localRotation = impulse.transform.localRotation;
            frameRect.localScale = impulse.transform.localScale;
            s.Button = frame;

            var button = UnityEngine.Object.Instantiate(impulse, frameRect, false) as GameObject;
            if (button == null) throw new InvalidOperationException("copying the IMPULSE button gave nothing");
            button.name = "Galaxy Page Button Face";
            if (button.transform is RectTransform face)
            {
                face.anchorMin = face.anchorMax = new Vector2(0.5f, 0.5f);
                face.sizeDelta = size;
                face.anchoredPosition = new Vector2((face.pivot.x - 0.5f) * size.x, (face.pivot.y - 0.5f) * size.y);
            }
            else button.transform.localPosition = Vector3.zero;
            button.transform.localRotation = Quaternion.identity;
            button.transform.localScale = Vector3.one;
            Neutralise(s, button, sameConsole: onHelm);

            if (onHelm)
            {
                // To the right of WARP, a little more than the buttons' spacing apart.
                Vector3 impulseAt = Centre(impulse), warpAt = warp != null ? Centre(warp) : impulseAt + impulse.transform.right * WorldWidth(impulse) * 1.1f;
                frameRect.position = warpAt + (warpAt - impulseAt) * 1.15f;
            }
            else
            {
                // The right end of the map's title bar, the same share of the map's width as on the helm.
                var helmPanel = helm.GetComponentInChildren<WarpPanel>(true);
                var helmMap = helmPanel != null ? fMapRect.GetValue(helmPanel) as RectTransform : null;
                float share = helmMap != null && WorldWidth(helmMap) > 0f && WorldWidth(impulse) > 0f ? WorldWidth(impulse) / WorldWidth(helmMap) : 0.15f;
                float want = share * WorldWidth(mapRect), have = WorldWidth(frameRect);
                if (have > 0f && want > 0f) frameRect.localScale = frameRect.localScale * (want / have);
                frameRect.localRotation = Quaternion.identity;
                var r = mapRect.rect;
                float halfW = size.x * frameRect.localScale.x / 2f, halfH = size.y * frameRect.localScale.y / 2f;
                float y = r.yMax + halfH * 1.5f;
                if (fLocationLabel != null && fLocationLabel.GetValue(panel) is Component label && label != null)
                    y = mapRect.InverseTransformPoint(label.transform.position).y;
                frameRect.localPosition = new Vector3(r.xMax - halfW, y, 0f);
            }
            frame.SetActive(true); // the copy wakes up here
            foreach (var toggle in button.GetComponentsInChildren<UnityEngine.UI.Toggle>(true))
            {
                var t = toggle;
                t.isOn = false;
                t.onValueChanged.AddListener(on => { if (on) { Pressed(s, "toggle"); t.isOn = false; } });
            }
            foreach (var b in button.GetComponentsInChildren<UnityEngine.UI.Button>(true))
                b.onClick.AddListener(() => Pressed(s, "button"));
            int handlers = 0;
            foreach (var hand in button.GetComponentsInChildren<HandInteraction>(true))
            {
                var click = hand.gameObject.GetComponent<PageButtonClick>() ?? hand.gameObject.AddComponent<PageButtonClick>();
                click.Clicked = () => Pressed(s, "press");
                handlers++;
            }
            if (handlers == 0) button.AddComponent<PageButtonClick>().Clicked = () => Pressed(s, "press");

            foreach (var c in button.GetComponentsInChildren<Component>(true))
                if (c is TMPro.TMP_Text || c is UnityEngine.UI.Text) s.Texts.Add(c);
            SetLabel(s, PageName(2));
            var kinds = new List<string>();
            foreach (var c in button.GetComponentsInChildren<Component>(true))
                if (c != null && !kinds.Contains(c.GetType().Name)) kinds.Add(c.GetType().Name);
            Plugin.Log.LogInfo(P + $"{s.Name}: page button at {frameRect.localPosition}, scale {frameRect.localScale.x:0.###}, under {GalaxyCatalogue.PathOf(parent)}, "
                + $"{handlers} hand interaction(s), {s.Texts.Count} text(s) reading '{ReadLabel(s)}'; parts: {string.Join(", ", kinds.ToArray())}");
        }

        // A helm map-mode button's root: its HandInteraction's object, or the smallest object holding both it and its
        // Toggle when that holds none of the other mode buttons.
        private static GameObject ModeRoot(ConsoleHelm helm, string buttonField, string toggleField, params string[] others)
        {
            var hand = AccessTools.Field(typeof(ConsoleHelm), buttonField)?.GetValue(helm) as Component;
            if (hand == null) return null;
            var toggle = AccessTools.Field(typeof(ConsoleHelm), toggleField)?.GetValue(helm) as Component;
            Transform root = hand.transform;
            if (toggle != null)
            {
                var both = Common(hand.transform, toggle.transform);
                bool clean = both != null;
                foreach (var name in others)
                {
                    var other = AccessTools.Field(typeof(ConsoleHelm), name)?.GetValue(helm) as Component;
                    if (other != null && both != null && other.transform.IsChildOf(both)) clean = false;
                }
                if (clean) root = both;
            }
            return root.gameObject;
        }

        private static Transform Common(Transform a, Transform b)
        {
            for (var t = a; t != null; t = t.parent)
                if (b.IsChildOf(t)) return t;
            return null;
        }

        // Before the copy wakes up: no UnityEvent of the original (they set the map mode), out of the mode buttons'
        // toggle group, no copied HandInteraction helpers; away from the helm, no helm hand grid and the panel's own
        // hand restriction (the captain's console is left-hand).
        private static void Neutralise(PanelState s, GameObject button, bool sameConsole)
        {
            foreach (var t in button.GetComponentsInChildren<Transform>(true))
                if (t != null && t != button.transform && t.name.EndsWith(Helper, StringComparison.Ordinal))
                    UnityEngine.Object.DestroyImmediate(t.gameObject);

            int events = 0;
            foreach (var behaviour in button.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null) continue;
                for (var type = behaviour.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
                    foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (!typeof(UnityEngine.Events.UnityEventBase).IsAssignableFrom(f.FieldType) || f.FieldType.IsAbstract) continue;
                        try { f.SetValue(behaviour, Activator.CreateInstance(f.FieldType)); events++; }
                        catch (Exception e) { Plugin.Log.LogWarning(P + $"{s.Name}: clearing {type.Name}.{f.Name}: {e.Message}"); }
                    }
                if (behaviour is EventTrigger trigger && trigger.triggers != null) trigger.triggers.Clear();
            }
            foreach (var toggle in button.GetComponentsInChildren<UnityEngine.UI.Toggle>(true)) toggle.group = null;

            if (!sameConsole)
            {
                var template = fTemplate?.GetValue(s.Panel) as Component;
                var templateHand = template != null ? template.GetComponentInChildren<HandInteraction>(true) : null;
                var side = AccessTools.Field(typeof(HandInteraction), "_sideRestriction");
                var grid = AccessTools.Field(typeof(HandInteraction), "_grid");
                foreach (var hand in button.GetComponentsInChildren<HandInteraction>(true))
                {
                    if (grid != null) grid.SetValue(hand, null);
                    if (side != null && templateHand != null) side.SetValue(hand, side.GetValue(templateHand));
                }
            }
            Plugin.Log.LogInfo(P + $"{s.Name}: page button: {events} copied event(s) emptied");
        }

        private static float WorldWidth(GameObject g) => g != null && g.transform is RectTransform r ? WorldWidth(r) : 0f;

        private static Vector3 Centre(GameObject g)
        {
            if (!(g.transform is RectTransform r)) return g.transform.position;
            var c = new Vector3[4];
            r.GetWorldCorners(c);
            return (c[0] + c[2]) / 2f;
        }

        private static float WorldWidth(RectTransform r)
        {
            var c = new Vector3[4];
            r.GetWorldCorners(c);
            return Vector3.Distance(c[0], c[3]);
        }

        private static float WorldHeight(GameObject g)
        {
            if (g == null || !(g.transform is RectTransform r)) return 0f;
            var c = new Vector3[4];
            r.GetWorldCorners(c);
            return Vector3.Distance(c[0], c[1]);
        }

        private static void SetLabel(PanelState s, string text)
        {
            s.Label = text.ToUpperInvariant();
            foreach (var c in s.Texts)
            {
                if (c is TMPro.TMP_Text tmp) tmp.OIKALDNFGKE = s.Label; // TMP_Text.text
                else if (c is UnityEngine.UI.Text ui) ui.text = s.Label;
            }
        }

        private static string ReadLabel(PanelState s)
        {
            foreach (var c in s.Texts)
            {
                if (c is TMPro.TMP_Text tmp) return tmp.OIKALDNFGKE;
                if (c is UnityEngine.UI.Text ui) return ui.text;
            }
            return null;
        }

        private static void Pressed(PanelState s, string how)
        {
            try
            {
                if (s.Failed || s.Panel == null || Time.time - s.LastPress < 0.3f) return;
                s.LastPress = Time.time;
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
            if (changed) Plugin.Log.LogInfo(P + $"{s.Name}: page {page} ({PageName(page)}), {why}; the button reads '{ReadLabel(s)}'");
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
                if (s.Button != null && s.Button.activeSelf != __0) s.Button.SetActive(__0);
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
                bool shown = any != null && any.KNPHKBGGHOC; // IsVisible
                if (s.Button.activeSelf != shown) s.Button.SetActive(shown);
                // Something may set the text after us (a localiser, the button's own state): keep ours.
                if (shown && Time.frameCount % 15 == 0 && s.Texts.Count > 0 && ReadLabel(s) != s.Label)
                {
                    if (!s.LabelResetLogged) Plugin.Log.LogInfo(P + $"{s.Name}: the button's text was changed to '{ReadLabel(s)}'; set back to '{s.Label}'");
                    s.LabelResetLogged = true;
                    SetLabel(s, s.Label);
                }
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
            if (from == 0) from = s.Page; // no current location known: the page being shown
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

        // The viewscreen's cycling picked a system after a CycleTargetEvent: show its page on every panel, so the
        // helm's own map follows too. The automatic pick when the viewscreen map opens (OnShowWarpPanel sets the
        // same cycle flags without an event) flips nothing: the map stays on the ship's page.
        private static void TrySelect_Postfix(WarpPanel __instance, int __0, bool __result)
        {
            if (!__result || panels.Count == 0) return;
            try
            {
                int page = PageOf(__0);
                if (Time.time - lastCycle >= 1f) return;
                foreach (var s in new List<PanelState>(panels.Values))
                {
                    if (s.Panel == null || s.Warping || s.Page == page) continue;
                    try { ShowPage(s, page, "a cycled warp target is there"); }
                    catch (Exception e) { Fail(s, "showing a cycled target's page", e); }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning(P + "cycling: " + e.Message); }
        }

        // ---- Failure ------------------------------------------------------------------------------------------------

        private static void Fail(PanelState s, string what, Exception e)
        {
            Plugin.Log.LogError(P + $"{s?.Name}: {what} failed; this panel goes back to the Trench map with every button"
                + (e != null ? ": " + e : ""));
            if (s != null) Restore(s.Panel);
        }

        // Back to the panel as the game made it: every location active, art and ship icon as they were, our button hidden.
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
                // Hidden, never destroyed: the console's HandAnimator and UIVisibilityControllers may hold references to
                // it, and a destroyed one makes them throw. Inactive objects are skipped by the former and safe for the
                // latter. Its presses do nothing once Failed is set.
                s.Failed = true;
                if (s.Button != null && s.Button.activeSelf) s.Button.SetActive(false);
            }
            catch (Exception e) { Plugin.Log.LogError(P + "hiding the page button: " + e.Message); }
        }
    }

    /// <summary>The warp map page button's press (VR poke or mouse click), on its HandInteraction's object.</summary>
    internal sealed class PageButtonClick : MonoBehaviour, IPointerClickHandler
    {
        internal Action Clicked;

        public void OnPointerClick(PointerEventData eventData)
        {
            try { Clicked?.Invoke(); }
            catch (Exception e) { Plugin.Log.LogError("Galaxy pages: page button press: " + e); }
        }
    }
}
