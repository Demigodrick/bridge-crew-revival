using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace StbcRevival
{
    /// <summary>
    /// Copies of the game's own console widgets for screens of ours: a console button (the helm's IMPULSE map-mode
    /// button: HandInteraction for VR pokes and the desktop mouse, a Toggle, its look), a slider (the helm's throttle,
    /// a SliderInteraction, which drags by tracked hand, desktop mouse and gamepad) and a text (the IMPULSE button's).
    /// A copy is made under an inactive parent and made safe before it wakes up (Neutralise): every UnityEvent of the
    /// original emptied, out of the original's toggle group, without the HandInteraction helper objects its Awake makes
    /// again. The warp map's page button (WarpPages) is made the same way.
    ///
    /// The hands only use the interactions in the local HandAnimators' console list (HandAnimator._consoleInteractions,
    /// DPAFLMHJMEC), which the game builds from the player's console and builds again on console changes: Register adds
    /// ours to it (and KeepRegistered adds them again after a rebuild), Unregister takes them out.
    /// </summary>
    internal static class ConsoleWidgets
    {
        private const string P = "Console widgets: ";
        internal const string Helper = "_ScaleAndAxesHelper"; // HandInteraction.Awake's own child object
        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // ---- Finding the originals ------------------------------------------------------------------------------------

        /// <summary>The scene's helm console (inactive ones too: a panel can be built before the helm is switched on).</summary>
        internal static ConsoleHelm FindHelm()
        {
            var found = UnityEngine.Object.FindObjectOfType<ConsoleHelm>();
            if (found != null) return found;
            foreach (var h in Resources.FindObjectsOfTypeAll<ConsoleHelm>())
                if (h != null && h.hideFlags == HideFlags.None && h.gameObject.scene.IsValid()) return h;
            return null;
        }

        /// <summary>
        /// A helm map-mode button's root: its HandInteraction's object, or the smallest object holding both it and its
        /// Toggle when that holds none of the other mode buttons.
        /// </summary>
        internal static GameObject ModeRoot(ConsoleHelm helm, string buttonField, string toggleField, params string[] others)
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

        /// <summary>The helm's IMPULSE map-mode button (the page button's and the science console's original).</summary>
        internal static GameObject ImpulseButton(ConsoleHelm helm) =>
            ModeRoot(helm, "_impulseButton", "_systemToggle", "_localButton", "_warpButton");

        internal static Transform Common(Transform a, Transform b)
        {
            for (var t = a; t != null; t = t.parent)
                if (b.IsChildOf(t)) return t;
            return null;
        }

        // ---- Copying a button face ------------------------------------------------------------------------------------

        /// <summary>A copy of a console button, centred in frame and sized to it (size in the frame's units).</summary>
        internal static GameObject CopyFace(GameObject source, RectTransform frame, Vector2 size, string name)
        {
            var button = UnityEngine.Object.Instantiate(source, frame, false) as GameObject;
            if (button == null) throw new InvalidOperationException("copying " + source.name + " gave nothing");
            button.name = name;
            if (button.transform is RectTransform face)
            {
                face.anchorMin = face.anchorMax = new Vector2(0.5f, 0.5f);
                face.sizeDelta = size;
                face.anchoredPosition = new Vector2((face.pivot.x - 0.5f) * size.x, (face.pivot.y - 0.5f) * size.y);
            }
            else button.transform.localPosition = Vector3.zero;
            button.transform.localRotation = Quaternion.identity;
            button.transform.localScale = Vector3.one;
            return button;
        }

        /// <summary>
        /// Where a copy's hands take their wrist poses from (HandAnimator asks each HandInteraction: from its grid when
        /// it has one, else from _poses[0], with no bounds check), and which hand may use it. Replace: set the grid to
        /// Grid; OnlyIfGrid: only on interactions that had a grid (the rest keep their own poses). SetSide: the hand side.
        /// </summary>
        internal sealed class HandFix
        {
            public bool Replace;
            public bool OnlyIfGrid;
            public UnityEngine.Object Grid;
            public bool SetSide;
            public object Side;
            public string Note = "";     // appended to each interaction's log line
        }

        /// <summary>
        /// Before a copy wakes up: no UnityEvent of the original, out of its toggle group, no copied HandInteraction
        /// helpers, the hands' grid and side as fix says. False if an interaction is left with no pose source.
        /// Logged as "{who}: {what} hand interaction ...".
        /// </summary>
        internal static bool Neutralise(GameObject copy, string who, string what, HandFix fix)
        {
            foreach (var t in copy.GetComponentsInChildren<Transform>(true))
                if (t != null && t != copy.transform && t.name.EndsWith(Helper, StringComparison.Ordinal))
                    UnityEngine.Object.DestroyImmediate(t.gameObject);

            int events = 0;
            foreach (var behaviour in copy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null) continue;
                for (var type = behaviour.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
                    foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (!typeof(UnityEngine.Events.UnityEventBase).IsAssignableFrom(f.FieldType) || f.FieldType.IsAbstract) continue;
                        try { f.SetValue(behaviour, Activator.CreateInstance(f.FieldType)); events++; }
                        catch (Exception e) { Plugin.Log.LogWarning($"{who}: clearing {type.Name}.{f.Name}: {e.Message}"); }
                    }
                if (behaviour is EventTrigger trigger && trigger.triggers != null) trigger.triggers.Clear();
            }
            foreach (var toggle in copy.GetComponentsInChildren<UnityEngine.UI.Toggle>(true)) toggle.group = null;

            var gridField = AccessTools.Field(typeof(HandInteraction), "_grid");
            var posesField = AccessTools.Field(typeof(HandInteraction), "_poses");
            var side = AccessTools.Field(typeof(HandInteraction), "_sideRestriction");
            if (gridField == null || posesField == null) { Plugin.Log.LogWarning($"{who}: HandInteraction._grid/_poses not found"); return false; }
            bool usable = true;
            foreach (var hand in copy.GetComponentsInChildren<HandInteraction>(true))
            {
                if (fix != null && fix.Replace && (!fix.OnlyIfGrid || gridField.GetValue(hand) as UnityEngine.Object != null))
                    gridField.SetValue(hand, fix.Grid != null ? fix.Grid : null);
                if (fix != null && fix.SetSide && side != null) side.SetValue(hand, fix.Side);
                var grid = gridField.GetValue(hand) as UnityEngine.Object;
                int poses = posesField.GetValue(hand) is Array a ? a.Length : 0;
                bool ok = grid != null || poses > 0;
                Plugin.Log.LogInfo($"{who}: {what} hand interaction {hand.name}: grid {(grid != null ? grid.name : "none")}, "
                    + $"{poses} pose(s){fix?.Note ?? ""}{(ok ? "" : ": no pose source")}");
                if (!ok) usable = false;
            }
            Plugin.Log.LogInfo($"{who}: {what}: {events} copied event(s) emptied");
            return usable;
        }

        // ---- Presses --------------------------------------------------------------------------------------------------

        /// <summary>A button copy's presses: its Toggles (any change is a press, then set off again), Buttons and a click
        /// handler on each HandInteraction's object.</summary>
        internal sealed class Presses
        {
            public readonly List<UnityEngine.UI.Toggle> Toggles = new List<UnityEngine.UI.Toggle>();
            public int Handlers;
            public bool Resetting;   // our own isOn = false, not a press

            public void ResetToggles()
            {
                Resetting = true;
                try { foreach (var t in Toggles) if (t != null && t.isOn) t.isOn = false; }
                finally { Resetting = false; }
            }
        }

        /// <summary>Wires a button copy's presses to pressed("toggle" | "button" | "press"). clickLog names it in errors.</summary>
        internal static Presses WirePresses(GameObject button, Action<string> pressed, string clickLog)
        {
            var p = new Presses();
            foreach (var toggle in button.GetComponentsInChildren<UnityEngine.UI.Toggle>(true))
            {
                var t = toggle;
                p.Toggles.Add(t);
                t.onValueChanged.AddListener(on =>
                {
                    if (p.Resetting) return;
                    pressed("toggle");
                    p.ResetToggles();
                });
            }
            foreach (var b in button.GetComponentsInChildren<UnityEngine.UI.Button>(true))
                b.onClick.AddListener(() => pressed("button"));
            foreach (var hand in button.GetComponentsInChildren<HandInteraction>(true))
            {
                var click = hand.gameObject.GetComponent<ConsoleClick>() ?? hand.gameObject.AddComponent<ConsoleClick>();
                click.Clicked = () => pressed("press");
                click.Log = clickLog;
                p.Handlers++;
            }
            if (p.Handlers == 0)
            {
                var click = button.AddComponent<ConsoleClick>();
                click.Clicked = () => pressed("press");
                click.Log = clickLog;
            }
            return p;
        }

        // ---- The science console's widgets ----------------------------------------------------------------------------

        /// <summary>A button of ours: a copy of the IMPULSE button filling its parent (a slot in a screen of ours).</summary>
        internal static ConsoleButton CloneButton(Transform parent, string label, Action onPress, UnityEngine.Object grid = null)
        {
            var helm = FindHelm();
            var source = helm != null ? ImpulseButton(helm) : null;
            if (source == null) throw new InvalidOperationException("the helm's IMPULSE button wasn't found");
            var frame = Frame(parent, "Science Button");
            var slot = (RectTransform)parent;
            // Native size: one face unit is as big in the world as on the helm; the face is stretched to the slot.
            float s = UnitRatio(source.transform, slot);
            var size = new Vector2(slot.rect.width / s, slot.rect.height / s);
            var face = CopyFace(source, frame, size, "Science Button Face");
            face.transform.localScale = new Vector3(s, s, s);
            if (!face.activeSelf) face.SetActive(true);
            var button = frame.gameObject.AddComponent<ConsoleButton>();
            if (!Neutralise(face, P.TrimEnd(' ', ':'), "button", ScienceFix(grid)))
                throw new InvalidOperationException("the button copy has no hand poses");
            CutOutside(face);
            Usable(face);
            button.Init(face, label, onPress);
            frame.gameObject.SetActive(true);
            return button;
        }

        /// <summary>
        /// A slider of ours: a copy of the helm's throttle handle, moving left to right along its parent (a slot), value
        /// 0..1, staying where it's left (no recentring). onChange gets every value the hand or mouse gives it.
        /// </summary>
        internal static SliderInteraction CloneSlider(Transform parent, Action<float> onChange, UnityEngine.Object grid = null)
        {
            var helm = FindHelm();
            var source = helm != null ? AccessTools.Field(typeof(ConsoleHelm), "throttleSlider")?.GetValue(helm) as SliderInteraction : null;
            if (source == null) throw new InvalidOperationException("the helm's throttle slider wasn't found");
            var track = (RectTransform)parent;
            // Under the (inactive) screen: it wakes up only once it's made safe.
            var copy = UnityEngine.Object.Instantiate(source.gameObject, track, false) as GameObject;
            if (copy == null) throw new InvalidOperationException("copying the throttle gave nothing");
            copy.SetActive(false);
            copy.name = "Science Slider";
            var slider = copy.GetComponent<SliderInteraction>();
            var originalParent = source.transform.parent;

            // Its travel in its own parent's space, turned to run along our track's x.
            Vector3 min = source.MinLocalPosition, max = source.MaxLocalPosition;
            Vector3 along = max - min;
            Quaternion turn = along.sqrMagnitude > 1e-12f ? Quaternion.FromToRotation(along.normalized, Vector3.right) : Quaternion.identity;
            // As big as on the helm, but no taller than the track (the hands' poses follow its scale).
            float s = originalParent != null ? UnitRatio(originalParent, track) : 1f;
            HandleHalves(source, along, out float alongHalf, out float acrossHalf);
            if (acrossHalf * 2f * s > track.rect.height && acrossHalf > 0f) s = track.rect.height / (acrossHalf * 2f);
            copy.transform.localRotation = turn * source.transform.localRotation;
            copy.transform.localScale = source.transform.localScale * s;
            float half = Mathf.Max(0f, track.rect.width / 2f - alongHalf * s);
            slider.MinLocalPosition = new Vector3(-half, 0f, 0f);
            slider.MaxLocalPosition = new Vector3(half, 0f, 0f);
            slider.RecenterSpeed = 0f;
            slider.SliderMinValue = 0f;
            var direction = AccessTools.Field(typeof(SliderInteraction), "Direction");
            if (direction != null) try { direction.SetValue(slider, Enum.Parse(direction.FieldType, "Horizontal")); } catch { }
            // At 0, where it stands (it wakes up reading its value from where it is).
            AccessTools.Field(typeof(SliderInteraction), "BDCPELPJBAL")?.SetValue(slider, 0f); // _value
            if (!Neutralise(copy, P.TrimEnd(' ', ':'), "slider", ScienceFix(grid)))
                throw new InvalidOperationException("the slider copy has no hand poses");
            CutOutside(copy); // the throttle's fill image, if it isn't part of the handle, is the helm's
            Usable(copy);
            slider.OnValueChanged.AddListener(v =>
            {
                try { onChange?.Invoke(v); }
                catch (Exception e) { Plugin.Log.LogError(P + "slider: " + e); }
            });
            copy.transform.localPosition = slider.MinLocalPosition;
            copy.SetActive(true);
            return slider;
        }

        /// <summary>A text of ours: a copy of the IMPULSE button's, filling its parent, at the given size (parent's units).</summary>
        internal static TMPro.TMP_Text CloneLabel(Transform parent, float fontSize)
        {
            var helm = FindHelm();
            var button = helm != null ? ImpulseButton(helm) : null;
            var source = button != null ? button.GetComponentInChildren<TMPro.TMP_Text>(true) : null;
            if (source == null) throw new InvalidOperationException("the IMPULSE button's text wasn't found");
            var copy = UnityEngine.Object.Instantiate(source.gameObject, parent, false) as GameObject;
            if (copy == null) throw new InvalidOperationException("copying the IMPULSE button's text gave nothing");
            copy.SetActive(false);
            copy.name = "Science Text";
            var r = (RectTransform)copy.transform;
            Stretch(r);
            r.localRotation = Quaternion.identity;
            r.localScale = Vector3.one;
            CutOutside(copy);
            var tmp = copy.GetComponent<TMPro.TMP_Text>();
            Unlocalise(tmp);
            tmp.BMAIEFHKPMA = false;          // enableAutoSizing
            tmp.ACEGMMMIHCN = fontSize;       // fontSize
            tmp.GADKGKMKHCE = false;          // enableWordWrapping
            tmp.OIKALDNFGKE = "";             // text
            copy.SetActive(true);
            return tmp;
        }

        /// <summary>
        /// A hand pose grid for a screen of ours: the IMPULSE button's grid (its poses) laid over our screen, so a hand
        /// reaching for one of our widgets takes the pose the helm's widgets get, sized to our screen (the helm's own grid
        /// holds at most 64 controls and places poses by the helm's corners). Awake at once, under the (active) helm; the
        /// caller destroys it with the screen (its GameObject). Null if the button has no grid or the helm isn't active
        /// (the copies then keep the helm's grid, or their own poses).
        /// </summary>
        internal static UnityEngine.Object MakeGrid(RectTransform screen)
        {
            var helm = FindHelm();
            var button = helm != null ? ImpulseButton(helm) : null;
            var hand = button != null ? button.GetComponentInChildren<HandInteraction>(true) : null;
            var gridField = AccessTools.Field(typeof(HandInteraction), "_grid");
            var source = hand != null ? gridField?.GetValue(hand) as Component : null;
            if (source == null || !helm.gameObject.activeInHierarchy) return null;
            var type = source.GetType();
            // The grid works in its own x (right) and z (up the screen): turned so z runs up ours, y out of it.
            // It's kept under the helm, not in the screen: its Awake needs a console above it (it lists the console's
            // controls, and throws without one), and it must be awake before the screen's controls wake and join it (a
            // control that joins a grid not yet awake isn't a member, and the grid then reads its poses at index -1).
            var holder = new GameObject("Science Hand Grid");
            holder.SetActive(false);
            holder.layer = screen.gameObject.layer;
            holder.transform.SetParent(helm.transform, false);
            holder.transform.position = screen.position;
            holder.transform.rotation = screen.rotation * Quaternion.Euler(-90f, 0f, 0f);
            var r = screen.rect;
            var topLeft = new GameObject("Top Left").transform;
            topLeft.SetParent(screen, false);
            topLeft.localPosition = new Vector3(r.xMin, r.yMax, 0f);
            var bottomRight = new GameObject("Bottom Right").transform;
            bottomRight.SetParent(screen, false);
            bottomRight.localPosition = new Vector3(r.xMax, r.yMin, 0f);
            var grid = holder.AddComponent(type);
            // Its serialized settings (poses per grid position, pose count, trigger spots, static), then our corners.
            for (var t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                foreach (var f in t.GetFields(Inst | BindingFlags.DeclaredOnly))
                    if (!f.IsStatic && !f.IsNotSerialized && (f.IsPublic || f.IsDefined(typeof(SerializeField), false)))
                        f.SetValue(grid, f.GetValue(source));
            AccessTools.Field(type, "_topLeftTransform")?.SetValue(grid, topLeft);
            AccessTools.Field(type, "_bottomRightTransform")?.SetValue(grid, bottomRight);
            holder.SetActive(true);
            Plugin.Log.LogInfo(P + $"hand grid for {screen.name} copied from {source.name}");
            return grid;
        }

        private static HandFix ScienceFix(UnityEngine.Object grid) => new HandFix
        {
            // Both hands, wherever the screen stands; the helm's grid replaced by the screen's own where there is one
            // (without one, the helm's grid is kept: the helm's poses, placed by the helm's corners).
            Replace = grid != null,
            OnlyIfGrid = true,
            Grid = grid,
            SetSide = true,
            Side = SideEither,
            Note = grid != null ? ", the screen's grid" : "",
        };

        private static object SideEither
        {
            get
            {
                var f = AccessTools.Field(typeof(HandInteraction), "_sideRestriction");
                try { return f != null ? Enum.Parse(f.FieldType, "Either") : null; } catch { return null; }
            }
        }

        // Whatever state the helm's control was in when copied (not interactable in some modes), ours takes hands.
        private static void Usable(GameObject copy)
        {
            foreach (var hand in copy.GetComponentsInChildren<HandInteraction>(true)) hand.Interactable = true;
        }

        /// <summary>A copy's references to scene objects outside it (the helm's fill image, its listener's associates and
        /// groups, ...) are cleared, so ours never drives the helm's.</summary>
        internal static void CutOutside(GameObject copy)
        {
            int cut = 0;
            var grid = AccessTools.Field(typeof(HandInteraction), "_grid");
            foreach (var behaviour in copy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null) continue;
                for (var type = behaviour.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
                    foreach (var f in type.GetFields(Inst | BindingFlags.DeclaredOnly))
                    {
                        if (f.IsStatic || f == grid) continue;
                        try
                        {
                            if (typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType))
                            {
                                if (Outside(f.GetValue(behaviour) as UnityEngine.Object, copy)) { f.SetValue(behaviour, null); cut++; }
                            }
                            else if (f.FieldType.IsArray && typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType.GetElementType())
                                     && f.GetValue(behaviour) is Array a && a.Length > 0)
                            {
                                var keep = new List<object>();
                                foreach (var o in a) if (!Outside(o as UnityEngine.Object, copy)) keep.Add(o);
                                if (keep.Count == a.Length) continue;
                                var b = Array.CreateInstance(f.FieldType.GetElementType(), keep.Count);
                                for (int i = 0; i < keep.Count; i++) b.SetValue(keep[i], i);
                                f.SetValue(behaviour, b);
                                cut += a.Length - keep.Count;
                            }
                        }
                        catch (Exception) { }
                    }
            }
            if (cut > 0) Plugin.Log.LogInfo(P + $"{copy.name}: {cut} reference(s) to the original's surroundings cleared");
        }

        // A scene object that isn't part of the copy.
        private static bool Outside(UnityEngine.Object o, GameObject copy)
        {
            if (o == null) return false;
            var go = o as GameObject ?? (o as Component)?.gameObject;
            if (go == null || !go.scene.IsValid()) return false; // an asset (sprite, material, font...)
            return !go.transform.IsChildOf(copy.transform);
        }

        // TMP keeps the key it was localised with and sets it again when the language changes: forget it.
        private static readonly FieldInfo LocalizedKey = FindField(typeof(TMPro.TMP_Text), "EEHDAODMMFJ");

        internal static void Unlocalise(TMPro.TMP_Text tmp)
        {
            try { if (tmp != null) LocalizedKey?.SetValue(tmp, null); } catch { }
        }

        private static FieldInfo FindField(Type t, string name)
        {
            for (; t != null; t = t.BaseType)
            {
                var f = t.GetField(name, Inst | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        internal static void Stretch(RectTransform r)
        {
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.pivot = new Vector2(0.5f, 0.5f);
            r.offsetMin = r.offsetMax = Vector2.zero;
        }

        // An empty frame filling the parent.
        private static RectTransform Frame(Transform parent, string name)
        {
            var frame = new GameObject(name, typeof(RectTransform));
            frame.SetActive(false);
            frame.layer = parent.gameObject.layer;
            var r = (RectTransform)frame.transform;
            r.SetParent(parent, false);
            Stretch(r);
            r.localRotation = Quaternion.identity;
            r.localScale = Vector3.one;
            return r;
        }

        // How many of `to`'s units one of `from`'s is, in the world.
        private static float UnitRatio(Transform from, Transform to)
        {
            float a = Mathf.Abs(from.lossyScale.x), b = Mathf.Abs(to.lossyScale.x);
            return a > 0f && b > 0f ? a / b : 1f;
        }

        // Half the handle's extent along its travel and across it (its own rectangle, in its parent's units), so it
        // stays on the track and within its row.
        private static void HandleHalves(SliderInteraction source, Vector3 along, out float alongHalf, out float acrossHalf)
        {
            alongHalf = acrossHalf = 0f;
            if (!(source.transform is RectTransform r) || along.sqrMagnitude < 1e-12f) return;
            var size = Vector3.Scale(r.rect.size, source.transform.localScale);
            var dir = Quaternion.Inverse(source.transform.localRotation) * along.normalized; // travel in the handle's own axes
            alongHalf = Mathf.Abs(dir.x) * size.x / 2f + Mathf.Abs(dir.y) * size.y / 2f;
            acrossHalf = Mathf.Abs(dir.y) * size.x / 2f + Mathf.Abs(dir.x) * size.y / 2f;
        }

        // ---- The hands ------------------------------------------------------------------------------------------------

        private static readonly Type AnimatorType = AccessTools.TypeByName("HandAnimator");
        private static readonly Type ManagerType = AccessTools.TypeByName("HandAnimatorManager");
        private static readonly MethodInfo ManagerInstance = AccessTools.Method(ManagerType, "get_Instance");
        private static readonly FieldInfo ManagerAnimators = AccessTools.Field(ManagerType, "AFBJOJJNCEA");  // animators -> consoles
        private static readonly FieldInfo ConsoleList = AccessTools.Field(AnimatorType, "DPAFLMHJMEC");      // _consoleInteractions
        private static readonly FieldInfo ListChanged = AccessTools.Field(AnimatorType, "OLMKBPANPIO");      // _interactionsChanged
        private static readonly FieldInfo PawnPlayer = AccessTools.Field(typeof(PawnComponent), "HAMOMEFCFNB"); // _playerInfo
        private static readonly PropertyInfo IsLocal = AccessTools.Property(typeof(PlayerInfo), "AOLEJMABHPE");
        private static readonly HashSet<HandInteraction> registered = new HashSet<HandInteraction>();
        private static readonly List<HandInteraction> scratch = new List<HandInteraction>();
        private static bool warnedFull, warnedMissing;

        /// <summary>The local player's hands can use these (VR pokes and drags, the desktop mouse).</summary>
        internal static void Register(IEnumerable<HandInteraction> list)
        {
            if (list == null) return;
            foreach (var h in list) if (h != null) registered.Add(h);
            KeepRegistered();
        }

        /// <summary>The local player's hands no longer use these.</summary>
        internal static void Unregister(IEnumerable<HandInteraction> list)
        {
            if (list == null) return;
            scratch.Clear();
            foreach (var h in list) if (h != null && registered.Remove(h)) scratch.Add(h);
            if (scratch.Count == 0) return;
            foreach (var animator in LocalAnimators())
            {
                if (!(ConsoleList.GetValue(animator) is List<HandInteraction> hands)) continue;
                bool changed = false;
                foreach (var h in scratch) changed |= hands.Remove(h);
                if (changed) ListChanged?.SetValue(animator, true);
            }
        }

        /// <summary>
        /// Puts the registered interactions back into the local hands' lists the game has built again (a console change,
        /// NotifyAnimatorsForConsole). Cheap; call a few times a second while any are registered.
        /// </summary>
        internal static void KeepRegistered()
        {
            if (registered.Count == 0) return;
            registered.RemoveWhere(h => h == null);
            foreach (var animator in LocalAnimators())
            {
                if (!(ConsoleList.GetValue(animator) is List<HandInteraction> hands)) continue;
                int missing = 0;
                foreach (var h in registered) if (!hands.Contains(h)) missing++;
                if (missing == 0) continue;
                // The animator keeps a fixed array of this many (and doesn't check).
                if (hands.Count + missing > HandInteraction.MaxCountPerConsole)
                {
                    if (!warnedFull) Plugin.Log.LogWarning(P + $"the hands' list is full ({hands.Count} of {HandInteraction.MaxCountPerConsole}); {missing} control(s) not added");
                    warnedFull = true;
                    continue;
                }
                foreach (var h in registered) if (!hands.Contains(h)) hands.Add(h);
                ListChanged?.SetValue(animator, true);
            }
        }

        // The local player's HandAnimators (HandAnimatorManager's animators dictionary, keyed by animator).
        private static IEnumerable<object> LocalAnimators()
        {
            if (ConsoleList == null || PawnPlayer == null)
            {
                if (!warnedMissing) Plugin.Log.LogWarning(P + "HandAnimator's console list or player wasn't found; the hands can't use our controls");
                warnedMissing = true;
                yield break;
            }
            var manager = ManagerInstance?.Invoke(null, null);
            if (manager == null || !(ManagerAnimators?.GetValue(manager) is IDictionary animators)) yield break;
            var keys = new List<object>();
            foreach (var k in animators.Keys) keys.Add(k);
            foreach (var animator in keys)
            {
                if (!(animator is Component c) || c == null) continue;
                var player = PawnPlayer.GetValue(animator);
                if (player != null && IsLocal?.GetValue(player, null) is bool local && local) yield return animator;
            }
        }
    }

    /// <summary>A button of ours (ConsoleWidgets.CloneButton): its label and press, counted once within 0.3 s.</summary>
    internal sealed class ConsoleButton : MonoBehaviour
    {
        private readonly List<Component> texts = new List<Component>();
        private ConsoleWidgets.Presses presses;
        private Action onPress;
        private float lastPress = -10f;
        private string label;
        private bool reset;

        internal GameObject Face { get; private set; }

        internal void Init(GameObject face, string text, Action press)
        {
            Face = face;
            onPress = press;
            presses = ConsoleWidgets.WirePresses(face, Pressed, "Science console: button press");
            foreach (var c in face.GetComponentsInChildren<Component>(true))
                if (c is TMPro.TMP_Text || c is UnityEngine.UI.Text) texts.Add(c);
            foreach (var c in texts) if (c is TMPro.TMP_Text tmp) ConsoleWidgets.Unlocalise(tmp);
            SetLabel(text);
        }

        internal void SetLabel(string text)
        {
            if (text == label) return;
            label = text;
            foreach (var c in texts)
            {
                if (c is TMPro.TMP_Text tmp) tmp.OIKALDNFGKE = text; // TMP_Text.text
                else if (c is UnityEngine.UI.Text ui) ui.text = text;
            }
        }

        internal IEnumerable<Component> Texts => texts;

        private void Start()
        {
            // Awake now: the copy's Toggles set off (not a press).
            if (reset) return;
            reset = true;
            presses?.ResetToggles();
        }

        private void Pressed(string how)
        {
            if (Time.unscaledTime - lastPress < 0.3f) return;
            lastPress = Time.unscaledTime;
            try { onPress?.Invoke(); }
            catch (Exception e) { Plugin.Log.LogError("Science console: button press: " + e); }
        }
    }

    /// <summary>A copied console button's press (VR poke or mouse click), on its HandInteraction's object.</summary>
    internal sealed class ConsoleClick : MonoBehaviour, IPointerClickHandler
    {
        internal Action Clicked;
        internal string Log = "Console button press";

        public void OnPointerClick(PointerEventData eventData)
        {
            try { Clicked?.Invoke(); }
            catch (Exception e) { Plugin.Log.LogError(Log + ": " + e); }
        }
    }
}
