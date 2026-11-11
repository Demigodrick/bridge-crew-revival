using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using BepInEx;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StbcRevival.Walking
{
    /// <summary>
    /// [Bridge Walk] Prototype bridge editor, 2D (monitor) mode: while standing on a bridge, EditorKey (F8) turns it on.
    /// Aim at something with the dot in the middle of the screen and click to pick it (click again for what's behind
    /// it), U for the group it's in; hold the right mouse button and move the mouse to slide it across the floor,
    /// the wheel to turn it (with the right button held: up and down; Shift: finer), H to hide or show it, Backspace
    /// to put it back. Stations and consoles stay put. Changes are saved per bridge scene to
    /// BepInEx/config/stbc.revival.bridges/&lt;scene&gt;.txt and applied whenever that bridge loads, editor on or not.
    ///
    /// Only for you: crewmates see their own bridge (their own file, if they have one). Most bridge pieces are
    /// statically batched, drawn as part of one combined mesh, so they can't move: those are hidden, and a copy of
    /// their shape read from the bridge's file (as for the test corridor's door) moves instead. Colliders move with
    /// the piece; the walk mesh still has a batched piece where it was until the bridge is reloaded.
    /// </summary>
    internal static class BridgeEditor
    {
        internal const string CopyName = "RevivalEditCopy";
        private const string FolderName = "stbc.revival.bridges";

        /// <summary>One changed piece: where it is in the bridge (path, and which of the same path), and what was done.</summary>
        private sealed class Edit
        {
            public string Path;
            public int Index;
            public Vector3 Move;   // in its parent's space
            public float Turn;     // degrees about its parent's up, around its middle
            public bool Hidden;

            // In the scene now (null T: not found in this bridge, kept for the file).
            public Transform T;
            public Vector3 BaseLocalPos, PivotLocal;
            public Quaternion BaseLocalRot;
            public readonly List<Renderer> Normal = new List<Renderer>();   // its own renderers, on at the start
            public readonly List<Renderer> Batched = new List<Renderer>();  // ...of which statically batched
            public readonly List<Collider> Colliders = new List<Collider>();
            public GameObject Copy;
            public readonly List<Mesh> CopyMeshes = new List<Mesh>();
            public string CopyProblem;

            public bool Moved => Move.sqrMagnitude > 1e-8f || Mathf.Abs(Turn) > 1e-3f;
            public bool Changed => Moved || Hidden;
        }

        private static readonly List<Edit> edits = new List<Edit>();
        private static Scene bridge;
        private static int boundRoot; // the bridge scene's first root object: changes when the bridge reloads
        private static bool open;
        private static Edit selected;
        private static Transform pickedNotEdited; // picked but nothing done to it yet
        private static readonly List<Renderer> lastHits = new List<Renderer>();
        private static int lastHitAt;
        private static Vector3 lastClickEye, lastClickDir;
        private static string status;
        private static float statusUntil;
        private static float saveAt = -1f;
        private static Material lineMat;
        private static Camera.CameraCallback postRender;

        internal static bool Open => open;

        // ------------------------------------------------------------------------------------------- every frame
        internal static void Tick(bool onBridge)
        {
            if (!onBridge || !FindBridge(out var scene))
            {
                if (open) Close("left the bridge");
                if (edits.Count > 0 || boundRoot != 0) Forget();
                return;
            }
            var roots = scene.GetRootGameObjects();
            int rootId = roots.Length > 0 ? roots[0].GetInstanceID() : 0;
            if (rootId == 0) return;
            if (rootId != boundRoot)
            {
                // Reached the bridge, or it was reloaded under us: its saved layout goes on.
                if (open) Close("the bridge was reloaded");
                Forget();
                bridge = scene;
                boundRoot = rootId;
                Load();
            }
            foreach (var e in edits) if (e.T != null && e.Moved && e.Batched.Count > 0 && e.Copy == null && e.CopyProblem == null) Refresh(e);

            if (!TextPrompt.IsOpen && Input.GetKeyDown(Plugin.BridgeEditorKey.Value))
            {
                if (open) Close("key");
                else
                {
                    if (!Walker.Standing) Walker.StandUp();
                    if (Walker.Standing) OpenEditor();
                }
            }
            if (open && !Walker.Standing) Close("sat down");
            if (open && !TextPrompt.IsOpen) Controls();
            if (saveAt > 0f && Time.unscaledTime >= saveAt) Save();
        }

        private static bool FindBridge(out Scene found)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isLoaded && s.name.StartsWith("Bridge_scene", StringComparison.OrdinalIgnoreCase)) { found = s; return true; }
            }
            found = default(Scene);
            return false;
        }

        private static void OpenEditor()
        {
            open = true;
            selected = null;
            pickedNotEdited = null;
            BatchedPieces.Ready(bridge.buildIndex);
            if (postRender == null)
            {
                postRender = DrawSelection;
                Camera.onPostRender += postRender;
            }
            Say("Bridge editor on");
            Plugin.Log.LogInfo($"Bridge editor: on, in {bridge.name} ({edits.Count} saved change(s))");
        }

        private static void Close(string why)
        {
            if (!open) return;
            open = false;
            selected = null;
            pickedNotEdited = null;
            Walker.HoldLook = false;
            Save();
            WalkMesh.Clear(); // read again with the pieces where they are now, next time you stand up
            Plugin.Log.LogInfo("Bridge editor: off (" + why + ")");
        }

        // ------------------------------------------------------------------------------------------- controls
        private static void Controls()
        {
            bool fine = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (Input.GetMouseButtonDown(0)) Pick();
            if (Input.GetKeyDown(KeyCode.Tab)) Cycle();
            if (Input.GetKeyDown(KeyCode.U)) Group();

            var e = Current(false);
            bool rmb = Input.GetMouseButton(1);
            Walker.HoldLook = rmb && (selected != null || pickedNotEdited != null);
            float wheel = Input.mouseScrollDelta.y;
            if (rmb && (selected != null || pickedNotEdited != null))
            {
                float mx = 0, my = 0;
                try { mx = Input.GetAxisRaw("Mouse X"); my = Input.GetAxisRaw("Mouse Y"); } catch { }
                if (Mathf.Abs(mx) + Mathf.Abs(my) > 0f || Mathf.Abs(wheel) > 0f)
                {
                    e = Current(true);
                    if (e == null) return;
                    var eye = Walker.Eye;
                    Vector3 up = e.T.parent != null ? e.T.parent.up : Vector3.up;
                    Vector3 right = Vector3.ProjectOnPlane(eye.right, up).normalized;
                    Vector3 fwd = Vector3.ProjectOnPlane(eye.forward, up).normalized;
                    float k = fine ? 0.004f : 0.02f;
                    Vector3 world = (right * mx + fwd * my) * k + up * wheel * (fine ? 0.01f : 0.05f);
                    e.Move += e.T.parent != null ? e.T.parent.InverseTransformVector(world) : world;
                    Changed(e);
                }
            }
            else if (Mathf.Abs(wheel) > 0f && (selected != null || pickedNotEdited != null))
            {
                e = Current(true);
                if (e == null) return;
                e.Turn = Mathf.Repeat(e.Turn + Mathf.Sign(wheel) * (fine ? 1f : 5f) + 180f, 360f) - 180f;
                Changed(e);
            }
            if (Input.GetKeyDown(KeyCode.H) && (selected != null || pickedNotEdited != null))
            {
                e = Current(true);
                if (e == null) return;
                e.Hidden = !e.Hidden;
                Changed(e);
                Say(e.Hidden ? "Hidden" : "Shown");
            }
            if (Input.GetKeyDown(KeyCode.Backspace) && selected != null)
            {
                var t = selected.T;
                Reset(selected);
                edits.Remove(selected);
                selected = null;
                pickedNotEdited = t;
                saveAt = Time.unscaledTime + 0.5f;
                Say("Put back as it was");
            }
        }

        /// <summary>The edit for what's picked; with make, one is started for something picked but not changed yet.</summary>
        private static Edit Current(bool make)
        {
            if (selected != null) return selected;
            if (!make || pickedNotEdited == null) return null;
            var e = new Edit { Path = Bridge.Path(pickedNotEdited), Index = IndexOf(pickedNotEdited) };
            if (!Bind(e, pickedNotEdited)) return null;
            edits.Add(e);
            selected = e;
            pickedNotEdited = null;
            return e;
        }

        private static void Changed(Edit e)
        {
            Refresh(e);
            saveAt = Time.unscaledTime + 1f;
        }

        // ------------------------------------------------------------------------------------------- picking
        private static void Pick()
        {
            var eye = Walker.Eye;
            if (eye == null) return;
            // Clicking again from the same place: the next thing along the line.
            if (lastHits.Count > 1 && (eye.position - lastClickEye).sqrMagnitude < 0.0025f && Vector3.Angle(eye.forward, lastClickDir) < 1f)
            {
                Cycle();
                return;
            }
            lastClickEye = eye.position;
            lastClickDir = eye.forward;
            lastHits.Clear();
            var ray = new Ray(eye.position, eye.forward);
            var hits = new List<KeyValuePair<float, Renderer>>();
            foreach (var top in bridge.GetRootGameObjects())
                foreach (var r in top.GetComponentsInChildren<MeshRenderer>(false))
                {
                    if (!r.enabled) continue;
                    Bounds b = r.bounds;
                    if (b.size.magnitude > 60f || b.Contains(ray.origin)) continue; // the room around you, the sky
                    if (!b.IntersectRay(ray, out float d) || d > 30f) continue;
                    string path = Bridge.Path(r.transform);
                    if (path.IndexOf("/FX/", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf("Canvas", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf(TestCorridor.RootName, StringComparison.Ordinal) >= 0) continue;
                    hits.Add(new KeyValuePair<float, Renderer>(d, r));
                }
            // Hidden pieces aren't drawn, but can still be picked to show them again.
            foreach (var e in edits)
                if (e.Hidden && e.T != null)
                    foreach (var r in e.Normal)
                        if (r != null && !r.bounds.Contains(ray.origin) && r.bounds.IntersectRay(ray, out float d) && d <= 30f)
                            hits.Add(new KeyValuePair<float, Renderer>(d, r));
            hits.Sort((a, b) => a.Key.CompareTo(b.Key));
            foreach (var h in hits) lastHits.Add(h.Value);
            lastHitAt = 0;
            if (lastHits.Count == 0) { Select(null); Say("Nothing there"); return; }
            Select(lastHits[0].transform);
        }

        private static void Cycle()
        {
            if (lastHits.Count == 0) return;
            lastHitAt = (lastHitAt + 1) % lastHits.Count;
            var r = lastHits[lastHitAt];
            if (r == null) { lastHits.Clear(); return; }
            Select(r.transform);
        }

        private static void Group()
        {
            var t = selected != null ? selected.T : pickedNotEdited;
            if (t == null || t.parent == null) { Say("No group above this"); return; }
            var p = t.parent;
            if (p.parent == null || BigOrRoom(p)) { Say("That's the whole bridge"); return; }
            if (Problem(p, out var why)) { Say(why); return; }
            foreach (var e in edits)
                if (e.T != null && e.T != p && e.T.IsChildOf(p)) { Say($"Put {e.T.name} back first (Backspace): it's in that group"); return; }
            if (selected != null && !selected.Changed) { Reset(selected); edits.Remove(selected); }
            selected = Find(p);
            pickedNotEdited = selected == null ? p : null;
            Say("Group: " + p.name);
        }

        private static void Select(Transform t)
        {
            selected = null;
            pickedNotEdited = null;
            if (t == null) return;
            // A moved copy of a batched piece: the piece it's a copy of.
            for (var a = t; a != null; a = a.parent) if (a.name == CopyName && a.parent != null) { t = a.parent; break; }
            // Inside something already changed: that.
            foreach (var e in edits) if (e.T != null && (t == e.T || t.IsChildOf(e.T))) { selected = e; Say("Picked: " + e.T.name); return; }
            if (Problem(t, out var why)) { Say(why); return; }
            pickedNotEdited = t;
            Say("Picked: " + t.name);
        }

        private static bool Problem(Transform t, out string why)
        {
            why = null;
            string path = Bridge.Path(t);
            if (path.StartsWith("/Players/")) why = "That's a crewmate";
            else if (t.GetComponentInParent<ConsoleBase>() != null || t.GetComponentInParent<NovaStation>() != null
                     || t.GetComponentInChildren<ConsoleBase>(true) != null || t.GetComponentInChildren<NovaStation>(true) != null)
                why = $"{t.name} is part of a station: stations stay put for now";
            return why != null;
        }

        private static bool BigOrRoom(Transform t)
        {
            var rs = t.GetComponentsInChildren<Renderer>(true);
            if (rs.Length > 400) return true;
            foreach (var r in rs) if (r.bounds.size.magnitude > 60f) return true;
            return false;
        }

        private static Edit Find(Transform t)
        {
            foreach (var e in edits) if (e.T == t) return e;
            return null;
        }

        // ------------------------------------------------------------------------------------------- applying
        /// <summary>Ties an edit to the scene object it's for, noting how it was.</summary>
        private static bool Bind(Edit e, Transform t)
        {
            e.T = t;
            e.BaseLocalPos = t.localPosition;
            e.BaseLocalRot = t.localRotation;
            Bounds? all = null;
            foreach (var r in t.GetComponentsInChildren<Renderer>(false))
            {
                if (!r.enabled || r is ParticleSystemRenderer) continue;
                e.Normal.Add(r);
                if (r.isPartOfStaticBatch && r is MeshRenderer) e.Batched.Add(r);
                if (all == null) all = r.bounds; else { var b = all.Value; b.Encapsulate(r.bounds); all = b; }
            }
            foreach (var c in t.GetComponentsInChildren<Collider>(false)) if (c.enabled) e.Colliders.Add(c);
            Vector3 middle = all?.center ?? t.position;
            e.PivotLocal = t.parent != null ? t.parent.InverseTransformPoint(middle) : middle;
            if (e.Batched.Count > 0) BatchedPieces.Ready(bridge.buildIndex);
            return true;
        }

        /// <summary>Puts an edit's piece where and how the edit says.</summary>
        private static void Refresh(Edit e)
        {
            if (e.T == null) return;
            if (e.Moved && e.Batched.Count > 0 && e.Copy == null && e.CopyProblem == null) MakeCopy(e);
            Quaternion turn = Quaternion.AngleAxis(e.Turn, Vector3.up);
            e.T.localPosition = e.PivotLocal + turn * (e.BaseLocalPos - e.PivotLocal) + e.Move;
            e.T.localRotation = turn * e.BaseLocalRot;
            bool copied = e.Copy != null;
            foreach (var r in e.Normal)
            {
                if (r == null) continue;
                bool batched = e.Batched.Contains(r);
                // A batched piece is drawn where it was baked whatever its transform does: hidden while its copy stands in.
                r.enabled = !e.Hidden && !(batched && copied);
            }
            if (copied) foreach (var r in e.Copy.GetComponentsInChildren<Renderer>(true)) r.enabled = !e.Hidden;
            foreach (var c in e.Colliders) if (c != null) c.enabled = !e.Hidden;
        }

        private static void MakeCopy(Edit e)
        {
            if (!BatchedPieces.Ready(bridge.buildIndex)) return; // still reading the bridge's file: tried again next frame
            if (BatchedPieces.Error != null) { e.CopyProblem = "the bridge's file couldn't be read: " + BatchedPieces.Error; Report(e); return; }
            var plan = new List<KeyValuePair<Renderer, BatchedPieces.Piece>>();
            foreach (var r in e.Batched)
            {
                if (r == null) continue;
                var candidates = BatchedPieces.At(Bridge.Path(r.transform));
                if (candidates == null || candidates.Count == 0) { e.CopyProblem = r.name + " isn't in the bridge's file"; Report(e); return; }
                if (candidates.Exists(p => p.HasRoot)) { e.CopyProblem = r.name + " is batched under a root of its own"; Report(e); return; }
                bool loaded = true;
                foreach (var p in candidates) if (!BatchedPieces.Loaded(p.Combined)) loaded = false;
                if (!loaded) return; // its combined mesh is being read
                plan.Add(new KeyValuePair<Renderer, BatchedPieces.Piece>(r, null));
            }
            // The copy is made with the piece where it was baked, then the edit is put back on.
            e.T.localPosition = e.BaseLocalPos;
            e.T.localRotation = e.BaseLocalRot;
            var root = new GameObject(CopyName);
            SceneManager.MoveGameObjectToScene(root, e.T.gameObject.scene);
            root.transform.SetParent(e.T, true);
            int made = 0;
            foreach (var pr in plan)
            {
                var r = pr.Key;
                // Same path more than once in the bridge: the one whose shape sits where this renderer is drawn.
                Mesh best = null; float bestD = float.MaxValue;
                foreach (var p in BatchedPieces.At(Bridge.Path(r.transform)))
                {
                    var m = BatchedPieces.Slice(p);
                    if (m == null) continue;
                    float d = (m.bounds.center - r.bounds.center).sqrMagnitude;
                    if (d < bestD) { if (best != null) UnityEngine.Object.Destroy(best); best = m; bestD = d; }
                    else UnityEngine.Object.Destroy(m);
                }
                if (best == null) continue;
                e.CopyMeshes.Add(best);
                var go = new GameObject(r.name);
                go.transform.SetParent(root.transform, false);
                // The slices' vertices are in the scene's space: the copy's own transform has to be the world's.
                go.transform.position = Vector3.zero;
                go.transform.rotation = Quaternion.identity;
                go.AddComponent<MeshFilter>().sharedMesh = best;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterials = r.sharedMaterials;
                mr.shadowCastingMode = r.shadowCastingMode;
                mr.receiveShadows = r.receiveShadows;
                mr.lightmapIndex = r.lightmapIndex;
                mr.lightmapScaleOffset = r.lightmapScaleOffset;
                made++;
            }
            if (made == 0) { UnityEngine.Object.Destroy(root); e.CopyProblem = "no shapes copied"; Report(e); return; }
            e.Copy = root;
            Plugin.Log.LogInfo($"Bridge editor: {e.Path}: {made} batched part(s) copied from the bridge's file to move");
        }

        private static void Report(Edit e)
        {
            Plugin.Log.LogInfo($"Bridge editor: {e.Path} can't move: {e.CopyProblem} (it can still be hidden)");
            if (open && selected == e) Say("Can't move this one (" + e.CopyProblem + "); H still hides it");
        }

        /// <summary>The piece back as the bridge had it.</summary>
        private static void Reset(Edit e)
        {
            if (e.T != null) { e.T.localPosition = e.BaseLocalPos; e.T.localRotation = e.BaseLocalRot; }
            foreach (var r in e.Normal) if (r != null) r.enabled = true;
            foreach (var c in e.Colliders) if (c != null) c.enabled = true;
            if (e.Copy != null) UnityEngine.Object.Destroy(e.Copy);
            foreach (var m in e.CopyMeshes) if (m != null) UnityEngine.Object.Destroy(m);
            e.Copy = null;
            e.CopyMeshes.Clear();
        }

        /// <summary>Left the bridge (its objects are gone with it) or it reloaded: only what we made goes.</summary>
        private static void Forget()
        {
            foreach (var e in edits) foreach (var m in e.CopyMeshes) if (m != null) UnityEngine.Object.Destroy(m);
            edits.Clear();
            selected = null;
            pickedNotEdited = null;
            lastHits.Clear();
            boundRoot = 0;
        }

        // ------------------------------------------------------------------------------------------- where things are
        /// <summary>Which of the bridge's objects with this one's path it is, in hierarchy order (our own objects left out).</summary>
        private static int IndexOf(Transform t)
        {
            string path = Bridge.Path(t);
            int n = 0, found = 0;
            Walk(x => { if (x == t) found = n; else if (Bridge.Path(x) == path) n++; return x == t; }, path);
            return found;
        }

        // Every object of the bridge whose name is the path's last part, in order, until visit says stop.
        private static void Walk(Func<Transform, bool> visit, string path)
        {
            string leaf = path.Substring(path.LastIndexOf('/') + 1);
            bool stop = false;
            void Go(Transform x)
            {
                if (stop || x.name == CopyName || x.name == TestCorridor.RootName) return;
                if (x.name == leaf && visit(x)) { stop = true; return; }
                foreach (Transform c in x) Go(c);
            }
            foreach (var top in bridge.GetRootGameObjects()) Go(top.transform);
        }

        private static Transform Resolve(string path, int index)
        {
            Transform hit = null;
            int n = 0;
            Walk(x =>
            {
                if (Bridge.Path(x) != path) return false;
                if (n++ == index) { hit = x; return true; }
                return false;
            }, path);
            return hit;
        }

        // ------------------------------------------------------------------------------------------- the file
        private static string FileFor(string scene) => Path.Combine(Path.Combine(Paths.ConfigPath, FolderName), scene + ".txt");

        private static void Load()
        {
            string file = FileFor(bridge.name);
            if (!File.Exists(file)) return;
            int bound = 0, missing = 0;
            try
            {
                foreach (var raw in File.ReadAllLines(file))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var f = line.Split('\t');
                    if (f.Length < 5) continue;
                    var e = new Edit
                    {
                        Path = f[0],
                        Index = int.Parse(f[1], CultureInfo.InvariantCulture),
                        Move = Vec(f[2]),
                        Turn = float.Parse(f[3], CultureInfo.InvariantCulture),
                        Hidden = f[4].Trim() == "hidden",
                    };
                    edits.Add(e);
                    var t = Resolve(e.Path, e.Index);
                    if (t == null) { missing++; continue; }
                    Bind(e, t);
                    Refresh(e);
                    bound++;
                }
                Plugin.Log.LogInfo($"Bridge editor: {bridge.name}: {bound} saved change(s) put on{(missing > 0 ? $", {missing} not found in this bridge (kept in the file)" : "")}");
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"Bridge editor: reading {file}: {ex.Message}"); }
        }

        private static void Save()
        {
            saveAt = -1f;
            if (boundRoot == 0) return;
            string file = FileFor(bridge.name);
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"# Bridge layout for {bridge.name}, made with Bridge Walk's editor (F8 while standing). One changed piece a line:");
                sb.AppendLine("# path<TAB>which of that path<TAB>move x,y,z (in its parent's space)<TAB>turn (degrees)<TAB>hidden or shown");
                int n = 0;
                foreach (var e in edits)
                {
                    if (!e.Changed) continue;
                    sb.Append(e.Path).Append('\t').Append(e.Index.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(F(e.Move.x)).Append(',').Append(F(e.Move.y)).Append(',').Append(F(e.Move.z)).Append('\t')
                      .Append(F(e.Turn)).Append('\t').Append(e.Hidden ? "hidden" : "shown").AppendLine();
                    n++;
                }
                if (n == 0) { if (File.Exists(file)) File.Delete(file); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, sb.ToString());
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"Bridge editor: saving {file}: {ex.Message}"); }
        }

        private static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        private static Vector3 Vec(string s)
        {
            var p = s.Split(',');
            return new Vector3(float.Parse(p[0], CultureInfo.InvariantCulture), float.Parse(p[1], CultureInfo.InvariantCulture), float.Parse(p[2], CultureInfo.InvariantCulture));
        }

        // ------------------------------------------------------------------------------------------- on screen
        private static void Say(string what)
        {
            status = what;
            statusUntil = Time.unscaledTime + 3f;
        }

        private static GUIStyle style;

        internal static void OnGUI()
        {
            if (!open) return;
            if (style == null) style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 13, wordWrap = true };
            // The aiming dot.
            GUI.Label(new Rect(Screen.width / 2f - 5, Screen.height / 2f - 10, 20, 20), "+");
            var sb = new StringBuilder();
            sb.AppendLine("BRIDGE EDITOR (prototype) — " + bridge.name);
            var t = selected != null ? selected.T : pickedNotEdited;
            if (t != null)
            {
                sb.AppendLine("Picked: " + Bridge.Path(t));
                if (selected != null)
                {
                    sb.AppendLine($"Moved {selected.Move.x:0.00}, {selected.Move.y:0.00}, {selected.Move.z:0.00}   turned {selected.Turn:0}°{(selected.Hidden ? "   HIDDEN" : "")}");
                    if (selected.Batched.Count > 0) sb.AppendLine(selected.Copy != null ? "Batched: a copy of it is moved" : selected.CopyProblem != null ? "Can't move: " + selected.CopyProblem : selected.Moved ? "Batched: reading its shape..." : "Batched: a copy is made when it moves");
                }
            }
            else sb.AppendLine("Aim the + at something and click to pick it");
            sb.AppendLine();
            sb.AppendLine("Click: pick (again: the thing behind)   U: its group");
            sb.AppendLine("Right mouse + move: slide   Wheel: turn   Right mouse + wheel: up/down   Shift: finer");
            sb.AppendLine("H: hide/show   Backspace: put back   " + Plugin.BridgeEditorKey.Value + ": done");
            if (status != null && Time.unscaledTime < statusUntil) sb.AppendLine().Append(status);
            GUI.Box(new Rect(12, 12, 520, 150), sb.ToString(), style);
        }

        // The picked thing's outline, drawn over the 2D camera's picture.
        private static void DrawSelection(Camera c)
        {
            if (!open || c == null || Walker.Eye == null) return;
            if (c.transform != Walker.Eye && !c.transform.IsChildOf(Walker.Eye)) return;
            var t = selected != null ? selected.T : pickedNotEdited;
            if (t == null) return;
            Bounds? all = null;
            foreach (var r in t.GetComponentsInChildren<Renderer>(false))
            {
                if (!r.enabled && !(selected != null && selected.Hidden)) continue;
                if (r is ParticleSystemRenderer) continue;
                if (all == null) all = r.bounds; else { var b0 = all.Value; b0.Encapsulate(r.bounds); all = b0; }
            }
            if (all == null) return;
            if (lineMat == null)
            {
                lineMat = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
                lineMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                lineMat.SetInt("_ZWrite", 0);
                lineMat.SetInt("_Cull", 0);
            }
            var b = all.Value;
            Vector3 mn = b.min, mx = b.max;
            lineMat.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.LINES);
            GL.Color(selected != null && selected.Hidden ? new Color(1f, 0.3f, 0.3f) : new Color(1f, 0.85f, 0.2f));
            for (int i = 0; i < 4; i++)
            {
                // Four edges along each axis.
                float a = (i & 1) == 0 ? 0 : 1, d = (i & 2) == 0 ? 0 : 1;
                Line(new Vector3(mn.x, L(mn.y, mx.y, a), L(mn.z, mx.z, d)), new Vector3(mx.x, L(mn.y, mx.y, a), L(mn.z, mx.z, d)));
                Line(new Vector3(L(mn.x, mx.x, a), mn.y, L(mn.z, mx.z, d)), new Vector3(L(mn.x, mx.x, a), mx.y, L(mn.z, mx.z, d)));
                Line(new Vector3(L(mn.x, mx.x, a), L(mn.y, mx.y, d), mn.z), new Vector3(L(mn.x, mx.x, a), L(mn.y, mx.y, d), mx.z));
            }
            GL.End();
            GL.PopMatrix();
        }

        private static float L(float a, float b, float t) => t == 0 ? a : b;
        private static void Line(Vector3 a, Vector3 b) { GL.Vertex(a); GL.Vertex(b); }
    }

    /// <summary>
    /// A bridge scene's statically batched pieces as its file has them: for each object, which combined mesh it's
    /// drawn in and which of that mesh's sub-meshes are its. The list is read once per scene on a background thread;
    /// a combined mesh itself is read (also in the background) the first time a piece of it is asked for.
    /// </summary>
    internal static class BatchedPieces
    {
        internal sealed class Piece
        {
            public long Combined;
            public bool HasRoot;
            public readonly List<int> SubMeshes = new List<int>();
        }

        private static readonly Dictionary<string, List<Piece>> byPath = new Dictionary<string, List<Piece>>();
        private static readonly Dictionary<long, MeshAsset> meshes = new Dictionary<long, MeshAsset>();
        private static readonly HashSet<long> wanted = new HashSet<long>();
        private static int scene = -1;
        private static string file;
        private static volatile bool reading, done, loading;
        internal static volatile string Error;

        /// <summary>Starts reading (once per scene); true once the list is read (or failed: see Error).</summary>
        internal static bool Ready(int buildIndex)
        {
            if (buildIndex < 0) { Error = "the bridge has no file"; return true; }
            if (scene == buildIndex) return done;
            if (reading || loading) return false;
            scene = buildIndex;
            done = false;
            Error = null;
            lock (meshes) { byPath.Clear(); meshes.Clear(); wanted.Clear(); }
            file = Path.Combine(Application.dataPath, "level" + buildIndex);
            reading = true;
            string path = file;
            new Thread(() =>
            {
                try { Read(path); }
                catch (Exception e) { Error = e.Message; }
                reading = false;
                done = true;
            }) { IsBackground = true, Name = "Bridge editor pieces" }.Start();
            return false;
        }

        private static void Read(string path)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int count = 0;
            using (var f = new AssetFile(path))
            {
                var names = new Dictionary<long, string>();   // transform path ID -> its object's name
                var fathers = new Dictionary<long, long>();   // transform path ID -> its parent's (0: none)
                var goTransform = new Dictionary<long, long>();
                foreach (var entry in f.Objects)
                {
                    if (entry.ClassId != 4) continue;
                    var t = TransformAsset.Read(f.At(entry));
                    var goEntry = f.Find(t.GameObject.PathId);
                    if (goEntry == null) continue;
                    names[entry.PathId] = GameObjectAsset.Read(f.At(goEntry)).Name;
                    fathers[entry.PathId] = t.Father.IsNull ? 0 : t.Father.PathId;
                    goTransform[t.GameObject.PathId] = entry.PathId;
                }
                string PathOf(long tid)
                {
                    var sb = new StringBuilder();
                    for (int guard = 0; tid != 0 && guard < 64; guard++)
                    {
                        if (!names.TryGetValue(tid, out var n)) break;
                        sb.Insert(0, "/" + n);
                        fathers.TryGetValue(tid, out tid);
                    }
                    return sb.ToString();
                }
                foreach (var entry in f.Objects)
                {
                    if (entry.ClassId != 23) continue;
                    var renderer = MeshRendererAsset.Read(f.At(entry));
                    if (renderer.SubsetIndices.Count == 0) continue;
                    var goEntry = f.Find(renderer.GameObject.PathId);
                    if (goEntry == null || !goTransform.TryGetValue(renderer.GameObject.PathId, out var tid)) continue;
                    var go = GameObjectAsset.Read(f.At(goEntry));
                    var filterRef = go.Component(33);
                    if (filterRef.IsNull) continue;
                    var filterEntry = f.Find(filterRef.PathId);
                    if (filterEntry == null) continue;
                    var filter = MeshFilterAsset.Read(f.At(filterEntry));
                    if (filter.Mesh.FileId != 0) continue;
                    var piece = new Piece { Combined = filter.Mesh.PathId, HasRoot = !renderer.StaticBatchRoot.IsNull };
                    foreach (var i in renderer.SubsetIndices) piece.SubMeshes.Add((int)i);
                    string p = PathOf(tid);
                    lock (meshes)
                    {
                        if (!byPath.TryGetValue(p, out var l)) byPath[p] = l = new List<Piece>();
                        l.Add(piece);
                    }
                    count++;
                }
            }
            Plugin.Log.LogInfo($"Bridge editor: {count} batched pieces listed in {Path.GetFileName(path)} ({sw.ElapsedMilliseconds} ms, in the background)");
        }

        internal static List<Piece> At(string path)
        {
            lock (meshes) return byPath.TryGetValue(path, out var l) ? l : null;
        }

        /// <summary>Whether a combined mesh is read; if not, it's asked for.</summary>
        internal static bool Loaded(long combined)
        {
            lock (meshes)
            {
                if (meshes.ContainsKey(combined)) return true;
                wanted.Add(combined);
                if (loading) return false;
                loading = true;
            }
            string path = file;
            new Thread(() =>
            {
                try
                {
                    using (var f = new AssetFile(path))
                        while (true)
                        {
                            long id;
                            lock (meshes)
                            {
                                id = 0;
                                foreach (var w in wanted) { id = w; break; }
                                if (id == 0) { loading = false; return; }
                                wanted.Remove(id);
                            }
                            var entry = f.Find(id);
                            var m = entry != null ? MeshAsset.Read(f.At(entry)) : null;
                            lock (meshes) meshes[id] = m;
                        }
                }
                catch (Exception e)
                {
                    Error = e.Message;
                    lock (meshes) { foreach (var w in wanted) meshes[w] = null; wanted.Clear(); loading = false; }
                }
            }) { IsBackground = true, Name = "Bridge editor mesh" }.Start();
            return false;
        }

        /// <summary>Main thread: a piece's sub-meshes of its combined mesh (in the scene's space), as a mesh of its own.</summary>
        internal static Mesh Slice(Piece piece)
        {
            MeshAsset m;
            lock (meshes) meshes.TryGetValue(piece.Combined, out m);
            if (m == null) return null;
            var map = new Dictionary<int, int>();
            var vertices = new List<Vector3>();
            var normals = m.Has(1) ? new List<Vector3>() : null;
            var uvs = m.Has(3) ? new List<Vector2>() : null;
            var uv2s = m.Has(4) ? new List<Vector2>() : null;
            var tangents = m.Has(7) ? new List<Vector4>() : null;
            var triangles = new List<int[]>();
            foreach (var s in piece.SubMeshes)
            {
                if (s < 0 || s >= m.SubMeshes.Count) continue;
                var sub = m.SubMeshes[s];
                var tri = new int[sub.IndexCount];
                for (int i = 0; i < tri.Length; i++)
                {
                    int v = BitConverter.ToUInt16(m.IndexBuffer, (int)sub.FirstByte + i * 2);
                    if (!map.TryGetValue(v, out var at))
                    {
                        at = map[v] = vertices.Count;
                        var p = m.Get(0, v);
                        vertices.Add(new Vector3(p[0], p[1], p[2]));
                        if (normals != null) { var n = m.Get(1, v); normals.Add(new Vector3(n[0], n[1], n[2])); }
                        if (uvs != null) { var u = m.Get(3, v); uvs.Add(new Vector2(u[0], u[1])); }
                        if (uv2s != null) { var u = m.Get(4, v); uv2s.Add(new Vector2(u[0], u[1])); }
                        if (tangents != null) { var t = m.Get(7, v); tangents.Add(new Vector4(t[0], t[1], t[2], t[3])); }
                    }
                    tri[i] = at;
                }
                triangles.Add(tri);
            }
            if (vertices.Count == 0) return null;
            var mesh = new Mesh { name = BridgeEditor.CopyName + " piece" };
            mesh.vertices = vertices.ToArray();
            if (normals != null) mesh.normals = normals.ToArray();
            if (uvs != null) mesh.uv = uvs.ToArray();
            if (uv2s != null) mesh.uv2 = uv2s.ToArray();
            if (tangents != null) mesh.tangents = tangents.ToArray();
            mesh.subMeshCount = triangles.Count;
            for (int i = 0; i < triangles.Count; i++) mesh.SetTriangles(triangles[i], i);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
