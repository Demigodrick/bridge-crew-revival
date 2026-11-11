using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// Wavefront .obj models (with their .mtl), read from the player's own files: positions, texture coordinates and
    /// normals, faces fanned into triangles, one piece per material and at most 65000 vertices per piece (Unity's
    /// 16-bit indices). Coordinates as the file has them (right-handed, y up).
    /// </summary>
    internal static class ObjModel
    {
        /// <summary>One textured piece of a model (one material's faces), in the file's coordinates.</summary>
        internal sealed class Part
        {
            public string Name, Texture;
            public Vector3[] Vertices, Normals;
            public Vector2[] Uv;
            public int[] Triangles;
        }

        internal sealed class Material
        {
            public string Name, Diffuse, Emission;
            public Color Colour = Color.white;
            public float Opacity = 1f;
        }

        private const int MaxVertices = 65000;

        internal static List<Part> Read(string path, out Dictionary<string, Material> materials)
        {
            materials = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
            var pos = new List<Vector3>(); var uv = new List<Vector2>(); var nrm = new List<Vector3>();
            var faces = new Dictionary<string, List<int[]>>(); // material → corners (v, vt, vn per corner, flattened per face)
            var order = new List<string>();
            string current = "";
            var inv = CultureInfo.InvariantCulture;
            var sep = new[] { ' ', '\t' };
            using (var reader = new StreamReader(path))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length < 2) continue;
                    var p = line.Split(sep, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length == 0) continue;
                    switch (p[0])
                    {
                        case "v": pos.Add(new Vector3(float.Parse(p[1], inv), float.Parse(p[2], inv), float.Parse(p[3], inv))); break;
                        case "vt": uv.Add(new Vector2(float.Parse(p[1], inv), p.Length > 2 ? float.Parse(p[2], inv) : 0f)); break;
                        case "vn": nrm.Add(new Vector3(float.Parse(p[1], inv), float.Parse(p[2], inv), float.Parse(p[3], inv))); break;
                        case "usemtl": current = p.Length > 1 ? p[1] : ""; break;
                        case "mtllib":
                        {
                            var mtl = Path.Combine(Path.GetDirectoryName(path), line.Substring(line.IndexOf(' ') + 1).Trim());
                            if (!File.Exists(mtl)) mtl = Path.ChangeExtension(path, ".mtl");
                            if (File.Exists(mtl)) ReadMtl(mtl, materials);
                            break;
                        }
                        case "f":
                        {
                            int n = p.Length - 1;
                            var corners = new int[n * 3];
                            for (int i = 0; i < n; i++)
                            {
                                var idx = p[i + 1].Split('/');
                                corners[i * 3] = Index(idx[0], pos.Count);
                                corners[i * 3 + 1] = idx.Length > 1 && idx[1].Length > 0 ? Index(idx[1], uv.Count) : -1;
                                corners[i * 3 + 2] = idx.Length > 2 && idx[2].Length > 0 ? Index(idx[2], nrm.Count) : -1;
                            }
                            if (!faces.TryGetValue(current, out var list)) { faces[current] = list = new List<int[]>(); order.Add(current); }
                            list.Add(corners);
                            break;
                        }
                    }
                }
            }
            if (materials.Count == 0 && File.Exists(Path.ChangeExtension(path, ".mtl"))) ReadMtl(Path.ChangeExtension(path, ".mtl"), materials);

            var parts = new List<Part>();
            foreach (var mat in order)
            {
                var map = new Dictionary<long, int>();
                var verts = new List<Vector3>(); var uvs = new List<Vector2>(); var norms = new List<Vector3>(); var tris = new List<int>();
                void Flush()
                {
                    if (tris.Count == 0) return;
                    parts.Add(new Part { Name = mat, Texture = mat, Vertices = verts.ToArray(), Uv = uvs.ToArray(), Normals = norms.ToArray(), Triangles = tris.ToArray() });
                    map.Clear(); verts.Clear(); uvs.Clear(); norms.Clear(); tris.Clear();
                }
                int Corner(int[] c, int k)
                {
                    int v = c[k * 3], t = c[k * 3 + 1], nn = c[k * 3 + 2];
                    long key = ((long)v << 42) ^ ((long)(t + 1) << 21) ^ (nn + 1);
                    if (map.TryGetValue(key, out var at)) return at;
                    at = verts.Count;
                    verts.Add(pos[v]); uvs.Add(t >= 0 ? uv[t] : Vector2.zero); norms.Add(nn >= 0 ? nrm[nn] : Vector3.zero);
                    map[key] = at;
                    return at;
                }
                foreach (var c in faces[mat])
                {
                    int n = c.Length / 3;
                    if (verts.Count + n > MaxVertices) Flush();
                    int first = Corner(c, 0);
                    int prev = Corner(c, 1);
                    for (int k = 2; k < n; k++) { int next = Corner(c, k); tris.Add(first); tris.Add(prev); tris.Add(next); prev = next; }
                }
                Flush();
            }
            return parts;
        }

        private static int Index(string s, int count)
        {
            int i = int.Parse(s, CultureInfo.InvariantCulture);
            return i < 0 ? count + i : i - 1;
        }

        private static void ReadMtl(string path, Dictionary<string, Material> materials)
        {
            Material m = null;
            var inv = CultureInfo.InvariantCulture;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int space = line.IndexOfAny(new[] { ' ', '\t' });
                string key = space < 0 ? line : line.Substring(0, space), rest = space < 0 ? "" : line.Substring(space + 1).Trim();
                switch (key)
                {
                    case "newmtl": m = new Material { Name = rest }; materials[rest] = m; break;
                    case "map_Kd": if (m != null) m.Diffuse = FileName(rest); break;
                    case "map_Ke": if (m != null) m.Emission = FileName(rest); break;
                    case "Kd":
                        if (m != null) { var p = rest.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries); if (p.Length >= 3) m.Colour = new Color(float.Parse(p[0], inv), float.Parse(p[1], inv), float.Parse(p[2], inv)); }
                        break;
                    case "d": if (m != null && float.TryParse(rest, NumberStyles.Float, inv, out var d)) m.Opacity = d; break;
                }
            }
        }

        // A full path in the .mtl ("C:\textures\hull.tif") → just the file name (textures are looked for beside the model).
        private static string FileName(string path) => path.Replace('\\', '/').Substring(path.Replace('\\', '/').LastIndexOf('/') + 1);
    }
}
