using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Sector;
using Sector.Spacecraft;
using Sector.Spacecraft.Subsystems;
using UnityEngine;

namespace SovereignQuantum
{
    [BepInPlugin("stbc.sovereign.quantum", "Sovereign Quantum Torpedoes", "0.1.3")]
    [BepInDependency("stbc.revival")]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance;
        private ConfigEntry<bool> enabledSetting;
        private ConfigEntry<float> damage;
        private ConfigEntry<float> soundVolume;
        private Harmony harmony;
        private AudioClip launchClip;
        private AudioSource launchAudio;
        private readonly Dictionary<int, float> nextSalvo = new Dictionary<int, float>();
        private static FieldInfo Source;
        private bool initialized;

        private void Awake()
        {
            Instance = this;
            enabledSetting = Config.Bind<bool>("Quantum", "Enabled", true, "Quantum torpedoes on Sovereign and Defiant player ships only. Restart after changes.");
            damage = Config.Bind<float>("Quantum", "DamageMultiplier", 1.5f, "Gameplay balance, not a canon figure. Clamped to 1..3. Host controls damage.");
            soundVolume = Config.Bind<float>("Audio", "SalvoVolume", 0.6f, "Additional quantum salvo sound, 0..1; 0 disables it. Standard game audio remains.");
        }

        private void Update()
        {
            // BepInEx enters Awake from MonoBehaviour's static constructor in this game.
            // Audio creation and game-type patching must wait for Unity's main-thread frame loop.
            if (initialized) return;
            initialized = true;
            if (!enabledSetting.Value) return;
            try
            {
                Source = AccessTools.Field(typeof(LauncherPayload), "DOJCBJGPHJL");
                MethodInfo init = AccessTools.DeclaredMethod(typeof(LauncherPayload), "LMOOIPLMILG", new Type[] { typeof(string) });
                MethodInfo explosion = AccessTools.DeclaredMethod(typeof(BaseCombatManager), "Server_Explosion");
                if (Source == null || Source.FieldType != typeof(SectorBody) || init == null || explosion == null ||
                    explosion.GetParameters().Length != 9 || explosion.GetParameters()[3].ParameterType != typeof(float))
                    throw new InvalidOperationException("Game hook signatures differ; plugin disabled.");
                harmony = new Harmony("stbc.sovereign.quantum");
                harmony.Patch(init, postfix: new HarmonyMethod(typeof(Plugin), "PayloadReady"));
                harmony.Patch(explosion, prefix: new HarmonyMethod(typeof(Plugin), "Explosion"));
                try
                {
                    string path = Path.Combine(Path.GetDirectoryName(Info.Location), "quantum.wav");
                    PcmWave wave = PcmWave.Read(File.ReadAllBytes(path));
                    launchClip = AudioClip.Create("Quantum torpedo salvo", wave.Samples.Length / wave.Channels, wave.Channels, wave.Rate, false);
                    launchClip.SetData(wave.Samples, 0);
                    launchAudio = gameObject.AddComponent<AudioSource>();
                    launchAudio.playOnAwake = false;
                    launchAudio.spatialBlend = 0f;
                }
                catch (Exception e) { Logger.LogWarning("Quantum sound unavailable: " + e.Message); }
                Logger.LogInfo("Quantum ready: Sovereign and Defiant, damage x" + QuantumPolicy.Multiplier(damage.Value) + "; sound " + (launchClip != null ? "loaded" : "unavailable"));
            }
            catch (Exception e)
            {
                if (harmony != null) harmony.UnpatchSelf();
                Logger.LogError("Quantum setup failed: " + e);
            }
        }

        internal static bool IsSovereign(SectorBody ship)
        {
            PlayerSpacecraft player = ship as PlayerSpacecraft;
            if (player == null) return false;
            // Inspect the firing ship's actual model, never the local crew's room/config selection.
            // Revival gives instantiated Sovereign bodies this exact prefix, including in PvP.
            SpacecraftBody body = player.KLENBCLCNKB;
            return body != null && QuantumPolicy.IsSovereignBody(true, body.name);
        }

