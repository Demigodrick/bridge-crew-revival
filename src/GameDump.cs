using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// [Debug] GameDumpKey (F9): describe the game's state to BepInEx/game-dump.txt, with a summary in the log:
    /// the hail-screen characters (Romulan captain and the rest, from the bridge's NPCHailingManager), the
    /// player avatar presets (CharacterProfile.VisualPrefab, plus any avatars in the scene), the sound objects
    /// and the scene, with a head-by-head comparison (could an NPC head fit a player avatar: same skeleton,
    /// same mesh layout, same face shapes?).
    /// </summary>
    internal static class GameDump
    {
        private static float lastRun = -10f;

        internal static void Update()
        {
            if (!Input.GetKeyDown(Plugin.GameDumpKey.Value) || Time.realtimeSinceStartup - lastRun < 3f) return;
            lastRun = Time.realtimeSinceStartup;
            var path = Path.Combine(Paths.BepInExRootPath, "game-dump.txt");
            try
            {
                var report = new StringBuilder();
                var summary = Run(report);
                File.WriteAllText(path, report.ToString());
                foreach (var line in summary) Plugin.Log.LogInfo("Game dump: " + line);
                Plugin.Log.LogInfo($"Game dump: full details in {path}");
                RoomBrowser.Show("Game dump saved: BepInEx/game-dump.txt");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Game dump failed: " + e);
            }
        }

        private static List<string> Run(StringBuilder report)
        {
            var summary = new List<string>();

            // Hail-screen characters: the bridge's NPCHailingManager lists a view per character
            // (_hailingViews), and each view has the character it shows (_characterPrefab).
            var hailManager = AccessTools.TypeByName("NPCHailingManager");
            var viewsField = hailManager == null ? null : AccessTools.Field(hailManager, "_hailingViews");
            var characterField = AccessTools.Field(AccessTools.TypeByName("NPCHailingView"), "_characterPrefab");
            var npcs = new Dictionary<string, GameObject>();
            if (viewsField != null && characterField != null)
                foreach (var manager in Resources.FindObjectsOfTypeAll(hailManager))
                    if (viewsField.GetValue(manager) is Array views)
                        foreach (var view in views)
                            if (view != null && characterField.GetValue(view) is GameObject character && !npcs.ContainsKey(character.name))
                                npcs[character.name] = character;
            summary.Add($"{npcs.Count} hail-screen characters: {string.Join(", ", npcs.Keys.OrderBy(n => n).ToArray())}");

            // Player presets, as the character creator offers them.
            var profiles = Resources.FindObjectsOfTypeAll<CharacterProfile>()
                .Where(p => p.VisualPrefab != null).GroupBy(p => p.VisualPrefab.name).Select(g => g.First())
                .OrderBy(p => p.CharacterSpecies.ToString()).ThenBy(p => p.CharacterName).ToList();
            summary.Add($"{profiles.Count} player presets loaded: "
                        + string.Join(", ", profiles.Select(p => $"{p.CharacterName} ({p.CharacterSpecies} {p.CharacterGender})").ToArray()));
            var inScene = UnityEngine.Object.FindObjectsOfType<CharacterCustomization>();
            summary.Add($"{inScene.Length} avatars in this scene");

            report.AppendLine("==== HAIL-SCREEN CHARACTERS ====");
            foreach (var npc in npcs.Values.OrderBy(n => n.name)) Describe(report, "NPC " + npc.name, npc);
            report.AppendLine("==== PLAYER PRESETS ====");
            foreach (var p in profiles) Describe(report, $"PRESET {p.CharacterName} ({p.CharacterSpecies} {p.CharacterGender}) -> {p.VisualPrefab.name}", p.VisualPrefab);
            report.AppendLine("==== AVATARS IN SCENE ====");
            foreach (var c in inScene) Describe(report, "SCENE " + FullName(c.transform), c.gameObject);

            // Head against head: the Romulans (or every NPC if none) against each player preset.
            var romulans = npcs.Values.Where(n => n.name.IndexOf("Romulan", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            var compare = romulans.Count > 0 ? romulans : npcs.Values.ToList();
            var playerHeads = profiles.Select(p => new { Name = p.CharacterName + " " + p.CharacterSpecies, Head = Head(p.VisualPrefab) })
                .Concat(inScene.Select(c => new { Name = "scene " + c.name, Head = Head(c.gameObject) }))
                .Where(x => x.Head != null).ToList();
            // In the character creator: the Vulcan species toggle against the ROMULAN copy, as the VR hand sees them.
            var creator = UnityEngine.Object.FindObjectOfType<CustomizationManager>();
            if (creator != null)
            {
                report.AppendLine("==== CREATOR SPECIES TOGGLES ====");
                foreach (var name in new[] { "_humanRaceToggle", "_vulcanRaceToggle", "_androidRaceToggle" })
                    if (AccessTools.Field(typeof(CustomizationManager), name)?.GetValue(creator) is UnityEngine.UI.Toggle t)
                        Interaction(report, summary, name, t.gameObject);
                var romulan = GameObject.Find("Romulan Race Toggle");
                if (romulan != null) Interaction(report, summary, "ROMULAN", romulan);
                else summary.Add("ROMULAN toggle not found in the scene");
            }

            // Sound: every Wwise component in the loaded scenes (Ak*), with its event/state/bank names, and
            // which sector map and system are loaded. For comparing a Trench mission with another map.
            var sounds = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>()
                .Where(m => m != null && m.GetType().Name.StartsWith("Ak", StringComparison.Ordinal)).ToList();
            var loadedMap = Loader.EJLNDBGLIFL != null ? Loader.EJLNDBGLIFL.GetLoadedMap() : null;
            report.AppendLine("==== SOUND ====").AppendLine($"map {(loadedMap != null ? loadedMap.name : "none")}");
            foreach (var group in sounds.GroupBy(m => m.GetType().Name).OrderBy(g => g.Key))
            {
                report.AppendLine($"-- {group.Key} x{group.Count()}");
                foreach (var m in group)
                {
                    var names = AccessTools.GetDeclaredFields(m.GetType()).Concat(AccessTools.GetDeclaredFields(m.GetType().BaseType ?? typeof(object)))
                        .Where(f => !f.IsStatic && (f.FieldType == typeof(string) || f.FieldType == typeof(int) || f.FieldType == typeof(uint)))
                        .Select(f => { try { return $"{f.Name}={f.GetValue(m)}"; } catch { return null; } })
                        .Where(x => x != null).ToArray();
                    report.AppendLine($"   {FullName(m.transform)} active {m.isActiveAndEnabled} {string.Join(" ", names)}");
                }
            }
            summary.Add($"sound: map {(loadedMap != null ? loadedMap.name : "none")}, "
                        + string.Join(", ", sounds.GroupBy(m => m.GetType().Name).OrderBy(g => g.Key)
                            .Select(g => $"{g.Key} {g.Count(m => m.isActiveAndEnabled)}/{g.Count()} active").ToArray()));

            // Ship prefabs and the models they use (which one is the Excelsior, and so on): loaded, not spawned.
            report.AppendLine("==== SHIPS ====");
            foreach (var ship in new[] { "Aegis", "Enterprise", "Enterprise 1701D", "Raleigh", "Raleigh TNG", "Huxley", "Huxley TNG", "Saladin",
                         "Saladin TNG", "Nicholson", "Kobayashi Maru", "Science A", "Science B", "Science C", "Science D", "Class J" })
            {
                try
                {
                    var prefab = Resources.Load<GameObject>("Sector/Ships/Ship - " + ship);
                    if (prefab == null) { report.AppendLine($" {ship}: not found"); continue; }
                    var meshes = prefab.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null).Select(f => f.sharedMesh.name)
                        .Concat(prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).Select(r => r.sharedMesh.name))
                        .Distinct().ToArray();
                    var line = $"{ship}: {string.Join(", ", meshes)}";
                    report.AppendLine(" " + line);
                    if (meshes.Any(m => m.IndexOf("Excelsior", StringComparison.OrdinalIgnoreCase) >= 0)) summary.Add("Excelsior model: " + line);
                }
                catch (Exception e)
                {
                    report.AppendLine($" {ship}: {e.Message}");
                }
            }

            // The waiting areas (the briefing room, the shuttle): what's there, where, and what moves. For
            // dressing up the view (out of warp, a starbase, ships coming and going).
            foreach (var sceneName in new[] { "Aegis_Briefing_Room", "Lobby_Shuttle" })
            {
                var shuttleScene = UnityEngine.SceneManagement.SceneManager.GetSceneByName(sceneName);
                if (!shuttleScene.IsValid() || !shuttleScene.isLoaded) continue;
                report.AppendLine($"==== SCENE {sceneName} ====");
                foreach (var root in shuttleScene.GetRootGameObjects()) Scenery(report, root.transform, 0, 4);
                var all = shuttleScene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Component>(true)).Where(c => c != null).ToList();
                report.AppendLine("-- animators / animations");
                foreach (var a2 in all.OfType<Animator>())
                    report.AppendLine($"   {FullName(a2.transform)} active {a2.isActiveAndEnabled} controller {a2.runtimeAnimatorController?.name} clips "
                                      + string.Join(",", (a2.runtimeAnimatorController?.animationClips ?? new AnimationClip[0]).Select(c => c.name).ToArray()));
                foreach (var a2 in all.OfType<Animation>())
                    report.AppendLine($"   {FullName(a2.transform)} active {a2.isActiveAndEnabled} playing {a2.isPlaying} clip {a2.clip?.name}");
                report.AppendLine("-- particle systems");
                foreach (var ps in all.OfType<ParticleSystem>())
                    report.AppendLine($"   {FullName(ps.transform)} active {ps.gameObject.activeInHierarchy} playing {ps.isPlaying}");
                report.AppendLine("-- cameras");
                foreach (var cam in all.OfType<Camera>())
                    report.AppendLine($"   {FullName(cam.transform)} active {cam.isActiveAndEnabled} depth {cam.depth} clear {cam.clearFlags} far {cam.farClipPlane} pos {cam.transform.position}");
                report.AppendLine($"-- skybox {RenderSettings.skybox?.name}");
                var shuttle = GameObject.Find("Player_Shuttle_and_Cameras");
                summary.Add($"{sceneName}: {shuttleScene.rootCount} roots, {all.OfType<ParticleSystem>().Count()} particle systems, "
                            + $"{all.OfType<Animator>().Count() + all.OfType<Animation>().Count()} animated, skybox {RenderSettings.skybox?.name}, "
                            + $"shuttle at {(shuttle != null ? shuttle.transform.position.ToString() : "none")}, "
                            + $"main camera at {(Camera.main != null ? Camera.main.transform.position.ToString() : "?")}");
            }

            report.AppendLine("==== HEAD COMPARISON ====");
            foreach (var npc in compare)
            {
                var head = Head(npc);
                if (head == null) { summary.Add($"{npc.name}: no head mesh found"); continue; }
                summary.Add($"{npc.name} head: {MeshLine(head)}; root bone {head.rootBone?.name ?? "none"}");
                foreach (var player in playerHeads)
                {
                    var line = $"{npc.name} vs {player.Name}: {Compare(head, player.Head)}";
                    report.AppendLine(line);
                    summary.Add(line);
                }
            }
            return summary;
        }

        // The skinned mesh most likely to be the head: named head/face, else the one with the most face shapes.
        private static SkinnedMeshRenderer Head(GameObject root)
        {
            var meshes = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).ToList();
            return meshes.FirstOrDefault(r => Named(r, "head")) ?? meshes.FirstOrDefault(r => Named(r, "face"))
                   ?? meshes.OrderByDescending(r => r.sharedMesh.blendShapeCount).FirstOrDefault();
        }

        private static bool Named(SkinnedMeshRenderer r, string word) =>
            r.name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0
            || r.sharedMesh.name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;

        private static string Compare(SkinnedMeshRenderer a, SkinnedMeshRenderer b)
        {
            var bonesA = new HashSet<string>(a.bones.Where(x => x != null).Select(x => x.name));
            var bonesB = new HashSet<string>(b.bones.Where(x => x != null).Select(x => x.name));
            var shapesA = new HashSet<string>(Shapes(a.sharedMesh));
            var shapesB = new HashSet<string>(Shapes(b.sharedMesh));
            int sharedBones = bonesA.Count(bonesB.Contains), sharedShapes = shapesA.Count(shapesB.Contains);
            return $"vertices {a.sharedMesh.vertexCount}/{b.sharedMesh.vertexCount}"
                   + (a.sharedMesh.vertexCount == b.sharedMesh.vertexCount ? " (same)" : "")
                   + $", bones shared {sharedBones} of {bonesA.Count}/{bonesB.Count}"
                   + $", face shapes shared {sharedShapes} of {shapesA.Count}/{shapesB.Count}"
                   + $", root bones {a.rootBone?.name ?? "none"}/{b.rootBone?.name ?? "none"}"
                   + $", submeshes {a.sharedMesh.subMeshCount}/{b.sharedMesh.subMeshCount}";
        }

        private static IEnumerable<string> Shapes(Mesh mesh)
        {
            for (int i = 0; i < mesh.blendShapeCount; i++) yield return mesh.GetBlendShapeName(i);
        }

        private static string MeshLine(SkinnedMeshRenderer r) =>
            $"'{r.sharedMesh.name}' on '{r.name}', {r.sharedMesh.vertexCount} vertices, {r.sharedMesh.subMeshCount} submeshes, "
            + $"{r.bones.Length} bones, {r.sharedMesh.blendShapeCount} face shapes";  // not the UVs: reading them off the game's locked meshes logs an error per mesh

        private static void Describe(StringBuilder report, string title, GameObject root)
        {
            report.AppendLine().AppendLine("---- " + title);
            Hierarchy(report, root.transform, 0);
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r is SkinnedMeshRenderer s && s.sharedMesh != null)
                {
                    report.AppendLine("  SKINNED " + MeshLine(s) + $", root bone {s.rootBone?.name ?? "none"}");
                    report.AppendLine("    bones: " + string.Join(" ", s.bones.Select(b => b == null ? "(null)" : b.name).ToArray()));
                    var shapes = Shapes(s.sharedMesh).ToArray();
                    if (shapes.Length > 0) report.AppendLine("    face shapes: " + string.Join(" ", shapes));
                }
                else
                    report.AppendLine($"  RENDERER {r.GetType().Name} '{r.name}'");
                foreach (var m in r.sharedMaterials.Where(m => m != null))
                    report.AppendLine($"    material '{m.name}' shader '{m.shader?.name}' main '{(m.HasProperty("_MainTex") ? m.mainTexture?.name : "-")}'");
            }
        }

        private static void Interaction(StringBuilder report, List<string> summary, string label, GameObject toggle)
        {
            report.AppendLine().AppendLine($"---- {label}: {FullName(toggle.transform)} active {toggle.activeInHierarchy} layer {toggle.layer} pos {toggle.transform.localPosition}");
            Hierarchy(report, toggle.transform, 0);
            var hand = toggle.GetComponent<HandInteraction>();
            if (hand != null)
                foreach (var f in AccessTools.GetDeclaredFields(typeof(HandInteraction)).Where(f => !f.IsStatic))
                {
                    object v;
                    try { v = f.GetValue(hand); } catch { continue; }
                    var text = v is Array a ? $"[{a.Length}]" : v is UnityEngine.Object o ? (o == null ? "null" : o.name) : v?.ToString() ?? "null";
                    report.AppendLine($"    {f.Name} = {text}");
                }
            var colliders = toggle.GetComponentsInChildren<Collider>(true);
            foreach (var c in colliders)
                report.AppendLine($"  collider {c.GetType().Name} '{c.name}' enabled {c.enabled} active {c.gameObject.activeInHierarchy} layer {LayerMask.LayerToName(c.gameObject.layer)} centre {c.bounds.center} size {c.bounds.size}");
            summary.Add($"{label}: active {toggle.activeInHierarchy}, interactable {toggle.GetComponent<UnityEngine.UI.Toggle>()?.IsInteractable()}, "
                        + $"hand {(hand != null ? "yes" : "no")}, {colliders.Length} colliders ({colliders.Count(c => c.enabled && c.gameObject.activeInHierarchy)} live), "
                        + $"trigger centres {string.Join(" ", colliders.Where(c => c.gameObject.layer != 0).Select(c => c.bounds.center.ToString("F3")).ToArray())}");
        }

        // Name, active, position and components, to a depth: the shuttle scene is too big for the full tree.
        private static void Scenery(StringBuilder report, Transform t, int depth, int maxDepth)
        {
            var components = t.GetComponents<Component>().Where(c => c != null && !(c is Transform)).Select(c => c.GetType().Name).ToArray();
            report.Append(' ', depth * 2).Append(t.name).Append(t.gameObject.activeSelf ? "" : " (off)")
                  .Append($" @{t.position}");
            if (components.Length > 0) report.Append("  [").Append(string.Join(", ", components)).Append(']');
            if (depth == maxDepth && t.childCount > 0) report.Append($"  +{t.childCount} children");
            report.AppendLine();
            if (depth < maxDepth)
                for (int i = 0; i < t.childCount; i++) Scenery(report, t.GetChild(i), depth + 1, maxDepth);
        }

        private static void Hierarchy(StringBuilder report, Transform t, int depth)
        {
            if (depth > 12) return;
            var components = t.GetComponents<Component>().Where(c => c != null && !(c is Transform)).Select(c => c.GetType().Name).ToArray();
            report.Append(' ', depth * 2).Append(t.name);
            if (components.Length > 0) report.Append("  [").Append(string.Join(", ", components)).Append(']');
            report.AppendLine();
            for (int i = 0; i < t.childCount; i++) Hierarchy(report, t.GetChild(i), depth + 1);
        }

        private static string FullName(Transform t) => t.parent == null ? t.name : FullName(t.parent) + "/" + t.name;
    }
}
