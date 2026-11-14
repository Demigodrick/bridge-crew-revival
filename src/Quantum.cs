using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Sector;
using Sector.Spacecraft;
using Sector.Spacecraft.Subsystems;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// [Ships] QuantumTorpedoes: the Sovereign's and the Defiant's torpedoes are quantum torpedoes (from YannikB98's
    /// add-on): blue, with a star-shaped flare, and [Ships] QuantumDamage times the damage (1 to 3; the host's setting
    /// counts, as the explosion's damage is worked out there). Only for a player ship whose body is one of those classes,
    /// never by name or the room's choice alone; the launchers, ammunition, guidance and shields are the game's.
    /// A salvo sound plays on our own ship's bridge if BepInEx/ships/quantum.wav is there (none comes with the mod).
    /// </summary>
    internal static class Quantum
    {
        private static readonly string[] Classes = { "Sovereign", "Defiant" };
        private static CustomShip[] classes;
        private static FieldInfo source;      // the payload's firing ship
        private static FieldInfo body;        // a player ship's body
        private static AudioClip salvoClip;
        private static bool audioTried;

        /// <summary>One of the classes' bodies has been built on this PC: no ship can fire one otherwise.</summary>
        private static bool Active
        {
            get
            {
                if (classes == null) return false;
                foreach (var c in classes) if (c != null && c.HasBody) return true;
                return false;
            }
        }

        internal static void Patch(Harmony harmony)
        {
            if (!Plugin.QuantumTorpedoes.Value) { Plugin.Log.LogInfo("Quantum torpedoes: off ([Ships] QuantumTorpedoes)"); return; }
            try
            {
                source = AccessTools.Field(typeof(LauncherPayload), "DOJCBJGPHJL");
                body = AccessTools.Field(typeof(PlayerSpacecraft), "KLENBCLCNKB");
                var init = AccessTools.DeclaredMethod(typeof(LauncherPayload), "LMOOIPLMILG", new[] { typeof(string) });
                var explosion = AccessTools.DeclaredMethod(typeof(BaseCombatManager), "Server_Explosion");
                if (source == null || source.FieldType != typeof(SectorBody) || body == null || !typeof(SpacecraftBody).IsAssignableFrom(body.FieldType)
                    || init == null || explosion == null || explosion.GetParameters().Length != 9 || explosion.GetParameters()[3].ParameterType != typeof(float))
                    throw new MissingMemberException("the game's launcher or explosion differs");
                harmony.Patch(init, postfix: new HarmonyMethod(typeof(Quantum), nameof(PayloadReady)));
                harmony.Patch(explosion, prefix: new HarmonyMethod(typeof(Quantum), nameof(Explosion)));
                classes = Array.ConvertAll(Classes, CustomShip.Find);
                Plugin.Log.LogInfo($"Quantum torpedoes: on for the {string.Join(" and ", Classes)}, damage x{Multiplier(Plugin.QuantumDamage.Value)}");
            }
            catch (Exception e) { classes = null; Plugin.Log.LogWarning("Quantum torpedoes: off: " + e.Message); }
        }

        /// <summary>The salvo sound, read once a quantum ship has been built (main thread).</summary>
        internal static void Update()
        {
            if (audioTried || !Active) return;
            audioTried = true;
            var path = Path.Combine(Path.Combine(BepInEx.Paths.BepInExRootPath, "ships"), "quantum.wav");
            if (!File.Exists(path)) return; // optional
            try
            {
                salvoClip = PcmWave.Read(File.ReadAllBytes(path)).Clip("Quantum torpedo salvo");
                Plugin.Log.LogInfo($"Quantum torpedoes: salvo sound {salvoClip.length:F2} s");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Quantum torpedoes: quantum.wav: " + e.Message); }
        }

        internal static float Multiplier(float value) => float.IsNaN(value) || float.IsInfinity(value) ? 1.5f : Mathf.Clamp(value, 1f, 3f);

        private static bool IsQuantumShip(SectorBody ship)
        {
            if (!(ship is PlayerSpacecraft player)) return false;
            var b = body.GetValue(player) as SpacecraftBody;
            if (b == null) return false;
            string name = b.name;
            foreach (var c in Classes) if (name.StartsWith("Body - " + c + " (", StringComparison.Ordinal)) return true;
            return false;
        }

        // Also runs when a pooled torpedo is fired again: the last shot's changes always come off first.
        private static void PayloadReady(LauncherPayload __instance)
        {
            if (!(__instance is LauncherTorpedo)) return;
            try
            {
                var state = __instance.GetComponent<QuantumVisual>();
                var from = source.GetValue(__instance) as SectorBody;
                bool quantum = Active && IsQuantumShip(from);
                if (state == null && !quantum) return;
                if (state == null) state = __instance.gameObject.AddComponent<QuantumVisual>();
                state.ResetShot();
                state.IsQuantum = quantum;
                if (!quantum) return;
                state.Tint();
                if (salvoClip != null && PvpPrototype.IsOwn(from as PlayerSpacecraft)) QuantumSalvo.Play(from, salvoClip);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Quantum torpedoes: launch: " + e.Message); }
        }

        // The explosion's damage, once, where the host works it out (before the result goes to everyone).
        private static void Explosion(object __0, object __1, ref float __3)
        {
            try
            {
                if (!(__1 is LauncherTorpedo torpedo)) return; // not mines, depth charges or ships blowing up
                var state = torpedo.GetComponent<QuantumVisual>();
                if (state != null && state.IsQuantum && ReferenceEquals(source.GetValue(torpedo), __0))
                    __3 *= Multiplier(Plugin.QuantumDamage.Value);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Quantum torpedoes: explosion: " + e.Message); }
        }
    }

    /// <summary>The salvo sound on our own bridge, at most once per clip length per ship.</summary>
    internal sealed class QuantumSalvo : MonoBehaviour
    {
        private AudioSource audioSource;
        private float until;

        internal static void Play(SectorBody ship, AudioClip clip)
        {
            var salvo = ship.GetComponent<QuantumSalvo>() ?? ship.gameObject.AddComponent<QuantumSalvo>();
            if (Time.unscaledTime < salvo.until) return;
            salvo.until = Time.unscaledTime + clip.length;
            if (salvo.audioSource == null)
            {
                salvo.audioSource = salvo.gameObject.AddComponent<AudioSource>();
                salvo.audioSource.playOnAwake = false;
                salvo.audioSource.spatialBlend = 0f; // on the bridge, as the game's own launch sounds
            }
            salvo.audioSource.PlayOneShot(clip, Mathf.Clamp01(Plugin.QuantumVolume.Value));
        }
    }

    /// <summary>A quantum torpedo's look: its particles and materials blue, a flare at it; put back when the torpedo is reused.</summary>
    internal sealed class QuantumVisual : MonoBehaviour
    {
        public bool IsQuantum;
        private sealed class ParticleState { public ParticleSystem System; public Color Color; public bool Lifetime, Speed; }
        private sealed class MaterialState { public Renderer Renderer; public Material[] Original, Copies; }
        private readonly List<ParticleState> particles = new List<ParticleState>();
        private readonly List<MaterialState> materials = new List<MaterialState>();
        private static readonly Color BlueWhite = new Color(0.08f, 0.3f, 1f, 1f);
        private GameObject flare;

        public void Tint()
        {
            foreach (var p in GetComponentsInChildren<ParticleSystem>(true))
            {
                particles.Add(new ParticleState { System = p, Color = p.startColor, Lifetime = p.colorOverLifetime.enabled, Speed = p.colorBySpeed.enabled });
                p.startColor = BlueWhite;
                var lifetime = p.colorOverLifetime; lifetime.enabled = false;
                var speed = p.colorBySpeed; speed.enabled = false;
            }
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                if (flare != null && r.transform.IsChildOf(flare.transform)) continue;
                var original = r.sharedMaterials;
                var copies = new Material[original.Length];
                for (int i = 0; i < copies.Length; i++)
                {
                    if (original[i] == null) continue;
                    copies[i] = new Material(original[i]);
                    foreach (var property in new[] { "_Color", "_TintColor", "_EmissionColor" })
                        if (copies[i].HasProperty(property))
                        {
                            var old = copies[i].GetColor(property);
                            copies[i].SetColor(property, new Color(BlueWhite.r, BlueWhite.g, BlueWhite.b, old.a));
                        }
                }
                materials.Add(new MaterialState { Renderer = r, Original = original, Copies = copies });
                r.sharedMaterials = copies;
            }
            flare = QuantumFlare.Create(transform);
        }

        private void LateUpdate()
        {
            if (flare == null) return;
            bool flying = false;
            foreach (var p in particles) if (p.System != null && p.System.isPlaying) { flying = true; break; }
            flare.SetActive(IsQuantum && flying);
        }

        public void ResetShot()
        {
            IsQuantum = false;
            if (flare != null) { flare.SetActive(false); Destroy(flare); flare = null; }
            foreach (var p in particles)
            {
                if (p.System == null) continue;
                p.System.startColor = p.Color;
                var lifetime = p.System.colorOverLifetime; lifetime.enabled = p.Lifetime;
                var speed = p.System.colorBySpeed; speed.enabled = p.Speed;
            }
            particles.Clear();
            foreach (var m in materials)
            {
                if (m.Renderer != null) m.Renderer.sharedMaterials = m.Original;
                foreach (var copy in m.Copies) if (copy != null) Destroy(copy);
            }
            materials.Clear();
        }

        private void OnDisable() => ResetShot();
        private void OnDestroy() => ResetShot();
    }

    /// <summary>A camera-facing additive star at a quantum torpedo, depth-tested; it follows the torpedo (and the skybox's scale).</summary>
    internal sealed class QuantumFlare : MonoBehaviour
    {
        private static Material material;
        private static Texture2D texture;
        private static Mesh mesh;

        public static GameObject Create(Transform projectile)
        {
            if (material == null)
            {
                var shader = Shader.Find("Particles/Additive");
                if (shader == null) return null;
                const int size = 128;
                texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "Quantum blue star flare", wrapMode = TextureWrapMode.Clamp };
                var pixels = new Color[size * size];
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float u = (x + 0.5f) / size * 2f - 1f, v = (y + 0.5f) / size * 2f - 1f;
                        float radius = Mathf.Sqrt(u * u + v * v), fade = Mathf.Clamp01(1f - radius);
                        float core = Mathf.Exp(-radius * radius * 110f);
                        float halo = 0.12f * Mathf.Exp(-radius * radius * 12f);
                        float horizontal = 0.65f * Mathf.Exp(-Mathf.Abs(v) * 100f) * fade * fade;
                        float vertical = 0.35f * Mathf.Exp(-Mathf.Abs(u) * 100f) * fade * fade;
                        float diagonal = 0.12f * Mathf.Exp(-Mathf.Abs(Mathf.Abs(u) - Mathf.Abs(v)) * 85f) * fade * fade;
                        float alpha = Mathf.Clamp01(core + halo + horizontal + vertical + diagonal) * fade;
                        pixels[y * size + x] = new Color(0.1f + core * 0.25f, 0.4f + core * 0.25f, 1f, alpha);
                    }
                texture.SetPixels(pixels);
                texture.Apply(false, true);
                DontDestroyOnLoad(texture);
                material = new Material(shader) { name = "Quantum additive lens flare", mainTexture = texture };
                material.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.65f));
                DontDestroyOnLoad(material);
                mesh = new Mesh { name = "Quantum flare billboard" };
                mesh.vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(-0.5f, 0.5f, 0) };
                mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
                mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
                mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                mesh.RecalculateBounds();
                DontDestroyOnLoad(mesh);
            }
            float diameter = 0f;
            float rootScale = Mathf.Max(0.00001f, Mathf.Abs(projectile.lossyScale.x));
            foreach (var p in projectile.GetComponentsInChildren<ParticleSystem>(true))
                diameter = Mathf.Max(diameter, p.startSize * Mathf.Abs(p.transform.lossyScale.x) / rootScale);
            if (diameter <= 0f) diameter = 1f;
            var glow = new GameObject("Quantum lens flare") { layer = projectile.gameObject.layer };
            glow.transform.SetParent(projectile, false);
            glow.transform.localScale = Vector3.one * diameter * 2.5f;
            glow.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = glow.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            glow.AddComponent<QuantumFlare>();
            return glow;
        }

        private void OnWillRenderObject()
        {
            var camera = Camera.current;
            if (camera != null) transform.rotation = camera.transform.rotation;
        }
    }
}