        private static void PayloadReady(LauncherPayload __instance)
        {
            if (!(__instance is LauncherTorpedo)) return;
            try
            {
                SectorBody source = Source.GetValue(__instance) as SectorBody;
                QuantumVisual state = __instance.GetComponent<QuantumVisual>();
                bool quantum = IsSovereign(source);
                if (state == null && !quantum) return;
                if (state == null) state = __instance.gameObject.AddComponent<QuantumVisual>();
                // This callback also runs for pooled reuse; always remove the old shot's changes first.
                state.ResetShot();
                state.IsQuantum = quantum;
                if (!quantum) return;
                state.Tint();
                Instance.PlaySalvo(source);
                Instance.Logger.LogDebug("Quantum launched by " + source.name);
            }
            catch (Exception e) { Instance.Logger.LogWarning("Quantum launch: " + e.Message); }
        }

        private static void Explosion(object __0, object __1, ref float __3)
        {
            LauncherTorpedo projectile = __1 as LauncherTorpedo;
            if (projectile == null) return; // excludes mines, depth charges and ship explosions
            QuantumVisual state = projectile.GetComponent<QuantumVisual>();
            // Damage is changed once, at the authoritative explosion, before its normal RPC distribution.
            if (state != null && state.IsQuantum && ReferenceEquals(Source.GetValue(projectile), __0))
            {
                __3 *= QuantumPolicy.Multiplier(Instance.damage.Value);
                Instance.Logger.LogDebug("Quantum explosion damage: " + __3);
            }
        }

        private void PlaySalvo(SectorBody source)
        {
            if (launchClip == null || launchAudio == null) return;
            int id = source.GetInstanceID();
            float until;
            if (nextSalvo.TryGetValue(id, out until) && Time.unscaledTime < until) return;
            if (nextSalvo.Count > 64) nextSalvo.Clear();
            nextSalvo[id] = Time.unscaledTime + launchClip.length;
            float volume = soundVolume.Value;
            if (float.IsNaN(volume) || float.IsInfinity(volume)) volume = 0.6f;
            launchAudio.PlayOneShot(launchClip, Mathf.Clamp01(volume));
        }

