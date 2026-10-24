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
    /// [Debug] AvatarDumpKey (F9): describe the hail-screen characters (Romulan captain and the rest, from the
    /// ship prefabs' NPCHailingView) and the player avatar presets (CharacterProfile.VisualPrefab, plus any
    /// avatars in the scene) to BepInEx/avatar-dump.txt, with a head-by-head comparison in the log. For
    /// working out whether an NPC head can be fitted to a player avatar: same skeleton, same mesh layout,
    /// same face shapes?
    /// </summary>
    internal static class AvatarDump
    {
        private static float lastRun = -10f;

        internal static void Update()
        {
            if (!Input.GetKeyDown(Plugin.AvatarDumpKey.Value) || Time.realtimeSinceStartup - lastRun < 3f) return;
            lastRun = Time.realtimeSinceStartup;
            var path = Path.Combine(Paths.BepInExRootPath, "avatar-dump.txt");
            try
            {
                var report = new StringBuilder();
                var summary = Run(report);
                File.WriteAllText(path, report.ToString());
                foreach (var line in summary) Plugin.Log.LogInfo("Avatar dump: " + line);
                Plugin.Log.LogInfo($"Avatar dump: full details in {path}");
                RoomBrowser.Show("Avatar dump saved: BepInEx/avatar-dump.txt");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Avatar dump failed: " + e);
            }
        }

        private static List<string> Run(StringBuilder report)
        {
            var summary = new List<string>();

            // Hail-screen characters: each ship prefab's NPCHailingView has the character it shows.
            var hailView = AccessTools.TypeByName("NPCHailingView");
            var characterField = hailView == null ? null : AccessTools.Field(hailView, "_characterPrefab");
            var npcs = new Dictionary<string, GameObject>();
            if (characterField != null)
                foreach (var ship in Resources.LoadAll<GameObject>("Sector/Ships"))
                foreach (var view in ship.GetComponentsInChildren(hailView, true))
                    if (characterField.GetValue(view) is GameObject character && !npcs.ContainsKey(character.name))
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
            + $"{r.bones.Length} bones, {r.sharedMesh.blendShapeCount} face shapes, uv {r.sharedMesh.uv.Length} uv2 {r.sharedMesh.uv2.Length}";

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
