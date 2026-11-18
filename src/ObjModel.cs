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
            public string Normal, Gloss;       // map_Bump: a normal map (packed for Unity: x in alpha, y in green); map_Ns: a metallic-gloss map
            public Color Colour = Color.white;
            public Color Glow = Color.black;   // Ke: a glow of its own colour, without a texture
            public float Opacity = 1f;
        }

        private const int MaxVertices = 65000;
        // Sanity limits for a ship (Mono's heap never shrinks, and the whole model is held while it's built): the Nova
        // has about 300,000 positions and 430,000 triangles.
        private const int MaxPositions = 2000000, MaxFaces = 4000000;

        /// <summary>One material's faces as read: the corners (v, vt, vn per corner; -1 for none) of every face, flat, and each face's corner count.</summary>
        private sealed class Faces { public readonly List<int> Corners = new List<int>(); public readonly List<int> Counts = new List<int>(); }

        // Read without splitting lines into strings: a ship's .obj has over a million lines, and the strings and arrays
        // a split makes per line come to gigabytes of garbage; collecting that stalls the game (on every thread) while
        // the model is read in the briefing room. Numbers are read straight out of each line.
        internal static List<Part> Read(string path, out Dictionary<string, Material> materials)
        {
            materials = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
            var pos = new List<Vector3>(); var uv = new List<Vector2>(); var nrm = new List<Vector3>();
            var faces = new Dictionary<string, Faces>(); // material → its faces
            var order = new List<string>();
            string current = "";
            Faces currentFaces = null;
            int faceCount = 0;
            var corner = new int[64 * 3];
            using (var reader = new StreamReader(path, System.Text.Encoding.UTF8, true, 1 << 16))
            {
                var line = new char[256];
                int n;
                while ((n = ReadLine(reader, ref line)) >= 0)
                {
                    int i = 0;
                    while (i < n && (line[i] == ' ' || line[i] == '\t')) i++;
                    if (n - i < 2) continue;
                    char c0 = line[i], c1 = line[i + 1];
                    if (c0 == 'v' && (c1 == ' ' || c1 == '\t'))
                    {
                        if (pos.Count >= MaxPositions) throw new InvalidDataException($"over {MaxPositions} vertices");
                        i++;
                        float x = Float(line, ref i, n), y = Float(line, ref i, n), z = Float(line, ref i, n);
                        pos.Add(new Vector3(x, y, z));
                    }
                    else if (c0 == 'v' && c1 == 't' && i + 2 < n && (line[i + 2] == ' ' || line[i + 2] == '\t'))
                    {
                        i += 2;
                        float u = Float(line, ref i, n);
                        float v = More(line, i, n) ? Float(line, ref i, n) : 0f;
                        uv.Add(new Vector2(u, v));
                    }
                    else if (c0 == 'v' && c1 == 'n' && i + 2 < n && (line[i + 2] == ' ' || line[i + 2] == '\t'))
                    {
                        i += 2;
                        float x = Float(line, ref i, n), y = Float(line, ref i, n), z = Float(line, ref i, n);
                        nrm.Add(new Vector3(x, y, z));
                    }
                    else if (c0 == 'f' && (c1 == ' ' || c1 == '\t'))
                    {
                        i++;
                        int count = 0;
                        while (More(line, i, n))
                        {
                            while (line[i] == ' ' || line[i] == '\t') i++;
                            int v = Int(line, ref i, n, pos.Count), t = -1, nn = -1;
                            if (i < n && line[i] == '/')
                            {
                                i++;
                                if (i < n && line[i] != '/' && line[i] != ' ' && line[i] != '\t') t = Int(line, ref i, n, uv.Count);
                                if (i < n && line[i] == '/') { i++; if (i < n && line[i] != ' ' && line[i] != '\t') nn = Int(line, ref i, n, nrm.Count); }
                            }
                            while (i < n && line[i] != ' ' && line[i] != '\t') i++; // anything else in the corner
                            if (count < 64) { corner[count * 3] = v; corner[count * 3 + 1] = t; corner[count * 3 + 2] = nn; }
                            count++;
                        }
                        if (++faceCount > MaxFaces) throw new InvalidDataException($"over {MaxFaces} faces");
                        if (count < 3 || count > 64) continue; // not a face (or not one a ship needs)
                        if (currentFaces == null && !faces.TryGetValue(current, out currentFaces)) { faces[current] = currentFaces = new Faces(); order.Add(current); }
                        for (int k = 0; k < count * 3; k++) currentFaces.Corners.Add(corner[k]);
                        currentFaces.Counts.Add(count);
                    }
                    else if (Starts(line, i, n, "usemtl"))
                    {
                        var p = new string(line, i, n - i).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        current = p.Length > 1 ? p[1] : "";
                        currentFaces = null;
                    }
                    else if (Starts(line, i, n, "mtllib") && i + 6 < n && (line[i + 6] == ' ' || line[i + 6] == '\t'))
                    {
                        var mtl = Path.Combine(Path.GetDirectoryName(path), new string(line, i + 6, n - i - 6).Trim());
                        if (!File.Exists(mtl)) mtl = Path.ChangeExtension(path, ".mtl");
                        if (File.Exists(mtl)) ReadMtl(mtl, materials);
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
                var f = faces[mat];
                int Corner(int at)
                {
                    int v = f.Corners[at], t = f.Corners[at + 1], nn = f.Corners[at + 2];
                    long key = ((long)v << 42) ^ ((long)(t + 1) << 21) ^ (nn + 1);
                    if (map.TryGetValue(key, out var idx)) return idx;
                    idx = verts.Count;
                    verts.Add(pos[v]); uvs.Add(t >= 0 ? uv[t] : Vector2.zero); norms.Add(nn >= 0 ? nrm[nn] : Vector3.zero);
                    map[key] = idx;
                    return idx;
                }
                int start = 0;
                foreach (int count in f.Counts)
                {
                    if (verts.Count + count > MaxVertices) Flush();
                    int first = Corner(start);
                    int prev = Corner(start + 3);
                    for (int k = 2; k < count; k++) { int next = Corner(start + k * 3); tris.Add(first); tris.Add(prev); tris.Add(next); prev = next; }
                    start += count * 3;
                }
                Flush();
            }
            return parts;
        }

        /// <summary>The next line into a reused buffer (grown as needed): its length, or -1 at the end of the file.</summary>
        private static int ReadLine(StreamReader reader, ref char[] line)
        {
            int n = 0, c;
            while ((c = reader.Read()) >= 0)
            {
                if (c == '\n') return n;
                if (c == '\r') { if (reader.Peek() == '\n') reader.Read(); return n; }
                if (n == line.Length) Array.Resize(ref line, line.Length * 2);
                line[n++] = (char)c;
            }
            return n > 0 ? n : -1;
        }

        private static bool Starts(char[] s, int i, int n, string word)
        {
            if (n - i < word.Length) return false;
            for (int k = 0; k < word.Length; k++) if (s[i + k] != word[k]) return false;
            return true;
        }

        /// <summary>Another number follows on the line (after spaces or tabs).</summary>
        private static bool More(char[] s, int i, int n)
        {
            while (i < n && (s[i] == ' ' || s[i] == '\t')) i++;
            return i < n && s[i] != '#';
        }

        /// <summary>The number at i (after any spaces), read without making a string; i moves past it.</summary>
        private static float Float(char[] s, ref int i, int n)
        {
            while (i < n && (s[i] == ' ' || s[i] == '\t')) i++;
            int begin = i;
            bool negative = false;
            if (i < n && (s[i] == '-' || s[i] == '+')) { negative = s[i] == '-'; i++; }
            double value = 0; int digits = 0, scale = 0;
            while (i < n && s[i] >= '0' && s[i] <= '9') { if (digits < 18) { value = value * 10 + (s[i] - '0'); digits++; } else scale++; i++; }
            if (i < n && s[i] == '.')
            {
                i++;
                while (i < n && s[i] >= '0' && s[i] <= '9') { if (digits < 18) { value = value * 10 + (s[i] - '0'); digits++; scale--; } i++; }
            }
            if (i < n && (s[i] == 'e' || s[i] == 'E'))
            {
                int j = i + 1; bool expNegative = false; int exp = 0, expDigits = 0;
                if (j < n && (s[j] == '-' || s[j] == '+')) { expNegative = s[j] == '-'; j++; }
                while (j < n && s[j] >= '0' && s[j] <= '9') { if (exp < 1000) exp = exp * 10 + (s[j] - '0'); expDigits++; j++; }
                if (expDigits > 0) { scale += expNegative ? -exp : exp; i = j; }
            }
            if (i == begin || (i < n && s[i] != ' ' && s[i] != '\t' && s[i] != '#'))
            {
                // Not a plain number (nan, inf, a stray character): read as before, or fail as before.
                int end = i; while (end < n && s[end] != ' ' && s[end] != '\t') end++;
                i = end;
                return float.Parse(new string(s, begin, end - begin), CultureInfo.InvariantCulture);
            }
            double result = scale == 0 ? value : scale > 0 ? value * Math.Pow(10, scale) : value / Math.Pow(10, -scale);
            return (float)(negative ? -result : result);
        }

        /// <summary>A face corner's index at i (1-based, or negative from the end), as a 0-based index; checked as before.</summary>
        private static int Int(char[] s, ref int i, int n, int count)
        {
            int begin = i;
            bool negative = false;
            if (i < n && (s[i] == '-' || s[i] == '+')) { negative = s[i] == '-'; i++; }
            long value = 0; int digits = 0;
            while (i < n && s[i] >= '0' && s[i] <= '9') { value = value * 10 + (s[i] - '0'); digits++; i++; if (value > int.MaxValue) throw new InvalidDataException("index too large"); }
            if (digits == 0) throw new FormatException($"not an index at column {begin} of \"{new string(s, 0, n)}\"");
            int at = negative ? count - (int)value : (int)value - 1;
            if (at < 0 || at >= count) throw new InvalidDataException($"index {(negative ? -value : value)} with {count} defined");
            return at;
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
                    case "map_Bump": case "bump": case "norm": if (m != null) m.Normal = FileName(rest); break;
                    case "map_Ns": if (m != null) m.Gloss = FileName(rest); break;
                    case "Ke":
                        if (m != null) { var p = rest.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries); if (p.Length >= 3 && float.TryParse(p[0], NumberStyles.Float, inv, out var r) && float.TryParse(p[1], NumberStyles.Float, inv, out var g) && float.TryParse(p[2], NumberStyles.Float, inv, out var bl)) m.Glow = new Color(r, g, bl); }
                        break;
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