        private void OnDestroy()
        {
            if (harmony != null) harmony.UnpatchSelf();
            if (launchAudio != null) Destroy(launchAudio);
            if (launchClip != null) Destroy(launchClip);
            QuantumFlare.ReleaseAssets();
            Instance = null;
        }
    }

    public sealed class QuantumVisual : MonoBehaviour
    {
        public bool IsQuantum;
        private sealed class ParticleState
        {
            public ParticleSystem System;
            public Color Color;
            public bool Lifetime, Speed;
        }
        private sealed class MaterialState
        {
            public Renderer Renderer;
            public Material[] Original, Copies;
        }
        private readonly List<ParticleState> particles = new List<ParticleState>();
        private readonly List<MaterialState> materials = new List<MaterialState>();
        private static readonly Color BlueWhite = new Color(0.08f, 0.3f, 1f, 1f);
        private GameObject flare;

        public void Tint()
        {
            foreach (ParticleSystem p in GetComponentsInChildren<ParticleSystem>(true))
            {
                particles.Add(new ParticleState { System = p, Color = p.startColor, Lifetime = p.colorOverLifetime.enabled, Speed = p.colorBySpeed.enabled });
                p.startColor = BlueWhite;
                var lifetime = p.colorOverLifetime; lifetime.enabled = false;
                var speed = p.colorBySpeed; speed.enabled = false;
            }
            foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
            {
                Material[] original = r.sharedMaterials;
                Material[] copies = new Material[original.Length];
                for (int i = 0; i < copies.Length; i++)
                {
                    if (original[i] == null) continue;
                    copies[i] = new Material(original[i]);
                    foreach (string property in new string[] { "_Color", "_TintColor", "_EmissionColor" })
                        if (copies[i].HasProperty(property))
                        {
                            Color old = copies[i].GetColor(property);
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
            foreach (ParticleState p in particles)
                if (p.System != null && p.System.isPlaying) { flying = true; break; }
            flare.SetActive(IsQuantum && flying);
        }

        public void ResetShot()
        {
            IsQuantum = false;
            if (flare != null) { flare.SetActive(false); Destroy(flare); flare = null; }
            foreach (ParticleState p in particles)
            {
                if (p.System == null) continue;
                p.System.startColor = p.Color;
                var lifetime = p.System.colorOverLifetime; lifetime.enabled = p.Lifetime;
                var speed = p.System.colorBySpeed; speed.enabled = p.Speed;
            }
            particles.Clear();
            foreach (MaterialState m in materials)
            {
                if (m.Renderer != null) m.Renderer.sharedMaterials = m.Original;
                foreach (Material copy in m.Copies) if (copy != null) Destroy(copy);
            }
            materials.Clear();
        }

        private void OnDisable() { ResetShot(); }
        private void OnDestroy() { ResetShot(); }
    }

    // A camera-facing additive star at the projectile, depth-tested by the particle shader.
    // It follows the projectile's transform (including the game's skybox scale), not the screen.
    public sealed class QuantumFlare : MonoBehaviour
    {
        private static Material material;
        private static Texture2D texture;
        private static Mesh mesh;

        public static GameObject Create(Transform projectile)
        {
            if (material == null)
            {
                Shader shader = Shader.Find("Particles/Additive");
                if (shader == null) return null;
                const int size = 128;
                texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
                texture.name = "Quantum blue star flare";
                texture.wrapMode = TextureWrapMode.Clamp;
                Color[] pixels = new Color[size * size];
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float u = (x + 0.5f) / size * 2f - 1f;
                        float v = (y + 0.5f) / size * 2f - 1f;
                        float radius = Mathf.Sqrt(u * u + v * v);
                        float fade = Mathf.Clamp01(1f - radius);
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
                material = new Material(shader);
                material.name = "Quantum additive lens flare";
                material.mainTexture = texture;
                material.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.65f));
                mesh = new Mesh();
                mesh.name = "Quantum flare billboard";
                mesh.vertices = new Vector3[] { new Vector3(-0.5f,-0.5f,0), new Vector3(0.5f,-0.5f,0), new Vector3(0.5f,0.5f,0), new Vector3(-0.5f,0.5f,0) };
                mesh.uv = new Vector2[] { new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1) };
                mesh.colors = new Color[] { Color.white, Color.white, Color.white, Color.white };
                mesh.triangles = new int[] { 0, 1, 2, 0, 2, 3 };
                mesh.RecalculateBounds();
            }
            float diameter = 0f;
            float rootScale = Mathf.Max(0.00001f, Mathf.Abs(projectile.lossyScale.x));
            foreach (ParticleSystem p in projectile.GetComponentsInChildren<ParticleSystem>(true))
                diameter = Mathf.Max(diameter, p.startSize * Mathf.Abs(p.transform.lossyScale.x) / rootScale);
            if (diameter <= 0f) diameter = 1f;
            GameObject glow = new GameObject("Quantum lens flare");
            glow.layer = projectile.gameObject.layer;
            glow.transform.SetParent(projectile, false);
            glow.transform.localScale = Vector3.one * diameter * 2.5f;
            glow.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = glow.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            glow.AddComponent<QuantumFlare>();
            return glow;
        }

        private void OnWillRenderObject()
        {
            Camera camera = Camera.current;
            if (camera != null) transform.rotation = camera.transform.rotation;
        }

        public static void ReleaseAssets()
        {
            if (material != null) Destroy(material);
            if (texture != null) Destroy(texture);
            if (mesh != null) Destroy(mesh);
            material = null; texture = null; mesh = null;
        }
    }
}
