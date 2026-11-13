using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// [Avatar] AllHairStyles (experimental, off by default): every hairstyle in the character creator, on any head. The game's HEAD choice picks a
    /// character preset (a person: Gunter, Hector...), its HAIR (look) choice one of that preset's four looks, and a
    /// look is one texture with that person's face and a hairstyle painted together (the alpha channel is the hair's
    /// mask; the normal and specular maps follow the same layout). All male presets share one head layout, all
    /// female ones another, so a hairstyle from another preset can be cut out by its mask and put on this face:
    /// the face comes from the preset's own looks (at each spot the look with the least hair there), the hair (and
    /// under hair this face's looks had and the chosen one doesn't, the other look's skin, tinted to this face's) from
    /// the chosen look; colour, normal and specular maps alike. Looks 0-3 stay the preset's own; 4 and up are the
    /// other presets' of the same sex, in the game's preset order. The creator's counters show the real numbers
    /// (HAIR 7/28, HEAD 2/4). The look number is saved and sent to the crew as it is (HeadTextureIdx): crewmates
    /// with the mod see the hairstyle, those without the preset's fourth look. Not for Androids (their looks are
    /// their own).
    /// The textures are read from the game's files and put together on a background thread; the texture is made
    /// (and compressed) on the main thread (Tick).
    /// </summary>
    internal static class HairStyles
    {
        private const int GameLooks = 4;

        private sealed class Style { public string Preset, Material; public int Look; public bool Female; }

        private static List<Style> styles;           // every non-Android preset's four looks, in the game's preset order
        private static readonly FieldInfo FaceMesh = AccessTools.Field(typeof(FaceCustomization), "_faceMesh");
        private static readonly FieldInfo LookIdx = AccessTools.Field(typeof(FaceCustomization), "_currentLookIdx");
        private static readonly FieldInfo LookNames = AccessTools.Field(typeof(FaceCustomization), "LookMaterialNames");
        private static readonly MethodInfo RenderOrder = AccessTools.Method(typeof(FaceCustomization), "HLDEKGCKKKA");

        // Patched whatever the setting: the hooks do nothing while it's off, so Revival Settings can switch it at once.
        internal static void Patch(Harmony harmony)
        {
            if (FaceMesh == null || LookIdx == null || LookNames == null) { Plugin.Log.LogWarning("Hairstyles: FaceCustomization's fields not found; off"); return; }
            Hook(harmony, typeof(FaceCustomization), "SetNextLook", nameof(Next_Prefix), null);
            Hook(harmony, typeof(FaceCustomization), "SetPrevLook", nameof(Prev_Prefix), null);
            var setLook = AccessTools.Method(typeof(FaceCustomization), "SetLook", new[] { typeof(int) });
            if (setLook != null) harmony.Patch(setLook, postfix: new HarmonyMethod(typeof(HairStyles), nameof(SetLook_Postfix)));
            else Plugin.Log.LogWarning("Hairstyles: FaceCustomization.SetLook(int) not found");
        }

        private static void Hook(Harmony harmony, Type type, string name, string prefix, string postfix)
        {
            var m = AccessTools.Method(type, name);
            if (m == null) { Plugin.Log.LogWarning($"Hairstyles: {type.Name}.{name} not found"); return; }
            harmony.Patch(m, prefix == null ? null : new HarmonyMethod(typeof(HairStyles), prefix), postfix == null ? null : new HarmonyMethod(typeof(HairStyles), postfix));
        }

        // ---- which hairstyles a face has --------------------------------------------------------------------

        private static List<Style> Styles()
        {
            if (styles != null) return styles;
            var list = new List<Style>();
            try
            {
                if (!(RomulanFace.Profiles() is IList profiles)) return list;
                foreach (var p in profiles)
                {
                    var profile = p as CharacterProfile;
                    if (profile == null || profile.VisualPrefab == null || profile.CharacterSpecies.ToString() == "Android") continue;
                    var face = profile.VisualPrefab.GetComponentInChildren<FaceCustomization>(true);
                    if (face == null || face.IsAnAndroid || !(LookNames.GetValue(face) is string[] names) || names.Length < GameLooks) continue;
                    if (list.Any(s => s.Material == names[0])) continue;
                    bool female = profile.CharacterGender.ToString() == "Female";
                    for (int i = 0; i < GameLooks; i++) list.Add(new Style { Preset = profile.CharacterName, Material = names[i], Look = i, Female = female });
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Hairstyles: listing the presets: " + e.Message); }
            if (list.Count > 0)
            {
                styles = list;
                Plugin.Log.LogInfo($"Hairstyles: {list.Count(s => !s.Female)} male and {list.Count(s => s.Female)} female hairstyles from {list.Count / GameLooks} presets");
            }
            return list;
        }

        private sealed class Face { public string Preset; public string[] Own; public List<Style> Others; }

        // This face's preset (by its own looks) and the other presets' hairstyles of its sex; null if none (Androids,
        // the Romulan and Klingon heads' presets, the creator's stand-in).
        private static Face Of(FaceCustomization face)
        {
            try
            {
                if (face == null || face.IsAnAndroid || !(LookNames.GetValue(face) is string[] names) || names.Length < GameLooks) return null;
                var all = Styles();
                var own = all.FirstOrDefault(s => s.Material == names[0]);
                if (own == null) return null;
                return new Face { Preset = own.Preset, Own = names, Others = all.Where(s => s.Female == own.Female && s.Preset != own.Preset).ToList() };
            }
            catch { return null; }
        }

        internal static int Count(FaceCustomization face) { var f = Of(face); return f == null ? GameLooks : GameLooks + f.Others.Count; }

        private static int Current(FaceCustomization face) => LookIdx.GetValue(face) is int i ? i : 0;

        private static bool Next_Prefix(FaceCustomization __instance)
        {
            if (!Plugin.AllHairStyles.Value) return true;
            int count = Count(__instance);
            if (count <= GameLooks) return true;
            __instance.SetLook((Current(__instance) + 1) % count);
            return false;
        }

        private static bool Prev_Prefix(FaceCustomization __instance)
        {
            if (!Plugin.AllHairStyles.Value) return true;
            int count = Count(__instance);
            if (count <= GameLooks) return true;
            __instance.SetLook(((Current(__instance) - 1) % count + count) % count);
            return false;
        }

        // ---- putting a hairstyle on ------------------------------------------------------------------------

        // Past its own looks the game has put the preset's fourth look on (no head shape); the chosen hairstyle goes
        // on once it's put together (at once if it's been made before), with its look's head shape.
        private static void SetLook_Postfix(FaceCustomization __instance, int __0)
        {
            if (!Plugin.AllHairStyles.Value) return;
            var f = Of(__instance);
            if (f == null) return;
            Prefetch(__instance, f);
            if (__0 < GameLooks || __0 - GameLooks >= f.Others.Count) return;
            var style = f.Others[__0 - GameLooks];
            var renderer = FaceMesh.GetValue(__instance) as SkinnedMeshRenderer;
            if (renderer == null || Romulan(renderer)) return;
            try { if (!__instance.IsAnAndroid && style.Look < 3) __instance.SetCustomization(style.Look, 1f); } catch { }
            string key = f.Preset + "|" + style.Material;
            if (made.TryGetValue(key, out var material) && material != null) { Wear(__instance, material); return; }
            Request(__instance, f, style, key, false);
        }

        // The skin settings the creator has made on the face's material (skin and race sliders, hair colour) carry
        // over to the hairstyle's.
        private static readonly string[] SkinColors = { "_MainTexColor", "_SkinColor1", "_SkinColor2", "_TransmissionColor", "_HairColor" };
        private static readonly string[] SkinFloats = { "_HairIntensity", "_ScatteringSpread", "_NormalSoftening", "_SpecularIntensity", "_SmoothnessModifier" };

        // The Romulan captain's face (RomulanFace) is a face of its own: no hairstyle goes over it.
        private static bool Romulan(SkinnedMeshRenderer renderer) =>
            renderer.sharedMaterial != null && renderer.sharedMaterial.name.IndexOf("Romulan", StringComparison.OrdinalIgnoreCase) >= 0;

        // Each face's copy of the hairstyle it wears (see Wear).
        private static readonly Dictionary<FaceCustomization, Material> wornCopies = new Dictionary<FaceCustomization, Material>();

        // Copies whose face is gone, or has another material on now (the game put one of its own looks on), go.
        private static void ForgetWorn()
        {
            List<FaceCustomization> gone = null;
            foreach (var pair in wornCopies)
            {
                var on = pair.Key != null ? (FaceMesh.GetValue(pair.Key) as SkinnedMeshRenderer)?.sharedMaterial : null;
                if (pair.Key == null || on != pair.Value) (gone ?? (gone = new List<FaceCustomization>())).Add(pair.Key);
            }
            if (gone == null) return;
            foreach (var face in gone)
            {
                var copy = wornCopies[face];
                wornCopies.Remove(face);
                if (copy != null) UnityEngine.Object.Destroy(copy);
            }
        }

        private static void Wear(FaceCustomization face, Material material)
        {
            var renderer = FaceMesh.GetValue(face) as SkinnedMeshRenderer;
            if (renderer == null || Romulan(renderer)) return;
            try
            {
                var before = renderer.sharedMaterial;
                // The face's own copy (its skin settings go on it), made here once per wearing: renderer.material would
                // make a new one each time, never freed. The copy it had before goes, unless it's still the one on.
                var now = new Material(material) { name = material.name };
                renderer.sharedMaterial = now;
                if (wornCopies.TryGetValue(face, out var old) && old != null && old != now) UnityEngine.Object.Destroy(old);
                wornCopies[face] = now;
                if (before != null)
                {
                    foreach (var p in SkinColors) if (before.HasProperty(p) && now.HasProperty(p)) now.SetColor(p, before.GetColor(p));
                    foreach (var p in SkinFloats) if (before.HasProperty(p) && now.HasProperty(p)) now.SetFloat(p, before.GetFloat(p));
                }
                face.SetHairColorMultiplier(face.GetHairColorMultiplier());
                RenderOrder?.Invoke(face, null);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Hairstyles: putting {material.name} on: {e.Message}"); }
        }

        // ---- making a face-with-hairstyle texture set --------------------------------------------------------

        private static readonly string[] Maps = { "_MainTex", "_BumpMap", "_SpecularMap" };

        private sealed class Source { public string Name; public bool Linear; public TextureWrapMode Wrap; public FilterMode Filter; public int Aniso; }

        private sealed class Want { public FaceCustomization Face; public int Look; }

        private sealed class Job
        {
            public string Key, Preset, StyleName;
            public Material Base;                 // the preset's own look material, copied for the result
            public Source[][] Own;                // [look][map]
            public Source[] Hair;                 // [map]
            public byte[][] Dxt;                  // [map]: DXT5 with all its mips
            public int[] Width, Height;
            public bool Prefetch;                 // made ahead, for a hairstyle next to the one on
            public readonly List<Want> Wants = new List<Want>();
            public long Millis;
            public volatile bool Done;
            public string Error;
        }

        private static readonly Dictionary<string, Material> made = new Dictionary<string, Material>();
        private static readonly List<string> madeOrder = new List<string>();     // oldest first
        private static readonly Dictionary<string, Job> jobs = new Dictionary<string, Job>(); // queued or being made
        private static readonly List<Job> queue = new List<Job>();                // what the worker makes next
        private static readonly object gate = new object();
        private static Thread worker;
        private static Dictionary<string, List<KeyValuePair<string, long>>> textureIndex; // texture name: (file, path id)
        private static readonly object indexGate = new object();
        // Made hairstyles kept (beyond those worn), and how many each way of the one on are made ahead: three 2048²
        // DXT5 maps with mips are about 17 MB of video memory a hairstyle, so 8 is about 135 MB (26 was about 430 MB).
        private const int Keep = 8, Ahead = 3;

        private static void Request(FaceCustomization face, Face f, Style style, string key, bool prefetch)
        {
            lock (gate)
            {
                // Stepped on past one still waiting to be made for this face: it waits behind the new one, as one made ahead.
                if (!prefetch && face != null)
                    foreach (var old in queue)
                        if (!old.Prefetch && old.Key != key && old.Wants.All(w => w.Face == face)) old.Prefetch = true;
                if (jobs.TryGetValue(key, out var existing))
                {
                    if (face != null) existing.Wants.Add(new Want { Face = face, Look = Current(face) });
                    if (queue.Remove(existing))
                    {
                        // Wanted now: ahead of all; made ahead: in the order asked for (nearest to the one on first).
                        if (!prefetch) { existing.Prefetch = false; queue.Insert(0, existing); }
                        else if (existing.Prefetch) queue.Add(existing);
                        else queue.Insert(0, existing);
                    }
                    return;
                }
            }
            try
            {
                var own = new Material[GameLooks];
                for (int i = 0; i < GameLooks; i++) own[i] = Resources.Load<Material>("CharacterMaterials/" + f.Own[i]);
                var hair = Resources.Load<Material>("CharacterMaterials/" + style.Material);
                if (own.Any(m => m == null) || hair == null) { Plugin.Log.LogWarning($"Hairstyles: a look material of {f.Preset} or {style.Material} wasn't found"); return; }
                var job = new Job
                {
                    Key = key, Preset = f.Preset, StyleName = $"{style.Preset} {style.Look + 1}", Base = own[0], Prefetch = prefetch,
                    Own = own.Select(SourcesOf).ToArray(), Hair = SourcesOf(hair),
                };
                if (job.Own.Any(s => s == null) || job.Hair == null) { Plugin.Log.LogWarning($"Hairstyles: {f.Preset} or {style.Material} lacks a texture"); return; }
                if (face != null) job.Wants.Add(new Want { Face = face, Look = Current(face) });
                lock (gate)
                {
                    jobs[key] = job;
                    if (prefetch) queue.Add(job); else queue.Insert(0, job);
                    Monitor.Pulse(gate);
                }
                StartWorker();
            }
            catch (Exception e) { Plugin.Log.LogWarning("Hairstyles: " + e.Message); }
        }

        // One background worker makes them one after another (the three maps of each side by side).
        private static void StartWorker()
        {
            if (worker != null) return;
            string dataDir = Application.dataPath;
            worker = new Thread(() =>
            {
                while (true)
                {
                    Job job;
                    bool idle;
                    lock (gate) idle = queue.Count == 0;
                    // Nothing to make: the head's data, the spare buffers and the open asset files go until there is.
                    if (idle) Idle();
                    lock (gate)
                    {
                        while (queue.Count == 0) Monitor.Wait(gate);
                        job = queue[0];
                        queue.RemoveAt(0);
                    }
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    try { Compose(dataDir, job); }
                    catch (Exception e) { job.Error = e.Message; }
                    job.Millis = clock.ElapsedMilliseconds;
                    job.Done = true;
                }
            }) { IsBackground = true, Name = "Hairstyles" };
            worker.Start();
        }

        // The hairstyles next to the one on are made ahead, so stepping through them shows each at once (the creator only).
        private static void Prefetch(FaceCustomization face, Face f)
        {
            if (creator == null || !creator.isActiveAndEnabled) return;
            // The nearest few each way (next, previous, the one after...): stepping on, the next few are ready.
            int look = Current(face), count = GameLooks + f.Others.Count;
            for (int i = 1; i < count && i <= 2 * Ahead; i++)
            {
                int step = (i + 1) / 2 * (i % 2 == 1 ? 1 : -1);
                int index = ((look + step) % count + count) % count;
                if (index < GameLooks) continue;
                var style = f.Others[index - GameLooks];
                string key = f.Preset + "|" + style.Material;
                if (made.ContainsKey(key)) continue;
                Request(null, f, style, key, true);
            }
        }

        private static Source[] SourcesOf(Material m)
        {
            var result = new Source[Maps.Length];
            for (int i = 0; i < Maps.Length; i++)
            {
                if (!m.HasProperty(Maps[i])) return null;
                var t = m.GetTexture(Maps[i]);
                if (t == null) return null;
                result[i] = new Source { Name = t.name, Wrap = t.wrapMode, Filter = t.filterMode, Aniso = t.anisoLevel };
            }
            return result;
        }

        /// <summary>Main thread: finished texture sets become materials, worn by the faces still on that look.</summary>
        internal static void Tick()
        {
            if (!Plugin.AllHairStyles.Value)
            {
                // Switched off: the hairstyles made and not worn go (one still worn stays until the look changes).
                if (made.Count > 0) Forget(0);
                return;
            }
            Counters();
            List<Job> done = null;
            lock (gate)
                foreach (var j in jobs.Values) if (j.Done) (done ?? (done = new List<Job>())).Add(j);
            if (done == null) return;
            foreach (var job in done)
            {
                lock (gate) jobs.Remove(job.Key);
                if (job.Error != null || job.Dxt == null) { Plugin.Log.LogWarning($"Hairstyles: {job.Key}: {job.Error ?? "nothing made"}"); continue; }
                try
                {
                    var material = new Material(job.Base) { name = "Hairstyle " + job.Key };
                    for (int i = 0; i < Maps.Length; i++)
                    {
                        var src = job.Hair[i];
                        var tex = new Texture2D(job.Width[i], job.Height[i], TextureFormat.DXT5, true, src.Linear)
                        { name = $"{job.Key} {Maps[i]}", wrapMode = src.Wrap, filterMode = src.Filter, anisoLevel = src.Aniso };
                        tex.LoadRawTextureData(job.Dxt[i]);
                        tex.Apply(false, true);
                        material.SetTexture(Maps[i], tex);
                    }
                    made[job.Key] = material;
                    madeOrder.Remove(job.Key);
                    madeOrder.Add(job.Key);
                    Plugin.Log.LogInfo($"Hairstyles: {job.Preset} with {job.StyleName}'s hair made in {job.Millis} ms{(job.Prefetch ? " (ahead)" : "")}");
                    foreach (var w in job.Wants)
                        if (w.Face != null && Current(w.Face) == w.Look) Wear(w.Face, material);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"Hairstyles: making {job.Key}: {e.Message}"); }
            }
            Forget(Keep);
        }

        // Beyond the last few made, hairstyles no face wears go (their textures too).
        private static void Forget(int keep)
        {
            ForgetWorn();
            if (made.Count <= keep) return;
            var worn = new HashSet<string>();
            foreach (var face in UnityEngine.Object.FindObjectsOfType<FaceCustomization>())
            {
                var m = (FaceMesh.GetValue(face) as SkinnedMeshRenderer)?.sharedMaterial;
                if (m != null && m.name.StartsWith("Hairstyle ")) worn.Add(m.name.Replace(" (Instance)", ""));
            }
            for (int i = 0; i < madeOrder.Count && made.Count > keep;)
            {
                string key = madeOrder[i];
                if (made.TryGetValue(key, out var material) && material != null && !worn.Contains(material.name))
                {
                    foreach (var map in Maps) { var t = material.GetTexture(map); if (t != null) UnityEngine.Object.Destroy(t); }
                    UnityEngine.Object.Destroy(material);
                    made.Remove(key);
                    madeOrder.RemoveAt(i);
                }
                else i++;
            }
        }

        // ---- background: reading and putting together ------------------------------------------------------

        // A preset's face, put together once from its own four looks: at each spot the look with least hair there.
        private sealed class FaceBase
        {
            public int W, H;
            public byte[] Pick, Hair;      // per colour-map pixel: the look used, its hair there
            public Color32[] Df;
            public Color32[][] Map;        // [map] the normal and specular maps, from the same looks
            public int[] MW, MH;
        }

        // The face of the preset last worked on (worker only): one at a time, about 55 MB at 2048².
        private static FaceBase cached;
        private static string cachedPreset;

        private static void ReleaseFace()
        {
            if (cached == null) return;
            Return(cached.Df);
            foreach (var m in cached.Map) Return(m);
            cached = null;
            cachedPreset = null;
        }

        // Worker, with nothing queued.
        private static void Idle()
        {
            ReleaseFace();
            lock (pool) pool.Clear();
            lock (openFiles)
            {
                foreach (var f in openFiles.Values) try { f.Dispose(); } catch { }
                openFiles.Clear();
            }
        }

        // Read one look at a time (not all four at once): at each spot the look with least hair so far is kept.
        private static FaceBase BaseOf(string dataDir, Job job)
        {
            if (cached != null && cachedPreset == job.Preset) return cached;
            ReleaseFace();
            var fb = new FaceBase { Map = new Color32[Maps.Length][], MW = new int[Maps.Length], MH = new int[Maps.Length] };
            for (int i = 0; i < GameLooks; i++)
            {
                var px = Read(dataDir, job.Own[i][0], out int ow, out int oh);
                if (i == 0)
                {
                    fb.W = ow; fb.H = oh;
                    int count = ow * oh;
                    fb.Pick = new byte[count]; fb.Hair = new byte[count]; fb.Df = Rent(count);
                    for (int p = 0; p < count; p++) { fb.Hair[p] = px[p].a; fb.Df[p] = px[p]; }
                }
                else
                {
                    if (ow != fb.W || oh != fb.H) { var sized = Resize(px, ow, oh, fb.W, fb.H); Return(px); px = sized; }
                    for (int p = 0; p < px.Length; p++)
                        if (px[p].a < fb.Hair[p]) { fb.Hair[p] = px[p].a; fb.Pick[p] = (byte)i; fb.Df[p] = px[p]; }
                }
                Return(px);
            }
            for (int m = 1; m < Maps.Length; m++)
            {
                int mw = 0, mh = 0;
                Color32[] map = null;
                for (int i = 0; i < GameLooks; i++)
                {
                    var px = Read(dataDir, job.Own[i][m], out int ow, out int oh);
                    if (i == 0) { mw = ow; mh = oh; map = Rent(mw * mh); }
                    else if (ow != mw || oh != mh) { var sized = Resize(px, ow, oh, mw, mh); Return(px); px = sized; }
                    for (int y = 0; y < mh; y++)
                        for (int x = 0; x < mw; x++)
                            if (fb.Pick[(y * fb.H / mh) * fb.W + (x * fb.W / mw)] == i) map[y * mw + x] = px[y * mw + x];
                    Return(px);
                }
                fb.Map[m] = map; fb.MW[m] = mw; fb.MH[m] = mh;
            }
            cached = fb;
            cachedPreset = job.Preset;
            return fb;
        }

        // Scratch pixel buffers by size, reused: Unity 5.4's Mono never gives memory back, and a fresh 16 MB buffer
        // for every texture read let its heap grow for good. A few of each size are kept (until the worker is idle).
        private static readonly Dictionary<int, Stack<Color32[]>> pool = new Dictionary<int, Stack<Color32[]>>();

        private static Color32[] Rent(int count)
        {
            lock (pool)
                if (pool.TryGetValue(count, out var stack) && stack.Count > 0) return stack.Pop();
            return new Color32[count];
        }

        private static void Return(Color32[] buffer)
        {
            if (buffer == null) return;
            lock (pool)
            {
                if (!pool.TryGetValue(buffer.Length, out var stack)) pool[buffer.Length] = stack = new Stack<Color32[]>();
                if (stack.Count < 4) stack.Push(buffer);
            }
        }

        private static void Compose(string dataDir, Job job)
        {
            Index(dataDir);
            var fb = BaseOf(dataDir, job);
            int maps = Maps.Length, w = fb.W, h = fb.H, n = w * h;
            job.Dxt = new byte[maps][];
            job.Width = new int[maps];
            job.Height = new int[maps];

            var hairDf = Read(dataDir, job.Hair[0], out int hw, out int hh);
            if (hw != w || hh != h) { var sized = Resize(hairDf, hw, hh, w, h); Return(hairDf); hairDf = sized; }

            // The normal and specular maps side by side with the colour map: the face's where neither has hair, the
            // chosen look's elsewhere.
            var threads = new List<Thread>();
            var errors = new List<string>();
            for (int m = 1; m < maps; m++)
            {
                int map = m;
                var t = new Thread(() =>
                {
                    try
                    {
                        int mw = fb.MW[map], mh = fb.MH[map];
                        var hair = Read(dataDir, job.Hair[map], out int ow, out int oh);
                        if (ow != mw || oh != mh) { var sized = Resize(hair, ow, oh, mw, mh); Return(hair); hair = sized; }
                        var face = fb.Map[map];
                        var outMap = Rent(mw * mh);
                        for (int y = 0; y < mh; y++)
                            for (int x = 0; x < mw; x++)
                            {
                                int p = y * mw + x, q = (y * h / mh) * w + (x * w / mw);
                                int k = 255 - (255 - fb.Hair[q]) * (255 - hairDf[q].a) / 255, ik = 255 - k;
                                var a = face[p]; var b = hair[p];
                                outMap[p] = new Color32((byte)((a.r * ik + b.r * k) / 255), (byte)((a.g * ik + b.g * k) / 255), (byte)((a.b * ik + b.b * k) / 255), (byte)((a.a * ik + b.a * k) / 255));
                            }
                        Return(hair);
                        job.Dxt[map] = Dxt5(outMap, mw, mh); job.Width[map] = mw; job.Height[map] = mh;
                        Return(outMap);
                    }
                    catch (Exception e) { lock (errors) errors.Add(e.Message); }
                }) { IsBackground = true, Name = "Hairstyles map " + map };
                t.Start();
                threads.Add(t);
            }

            // Tint for the other look's skin where it has to stand in: this face's skin over theirs, where both are bare.
            double fr = 0, fg = 0, fbl = 0, hr = 0, hg = 0, hbl = 0; long count = 0;
            for (int p = 0; p < n; p += 7)
            {
                var face = fb.Df[p]; var hair = hairDf[p];
                if (fb.Hair[p] > 12 || hair.a > 12 || face.r + face.g + face.b < 60 || hair.r + hair.g + hair.b < 60) continue;
                fr += face.r; fg += face.g; fbl += face.b; hr += hair.r; hg += hair.g; hbl += hair.b; count++;
            }
            // In 1/256ths.
            int tr = count > 0 && hr > 0 ? (int)(fr / hr * 256) : 256, tg = count > 0 && hg > 0 ? (int)(fg / hg * 256) : 256, tb = count > 0 && hbl > 0 ? (int)(fbl / hbl * 256) : 256;
            var df = Rent(n);
            for (int p = 0; p < n; p++)
            {
                var face = fb.Df[p]; var hair = hairDf[p];
                int fa = fb.Hair[p], ifa = 255 - fa, ha = hair.a, iha = 255 - ha;
                // Under the face's own hair: the other look's skin, tinted; then the chosen hair over all.
                int r = (face.r * ifa + Math.Min(255, hair.r * tr >> 8) * fa) / 255;
                int g = (face.g * ifa + Math.Min(255, hair.g * tg >> 8) * fa) / 255;
                int b = (face.b * ifa + Math.Min(255, hair.b * tb >> 8) * fa) / 255;
                df[p] = new Color32((byte)((r * iha + hair.r * ha) / 255), (byte)((g * iha + hair.g * ha) / 255), (byte)((b * iha + hair.b * ha) / 255), hair.a);
            }
            job.Dxt[0] = Dxt5(df, w, h); job.Width[0] = w; job.Height[0] = h;
            Return(df);
            foreach (var t in threads) t.Join();
            Return(hairDf);
            if (errors.Count > 0) throw new Exception(errors[0]);
        }

        // ---- DXT5 out: all mips, as LoadRawTextureData takes them -----------------------------------------------

        private static byte[] Dxt5(Color32[] top, int w, int h)
        {
            int size = 0;
            for (int lw = w, lh = h; ; lw = Math.Max(1, lw / 2), lh = Math.Max(1, lh / 2))
            {
                size += ((lw + 3) / 4) * ((lh + 3) / 4) * 16;
                if (lw == 1 && lh == 1) break;
            }
            var data = new byte[size];
            int o = 0;
            var level = top;
            for (int lw = w, lh = h; ;)
            {
                o = EncodeLevel(level, lw, lh, data, o);
                if (lw == 1 && lh == 1) break;
                int nw = Math.Max(1, lw / 2), nh = Math.Max(1, lh / 2);
                var next = Half(level, lw, lh, nw, nh);
                if (level != top) Return(level);
                level = next;
                lw = nw; lh = nh;
            }
            if (level != top) Return(level);
            return data;
        }

        private static Color32[] Half(Color32[] src, int sw, int sh, int dw, int dh)
        {
            var dst = Rent(dw * dh);
            for (int y = 0; y < dh; y++)
                for (int x = 0; x < dw; x++)
                {
                    int x0 = Math.Min(sw - 1, x * 2), x1 = Math.Min(sw - 1, x * 2 + 1), y0 = Math.Min(sh - 1, y * 2), y1 = Math.Min(sh - 1, y * 2 + 1);
                    Color32 a = src[y0 * sw + x0], b = src[y0 * sw + x1], c = src[y1 * sw + x0], d = src[y1 * sw + x1];
                    dst[y * dw + x] = new Color32((byte)((a.r + b.r + c.r + d.r + 2) >> 2), (byte)((a.g + b.g + c.g + d.g + 2) >> 2),
                                                  (byte)((a.b + b.b + c.b + d.b + 2) >> 2), (byte)((a.a + b.a + c.a + d.a + 2) >> 2));
                }
            return dst;
        }

        private static int EncodeLevel(Color32[] px, int w, int h, byte[] data, int o)
        {
            var block = new Color32[16];
            for (int by = 0; by < (h + 3) / 4; by++)
                for (int bx = 0; bx < (w + 3) / 4; bx++)
                {
                    for (int j = 0; j < 16; j++)
                    {
                        int x = Math.Min(w - 1, bx * 4 + (j & 3)), y = Math.Min(h - 1, by * 4 + (j >> 2));
                        block[j] = px[y * w + x];
                    }
                    EncodeBlock(block, data, o);
                    o += 16;
                }
            return o;
        }

        // One block: alpha between its lowest and highest (eight steps), colour along its bounding box's diagonal.
        private static void EncodeBlock(Color32[] b, byte[] d, int o)
        {
            byte amin = 255, amax = 0;
            int rmin = 255, gmin = 255, bmin = 255, rmax = 0, gmax = 0, bmax = 0;
            for (int j = 0; j < 16; j++)
            {
                var c = b[j];
                if (c.a < amin) amin = c.a;
                if (c.a > amax) amax = c.a;
                if (c.r < rmin) rmin = c.r;
                if (c.r > rmax) rmax = c.r;
                if (c.g < gmin) gmin = c.g;
                if (c.g > gmax) gmax = c.g;
                if (c.b < bmin) bmin = c.b;
                if (c.b > bmax) bmax = c.b;
            }
            // Alpha: a0 the highest, a1 the lowest; indices 0 (a0), 1 (a1), 2-7 the steps between from a0 down.
            d[o] = amax; d[o + 1] = amin;
            ulong abits = 0;
            if (amax > amin)
            {
                float range = amax - amin;
                for (int j = 0; j < 16; j++)
                {
                    int q = (int)((b[j].a - amin) / range * 7f + 0.5f);
                    int index = q == 7 ? 0 : q == 0 ? 1 : 8 - q;
                    abits |= (ulong)index << (3 * j);
                }
            }
            for (int i = 0; i < 6; i++) d[o + 2 + i] = (byte)(abits >> (8 * i));
            // Colour: c0 the box's top, c1 its bottom (as 5:6:5); indices 0 (c0), 1 (c1), 2 (2/3 c0), 3 (1/3 c0).
            int c0 = (rmax >> 3) << 11 | (gmax >> 2) << 5 | (bmax >> 3), c1 = (rmin >> 3) << 11 | (gmin >> 2) << 5 | (bmin >> 3);
            d[o + 8] = (byte)c0; d[o + 9] = (byte)(c0 >> 8); d[o + 10] = (byte)c1; d[o + 11] = (byte)(c1 >> 8);
            uint bits = 0;
            int dr = rmax - rmin, dg = gmax - gmin, db = bmax - bmin;
            int len = dr * dr + dg * dg + db * db;
            if (len > 0)
                for (int j = 0; j < 16; j++)
                {
                    var c = b[j];
                    float t = ((c.r - rmin) * dr + (c.g - gmin) * dg + (c.b - bmin) * db) / (float)len;
                    uint index = t >= 5f / 6f ? 0u : t >= 0.5f ? 2u : t >= 1f / 6f ? 3u : 1u;
                    bits |= index << (2 * j);
                }
            d[o + 12] = (byte)bits; d[o + 13] = (byte)(bits >> 8); d[o + 14] = (byte)(bits >> 16); d[o + 15] = (byte)(bits >> 24);
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private static Color32[] Resize(Color32[] src, int sw, int sh, int dw, int dh)
        {
            var dst = Rent(dw * dh);
            for (int y = 0; y < dh; y++) for (int x = 0; x < dw; x++) dst[y * dw + x] = src[(y * sh / dh) * sw + (x * sw / dw)];
            return dst;
        }

        // Every texture in the game's asset files by name (once).
        private static void Index(string dataDir)
        {
            lock (indexGate)
            {
                if (textureIndex != null) return;
                var index = new Dictionary<string, List<KeyValuePair<string, long>>>();
                foreach (var path in Directory.GetFiles(dataDir).Where(p =>
                {
                    var name = Path.GetFileName(p);
                    return name == "resources.assets" || (name.StartsWith("sharedassets") && name.EndsWith(".assets"));
                }).OrderBy(p => Path.GetFileName(p) == "resources.assets" ? 0 : 1))
                {
                    using (var file = new AssetFile(path))
                        foreach (var entry in file.Objects)
                        {
                            if (entry.ClassId != 28) continue;
                            string name;
                            try { name = file.NameOf(entry); } catch { continue; }
                            if (!index.TryGetValue(name, out var list)) index[name] = list = new List<KeyValuePair<string, long>>();
                            list.Add(new KeyValuePair<string, long>(path, entry.PathId));
                        }
                }
                textureIndex = index;
            }
        }

        private static Color32[] Read(string dataDir, Source source, out int width, out int height)
        {
            if (!textureIndex.TryGetValue(source.Name, out var found) || found.Count == 0) throw new FileNotFoundException($"texture {source.Name} isn't in the game's files");
            var where = found[0];
            TextureAsset t;
            // The asset files stay open (opening resources.assets, 43,000 objects, takes a good tenth of a second);
            // one reader each, so a texture's header is read under its file's lock.
            AssetFile file;
            lock (openFiles)
                if (!openFiles.TryGetValue(where.Key, out file)) openFiles[where.Key] = file = new AssetFile(where.Key);
            lock (file) t = TextureAsset.Read(file.At(file.Find(where.Value)));
            width = t.Width; height = t.Height;
            source.Linear = t.ColorSpace == 0;
            if (t.Format != 12 && t.Format != 10) throw new NotSupportedException($"texture {source.Name}: format {t.Format}");
            // Sizes as the file says, checked before anything that size is made or read.
            if (t.Width <= 0 || t.Height <= 0 || t.Width > 8192 || t.Height > 8192) throw new InvalidDataException($"texture {source.Name}: size {t.Width}x{t.Height}");
            bool dxt5 = t.Format == 12;
            byte[] data = t.Data.Length > 0 ? t.Data : Stream(dataDir, t);
            long need = (long)((t.Width + 3) / 4) * ((t.Height + 3) / 4) * (dxt5 ? 16 : 8);
            if (data.Length < need) throw new InvalidDataException($"texture {source.Name}: {data.Length} bytes, {need} needed");
            return Dxt(data, t.Width, t.Height, dxt5);
        }

        private static readonly Dictionary<string, AssetFile> openFiles = new Dictionary<string, AssetFile>();

        private static byte[] Stream(string dataDir, TextureAsset t)
        {
            // Only ever a file beside the asset files (the .resS the texture names), whatever path it gives.
            string name = Path.GetFileName(t.StreamPath ?? "");
            if (name.Length == 0 || t.StreamSize <= 0 || t.StreamSize > 256L * 1024 * 1024) throw new InvalidDataException($"texture {t.Name}: stream {t.StreamPath} ({t.StreamSize} bytes)");
            using (var s = new FileStream(Path.Combine(dataDir, name), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                s.Position = t.StreamOffset;
                var bytes = new byte[t.StreamSize];
                for (int got = 0; got < bytes.Length;)
                {
                    int r = s.Read(bytes, got, bytes.Length - got);
                    if (r <= 0) throw new EndOfStreamException(t.Name);
                    got += r;
                }
                return bytes;
            }
        }

        // DXT1 / DXT5 to pixels: the top mip only, rows in Unity's order (as SetPixels32 takes them).
        private static Color32[] Dxt(byte[] d, int w, int h, bool dxt5)
        {
            var px = Rent(w * h);
            int bw = (w + 3) / 4, bh = (h + 3) / 4, o = 0;
            var c = new Color32[4];
            var a = new byte[8];
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    ulong abits = 0;
                    if (dxt5)
                    {
                        a[0] = d[o]; a[1] = d[o + 1];
                        if (a[0] > a[1]) for (int i = 1; i < 7; i++) a[i + 1] = (byte)(((7 - i) * a[0] + i * a[1]) / 7);
                        else { for (int i = 1; i < 5; i++) a[i + 1] = (byte)(((5 - i) * a[0] + i * a[1]) / 5); a[6] = 0; a[7] = 255; }
                        for (int i = 0; i < 6; i++) abits |= (ulong)d[o + 2 + i] << (8 * i);
                        o += 8;
                    }
                    int c0 = d[o] | d[o + 1] << 8, c1 = d[o + 2] | d[o + 3] << 8;
                    uint bits = (uint)(d[o + 4] | d[o + 5] << 8 | d[o + 6] << 16 | d[o + 7] << 24);
                    o += 8;
                    c[0] = C565(c0); c[1] = C565(c1);
                    if (dxt5 || c0 > c1)
                    {
                        c[2] = new Color32((byte)((2 * c[0].r + c[1].r) / 3), (byte)((2 * c[0].g + c[1].g) / 3), (byte)((2 * c[0].b + c[1].b) / 3), 255);
                        c[3] = new Color32((byte)((c[0].r + 2 * c[1].r) / 3), (byte)((c[0].g + 2 * c[1].g) / 3), (byte)((c[0].b + 2 * c[1].b) / 3), 255);
                    }
                    else
                    {
                        c[2] = new Color32((byte)((c[0].r + c[1].r) / 2), (byte)((c[0].g + c[1].g) / 2), (byte)((c[0].b + c[1].b) / 2), 255);
                        c[3] = new Color32(0, 0, 0, 0);
                    }
                    for (int j = 0; j < 16; j++)
                    {
                        int x = bx * 4 + (j & 3), y = by * 4 + (j >> 2);
                        if (x >= w || y >= h) continue;
                        var col = c[(bits >> (2 * j)) & 3];
                        if (dxt5) col.a = a[(int)((abits >> (3 * j)) & 7)];
                        px[y * w + x] = col;
                    }
                }
            return px;
        }

        private static Color32 C565(int v) => new Color32((byte)((v >> 11 & 31) * 255 / 31), (byte)((v >> 5 & 63) * 255 / 63), (byte)((v & 31) * 255 / 31), 255);

        // ---- the creator's counters ------------------------------------------------------------------------

        private static readonly FieldInfo LookText = AccessTools.Field(typeof(CustomizationManager), "_lookText");
        private static readonly FieldInfo HeadText = AccessTools.Field(typeof(CustomizationManager), "_headText");
        private static readonly FieldInfo Avatar = AccessTools.Field(typeof(CustomizationManager), "IBONPLINBFL");
        private static readonly FieldInfo Gender = AccessTools.Field(typeof(CustomizationManager), "EPNEDLICCMJ");
        private static readonly FieldInfo Species = AccessTools.Field(typeof(CustomizationManager), "COABBGKIHFL");
        private static readonly MethodInfo GenderFits = AccessTools.Method(typeof(CustomizationManager), "ENPBOKEDLLO");
        private static readonly MethodInfo SpeciesFits = AccessTools.Method(typeof(CustomizationManager), "PEMPNBCOPFL");

        private static CustomizationManager creator;
        private static float nextCreatorSearch, nextCounters;
        private static string lastLog;
        private static readonly FieldInfo LookLabel = AccessTools.Field(typeof(CustomizationManager), "OGGAMIKLGBM");
        private static readonly FieldInfo HeadLabel = AccessTools.Field(typeof(CustomizationManager), "PPILFEEKDAP");
        private static readonly FieldInfo HeadIndex = AccessTools.Field(typeof(CustomizationManager), "JGOBPBOKGKO");

        // The creator writes "HAIR 3" and "HEAD 2" (label key, number) with its texts' SetLocalizedText, its hair
        // number counting round in fours. That little method is inlined where it's called, so it can't be patched:
        // four times a second while the creator is open the two texts are written again the same way, "HAIR 7/28"
        // (the face's own look number) and "HEAD 2/4".
        private static void Counters()
        {
            if (Time.unscaledTime < nextCounters) return;
            nextCounters = Time.unscaledTime + 0.25f;
            ForgetWorn();
            if (creator == null && Time.unscaledTime >= nextCreatorSearch)
            {
                nextCreatorSearch = Time.unscaledTime + 1f;
                creator = Tracked.First<CustomizationManager>();
            }
            if (creator == null || !creator.isActiveAndEnabled) return;
            try
            {
                var face = (Avatar?.GetValue(creator) as GameObject)?.GetComponentInChildren<FaceCustomization>(true);
                int look = face != null ? Current(face) + 1 : 0, looks = face != null ? Count(face) : 0;
                int head = HeadIndex?.GetValue(creator) is int h ? h + 1 : 0, heads = Heads(creator);
                if (face != null) Write(LookText?.GetValue(creator), LookLabel?.GetValue(creator) as string, look, looks);
                if (head > 0 && heads > 0) Write(HeadText?.GetValue(creator), HeadLabel?.GetValue(creator) as string, head, heads);
                string state = $"hair {look}/{looks} on {(face != null ? face.transform.root.name : "no face")}, head {head}/{heads}";
                if (state != lastLog) { lastLog = state; Plugin.Log.LogInfo("Hairstyles: creator counters " + state); }
            }
            catch (Exception e) { Plugin.Log.LogDebug("Hairstyles: counter: " + e.Message); }
        }

        private static void Write(object text, string labelKey, int number, int total)
        {
            if (text == null || labelKey == null) return;
            string want = $"{SVR.Localization.GetLocalizedText(labelKey)} {number}/{total}";
            var set = text.GetType().GetMethod("SetLocalizedText", new[] { typeof(string) });
            if (set != null) set.Invoke(text, new object[] { want });
            else text.GetType().GetProperty("text")?.SetValue(text, want, null);
        }

        // How many presets the creator's HEAD choice runs through (its sex and species).
        private static int Heads(CustomizationManager creator)
        {
            if (Gender == null || Species == null || GenderFits == null || SpeciesFits == null || !(RomulanFace.Profiles() is IList profiles)) return 0;
            object gender = Gender.GetValue(creator), species = Species.GetValue(creator);
            int n = 0;
            foreach (var p in profiles)
            {
                var profile = p as CharacterProfile;
                if (profile == null) continue;
                if ((bool)GenderFits.Invoke(null, new object[] { gender, profile.CharacterGender }) && (bool)SpeciesFits.Invoke(null, new object[] { species, profile.CharacterSpecies })) n++;
            }
            return n;
        }
    }
}
