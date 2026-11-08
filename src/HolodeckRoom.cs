using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StbcRevival
{
    /// <summary>
    /// The training holodeck's room (Bridge_scene_Holodeck: the grid walls, pillars, doorway, wires, light trim and
    /// the dark box around them), read from the game's own files on this PC, for a PvP room's briefing room
    /// (PvpLobby). Loading that scene would also start its tutorial, loader override and pause menu, so only its
    /// drawn pieces are read (AssetFile, as for the Klingon heads). The scene was statically batched: every piece
    /// is a slice (sub-meshes) of one combined mesh whose vertices are in the scene's own space, so each piece's
    /// slices are copied out and moved so the room's floor and middle meet the briefing room's. Its materials'
    /// stencil-masked shader (drawn only where the tutorial's reveal effect marks the screen) becomes Standard.
    /// The file is read once, on a background thread; the meshes and materials are made on the main thread.
    /// </summary>
    internal static class HolodeckRoom
    {
        private const string SceneFile = "level19"; // Assets/Nova/Scenes/Bridges/Bridge_scene_Holodeck.unity
        private static readonly string[] Parts = { "Holodeck_Walls", "Holodeck_DoorWay", "Holodeck_Wires", "Holodeck_Wall_Pillars" };
        private const string WallsPart = "Walls"; // the grid room, floor and ceiling included
        private const string StencilMasked = "RSE/StandardStencilMasked";

        private sealed class Piece
        {
            public string Name;
            public readonly List<int> SubMeshes = new List<int>();
            public readonly List<KlingonHeadFiles.MaterialPlan> Materials = new List<KlingonHeadFiles.MaterialPlan>();
        }

        private sealed class Room
        {
            public MeshAsset Combined;
            public readonly List<Piece> Pieces = new List<Piece>();
            public Vector3 FloorMiddle; // in the scene's space: the box's bottom, the walls' middle
        }

        private static Room room;
        private static bool reading;
        private static Exception failed;

        /// <summary>Starts reading the room (once); true once it's read and can be built.</summary>
        internal static bool Ready(MonoBehaviour host)
        {
            if (room != null) return true;
            if (!reading && failed == null && host != null)
            {
                reading = true;
                host.StartCoroutine(ReadInBackground());
            }
            return false;
        }

        internal static bool Failed => failed != null;

        private static System.Collections.IEnumerator ReadInBackground()
        {
            string dataDir = Application.dataPath;
            Room read = null;
            Exception error = null;
            var thread = new Thread(() =>
            {
                try { read = Read(dataDir); }
                catch (Exception e) { error = e; }
            }) { IsBackground = true, Name = "Holodeck room" };
            thread.Start();
            while (thread.IsAlive) yield return null;
            reading = false;
            if (error != null)
            {
                failed = error;
                Plugin.Log.LogWarning("PvP: reading the holodeck room from the game's files: " + error.Message);
                yield break;
            }
            room = read;
            var plans = room.Pieces.SelectMany(p => p.Materials).ToList();
            Plugin.Log.LogInfo($"PvP: holodeck room read ({room.Pieces.Count} pieces, {room.Combined.VertexCount} vertices in its scene, floor middle {room.FloorMiddle}; "
                               + $"{plans.SelectMany(p => p.Textures).Select(t => t.Value.Key).Distinct().Count()} textures, "
                               + $"{KlingonHeadFiles.PixelBytes(plans) / (1024 * 1024)} MB of pixels read, let go once they're made)");
        }

        // ---- Reading (background thread) ----------------------------------------------------------------

        private static Room Read(string dataDir)
        {
            var files = new Dictionary<string, AssetFile>(StringComparer.OrdinalIgnoreCase);
            try
            {
                AssetFile Open(AssetFile from, PPtr link)
                {
                    var name = from.FileOf(link);
                    if (name == null) return from;
                    if (!files.TryGetValue(name, out var file)) files[name] = file = new AssetFile(Path.Combine(dataDir, name));
                    return file;
                }
                var scene = files[SceneFile] = new AssetFile(Path.Combine(dataDir, SceneFile));
                GameObjectAsset Go(PPtr p) => GameObjectAsset.Read(scene.At(scene.Find(p.PathId)));
                TransformAsset Tr(PPtr p) => TransformAsset.Read(scene.At(scene.Find(p.PathId)));

                var result = new Room();
                long combined = 0;
                var seen = new HashSet<long>();
                void Collect(TransformAsset t)
                {
                    if (!seen.Add(t.GameObject.PathId)) return;
                    var go = Go(t.GameObject);
                    var rendererRef = go.Component(23);
                    var filterRef = go.Component(33);
                    if (!rendererRef.IsNull && !filterRef.IsNull)
                    {
                        var renderer = MeshRendererAsset.Read(scene.At(scene.Find(rendererRef.PathId)));
                        var filter = MeshFilterAsset.Read(scene.At(scene.Find(filterRef.PathId)));
                        if (renderer.Enabled && filter.Mesh.FileId == 0 && renderer.SubsetIndices.Count > 0)
                        {
                            if (combined != 0 && filter.Mesh.PathId != combined) throw new InvalidDataException($"{go.Name}: a second combined mesh");
                            combined = filter.Mesh.PathId;
                            var piece = new Piece { Name = go.Name };
                            for (int i = 0; i < renderer.SubsetIndices.Count && i < renderer.Materials.Count; i++)
                            {
                                piece.SubMeshes.Add((int)renderer.SubsetIndices[i]);
                                var plan = KlingonHeadFiles.ReadMaterial(dataDir, Open, scene, renderer.Materials[i]);
                                // The holodeck's own shader draws only where the tutorial's reveal marked the stencil
                                // buffer; Standard has the same textures and keywords and draws everywhere.
                                if (plan.Shader == StencilMasked) plan.Shader = "Standard";
                                piece.Materials.Add(plan);
                            }
                            result.Pieces.Add(piece);
                        }
                    }
                    foreach (var child in t.Children) Collect(Tr(child));
                }
                foreach (var entry in scene.Objects.Where(o => o.ClassId == 4))
                {
                    var t = TransformAsset.Read(scene.At(entry));
                    if (Array.IndexOf(Parts, Go(t.GameObject).Name) >= 0) Collect(t);
                }
                if (combined == 0 || result.Pieces.Count == 0) throw new InvalidDataException("no holodeck pieces found");
                result.Combined = MeshAsset.Read(scene.At(scene.Find(combined)));

                // Where it stands: the walls' floor (their most crowded height near the bottom: the floor panels,
                // about a metre above the dark box's bottom), under the middle of the walls.
                float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue, minY = float.MaxValue;
                var heights = new Dictionary<int, int>();
                foreach (var piece in result.Pieces.Where(p => p.Name == WallsPart))
                    foreach (var s in piece.SubMeshes)
                    {
                        var sub = result.Combined.SubMeshes[s];
                        for (uint v = sub.FirstVertex; v < sub.FirstVertex + sub.VertexCount; v++)
                        {
                            var p = result.Combined.Get(0, (int)v);
                            minX = Math.Min(minX, p[0]); maxX = Math.Max(maxX, p[0]); minZ = Math.Min(minZ, p[2]); maxZ = Math.Max(maxZ, p[2]);
                            minY = Math.Min(minY, p[1]);
                            int key = (int)Math.Round(p[1] * 10);
                            heights[key] = heights.TryGetValue(key, out var n) ? n + 1 : 1;
                        }
                    }
                if (minX == float.MaxValue) throw new InvalidDataException("no walls among the pieces");
                var floorLevel = heights.Where(h => h.Key / 10f < minY + 1.5f).OrderByDescending(h => h.Value).First().Key / 10f;
                result.FloorMiddle = new Vector3((minX + maxX) / 2, floorLevel, (minZ + maxZ) / 2);
                return result;
            }
            finally
            {
                foreach (var file in files.Values) file.Dispose();
            }
        }

        // ---- Building (main thread) ---------------------------------------------------------------------

        /// <summary>The room, in a scene, its floor's middle at a point there; null if a shader isn't loaded.</summary>
        internal static GameObject Build(Scene scene, Vector3 floorMiddle)
        {
            if (room == null) return null;
            var root = new GameObject("Holodeck room (PvP)");
            SceneManager.MoveGameObjectToScene(root, scene);
            var shift = floorMiddle - room.FloorMiddle;
            int made = 0, skipped = 0;
            foreach (var piece in room.Pieces)
            {
                var materials = piece.Materials.Select(KlingonHeadFiles.MakeMaterial).ToArray();
                if (materials.Any(m => m == null))
                {
                    skipped++;
                    Plugin.Log.LogWarning($"PvP: holodeck piece {piece.Name}: shader {piece.Materials.First(p => KlingonHeadFiles.MakeMaterial(p) == null).Shader} not loaded");
                    continue;
                }
                var go = new GameObject(piece.Name);
                go.transform.SetParent(root.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = Slice(piece, shift);
                go.AddComponent<MeshRenderer>().sharedMaterials = materials;
                made++;
            }
            Plugin.Log.LogInfo($"PvP: holodeck room built ({made} pieces{(skipped > 0 ? $", {skipped} without their shader" : "")}), moved by {shift}");
            return made > 0 ? root : null;
        }

        // A piece's sub-meshes of the combined mesh, with only the vertices they use, moved into place.
        private static Mesh Slice(Piece piece, Vector3 shift)
        {
            var m = room.Combined;
            var map = new Dictionary<int, int>();
            var vertices = new List<Vector3>();
            var normals = m.Has(1) ? new List<Vector3>() : null;
            var uvs = m.Has(3) ? new List<Vector2>() : null;
            var uv2s = m.Has(4) ? new List<Vector2>() : null;
            var tangents = m.Has(7) ? new List<Vector4>() : null;
            var triangles = new List<int[]>();
            foreach (var s in piece.SubMeshes)
            {
                var sub = m.SubMeshes[s];
                var tri = new int[sub.IndexCount];
                for (int i = 0; i < tri.Length; i++)
                {
                    int v = BitConverter.ToUInt16(m.IndexBuffer, (int)sub.FirstByte + i * 2);
                    if (!map.TryGetValue(v, out var at))
                    {
                        at = map[v] = vertices.Count;
                        var p = m.Get(0, v);
                        vertices.Add(new Vector3(p[0], p[1], p[2]) + shift);
                        if (normals != null) { var n = m.Get(1, v); normals.Add(new Vector3(n[0], n[1], n[2])); }
                        if (uvs != null) { var u = m.Get(3, v); uvs.Add(new Vector2(u[0], u[1])); }
                        if (uv2s != null) { var u = m.Get(4, v); uv2s.Add(new Vector2(u[0], u[1])); }
                        if (tangents != null) { var t = m.Get(7, v); tangents.Add(new Vector4(t[0], t[1], t[2], t[3])); }
                    }
                    tri[i] = at;
                }
                triangles.Add(tri);
            }
            var mesh = new Mesh { name = piece.Name };
            mesh.vertices = vertices.ToArray();
            if (normals != null) mesh.normals = normals.ToArray();
            if (uvs != null) mesh.uv = uvs.ToArray();
            if (uv2s != null) mesh.uv2 = uv2s.ToArray();
            if (tangents != null) mesh.tangents = tangents.ToArray();
            mesh.subMeshCount = triangles.Count;
            for (int s = 0; s < triangles.Count; s++) mesh.SetTriangles(triangles[s], s);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
