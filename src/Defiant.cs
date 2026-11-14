using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using Sector;
using Sector.Spacecraft;
using Sector.Spacecraft.Subsystems;
using UnityEngine;

namespace StbcRevival
{
    // Changes are attached to the firing craft, never to the local room's ship selection.
    internal static class Defiant
    {
        private static readonly MethodInfo Body = AccessTools.Method(typeof(BaseSpacecraft), "get_body");
        private static AudioClip pulseClip;
        private static bool audioLoaded;
        private static bool pairing;
        private static Transform muzzle;
        private static bool visualOnly;
        private static BaseSpacecraft soundCraft;
        internal static readonly Color PulseColor = new Color(1f, 0.76f, 0.28f, 1f);

        internal static bool IsDefiant(BaseSpacecraft craft)
        {
            if (!(craft is PlayerSpacecraft)) return false;
            var body = Body.Invoke(craft, null) as SpacecraftBody;
            return body != null && body.name.StartsWith("Body - Defiant (", StringComparison.Ordinal);
        }
        private static BaseSpacecraft Owner(Component component)
        {
            if (component == null) return null;
            // The body/turrets can be reparented into simulation/skybox roots away from the craft.
            var turret = component as BeamTurret;
            if (turret != null)
            {
                var info = AccessTools.Field(typeof(BeamTurret), "DAIHMGBLNDH").GetValue(turret);
                return info == null ? null : AccessTools.Method(info.GetType(), "get_Craft").Invoke(info, null) as BaseSpacecraft;
            }
            var craft = component.GetComponentInParent<BaseSpacecraft>();
            if (craft != null) return craft;
            var getter = AccessTools.Method(component.GetType(), "get_facade");
            object facade = getter == null ? null : getter.Invoke(component, null);
            var getCraft = facade == null ? null : AccessTools.Method(facade.GetType(), "get_craft");
            return getCraft == null ? null : getCraft.Invoke(facade, null) as BaseSpacecraft;
        }
        private static DefiantState State(BaseSpacecraft craft)
        {
            return craft.GetComponent<DefiantState>() ?? craft.gameObject.AddComponent<DefiantState>();
        }
        private static void Hook(Harmony h, Type type, string method, string prefix = null, string postfix = null, string finalizer = null)
        {
            var target = AccessTools.Method(type, method);
            if (target == null) throw new MissingMethodException(type.FullName, method);
            h.Patch(target, prefix == null ? null : new HarmonyMethod(typeof(Defiant), prefix),
                postfix == null ? null : new HarmonyMethod(typeof(Defiant), postfix),
                finalizer: finalizer == null ? null : new HarmonyMethod(typeof(Defiant), finalizer));
        }
        internal static void Patch(Harmony unused)
        {
            var h = new Harmony("stbc.revival.defiant.systems");
            try
            {
                Hook(h, typeof(SimpleEngine), "init", postfix: nameof(EngineReady));
                Hook(h, typeof(PhaserGroup), "Init", prefix: nameof(PhasersBefore), postfix: nameof(PhasersReady));
                Hook(h, typeof(LauncherGroup), "Fire", prefix: nameof(LaunchPair));
                Hook(h, typeof(BeamTurret), "FireShortShot", prefix: nameof(FirePair));
                Hook(h, typeof(BeamTurretBolt), "FireShortShot", prefix: nameof(BoltBefore), postfix: nameof(BoltReady));
                Hook(h, typeof(BaseSpacecraft), "OnPhaserAttackResultsEvent", prefix: nameof(HitData));
                Hook(h, typeof(BeamWeaponClient), "IsTargetInCone", postfix: nameof(Cone));
                Hook(h, typeof(BeamWeaponClient), "BatchUpdate", prefix: nameof(SoundScope), finalizer: nameof(SoundScopeEnd));
                Hook(h, typeof(PlayerSpacecraftSoundManager), "StartPhaserFireSound", prefix: nameof(SoundBefore));
                Plugin.Log.LogInfo("Defiant systems v6: flight x3 yaw/x4 yaw acceleration, manual paired gold bursts, dual torpedoes; all hooks resolved");
            }
            catch (Exception e)
            {
                h.UnpatchSelf();
                Plugin.Log.LogError("Defiant systems disabled: " + e);
            }
        }
        internal static void Update()
        {
            if (audioLoaded) return;
            audioLoaded = true;
            try
            {
                var wave = DefiantWave.Read(File.ReadAllBytes(Path.Combine(Paths.BepInExRootPath, "ships/Defiant/pulse_phaser.wav")));
                pulseClip = AudioClip.Create("Defiant pulse salvo", wave.Samples.Length / wave.Channels, wave.Channels, wave.Rate, false);
                pulseClip.SetData(wave.Samples, 0);
                Plugin.Log.LogInfo("Defiant pulse audio loaded: " + pulseClip.length.ToString("F2") + " seconds");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Defiant audio: " + e.Message + "; retaining game sound"); }
        }
        private static void Scale(object obj, string field, float multiplier)
        {
            var f = AccessTools.Field(obj.GetType(), field);
            if (f == null || f.FieldType != typeof(float)) throw new MissingFieldException(obj.GetType().Name, field);
            f.SetValue(obj, (float)f.GetValue(obj) * multiplier);
        }
        private static void Set(object obj, string field, object value)
        {
            var f = AccessTools.Field(obj.GetType(), field);
            if (f == null) throw new MissingFieldException(obj.GetType().Name, field);
            f.SetValue(obj, value);
        }
        private static void EngineReady(SimpleEngine __instance, BaseSpacecraft __0)
        {
            if (!IsDefiant(__0)) return;
            var state = State(__0);
            if (state.EngineReady) return;
            var engine = __instance.GetComponent<SimpleEngineClient>();
            Scale(engine, "_maxYawVel", 3f);
            Scale(engine, "_maxYawAcc", 4f);
            Scale(engine, "_maxAcc", 1.6f);
            Scale(engine, "_maxReverseAcc", 1.6f);
            state.EngineReady = true;
            Plugin.Log.LogInfo("Defiant flight applied to " + __0.name + ": yaw x3, yaw acceleration x4, acceleration x1.6; mass and top speed retained");
        }
        private static void PhasersBefore(PhaserGroup __instance, BaseSpacecraft __0)
        {
            if (!IsDefiant(__0)) return;
            var state = State(__0);
            if (state.PhasersReady) return;
            // ScriptableObjects are shared by every Aegis-derived ship. Clone per ship before changing anything.
            foreach (string field in new[] { "_standardFiringMode", "_subsystemFiringMode" })
            {
                var f = AccessTools.Field(typeof(PhaserGroup), field);
                var original = (PhaserData)f.GetValue(__instance);
                var mode = UnityEngine.Object.Instantiate(original);
                mode.name = "Defiant " + original.name;
                mode.SetNetID((int)AccessTools.Method(typeof(PhaserData), "get_NetID").Invoke(original, null));
                Set(mode, "_burstFire", true);
                Set(mode, "_numberOfShotsInBurst", 1);
                Set(mode, "_numberOfVirtualShotsInBurst", 3);
                Set(mode, "_fireRate", 0.12f);
                Set(mode, "_timeBetweenPulses", 0.12f);
                Set(mode, "_firingArc", 50f);
                float speed = (float)AccessTools.Field(typeof(PhaserData), "_beamBoltSpeed").GetValue(mode);
                // Leave a visible gap between pulses even at the stock bolt speed.
                Set(mode, "_beamBoltLength", Mathf.Min(8f, speed * 0.06f));
                f.SetValue(__instance, mode);
                state.OwnedModes.Add(mode);
            }
            state.PhasersReady = true;
        }
        private static void PhasersReady(PhaserGroup __instance, BaseSpacecraft __0)
        {
            if (!IsDefiant(__0)) return;
            var state = State(__0);
            var body = (SpacecraftBody)Body.Invoke(__0, null);
            string[] names = { "Top Left Turret", "Top Right Turret", "Bottom Left Turret", "Bottom Right Turret" };
            var all = body.GetComponentsInChildren<Transform>(true);
            state.Muzzles = new Transform[4];
            for (int i = 0; i < names.Length; i++)
                foreach (var t in all) if (t.name == names[i]) { state.Muzzles[i] = t; break; }
            foreach (var t in state.Muzzles) if (t == null) throw new InvalidOperationException("Defiant cannon hardpoint missing");
            var weapon = (Component)AccessTools.Field(typeof(PhaserGroup), "_weapon").GetValue(__instance);
            var client = weapon.GetComponent<BeamWeaponClient>();
            Set(client, "_delayBetweenShots", 0.12f);
            Set(client, "_delayBetweenVollies", 0.28f);
            int cost = Math.Max(1, Mathf.CeilToInt((float)AccessTools.Field(typeof(BeamWeaponClient), "_maxEnergy").GetValue(client) / 8f));
            foreach (var mode in state.OwnedModes) Set(mode, "_energyPerPulse", cost);
            Plugin.Log.LogInfo("Defiant phasers: one manual burst per command, eight bursts per full bank, Aegis width, short separated pulse tails, +/-25 degree forward cone");
        }
        private static bool Forward(BaseSpacecraft craft, SectorBody target)
        {
            if (target == null) return false;
            return Vector3.Angle(craft.transform.forward, target.transform.position - craft.transform.position) <= 25f;
        }
        private static void Cone(BeamWeaponClient __instance, SectorBody __0, ref bool __result)
        {
            var craft = Owner(__instance);
            if (IsDefiant(craft)) __result = __result && Forward(craft, __0);
        }
        private static bool LaunchPair(LauncherGroup __instance, int __0)
        {
            var launchers = (LauncherWeapon[])AccessTools.Field(typeof(LauncherGroup), "_launchers").GetValue(__instance);
            if (launchers == null || launchers.Length < 2 || !IsDefiant(Owner(launchers[0]))) return true;
            // Never recurse through the group: GroupForward/Spread would otherwise duplicate whole salvos.
            if (Convert.ToInt32(__instance.Pattern) != 0 || __0 < 0 || __0 > 1) return true;
            if (!launchers[__0].CanFire()) return false;
            for (int i = 0; i < 2; i++)
            {
                if (!launchers[i].CanFire()) continue;
                AccessTools.Method(typeof(LauncherWeapon), "set_CurrentFireArcOffset").Invoke(launchers[i], new object[] { 0f });
                var target = (SectorBody)AccessTools.Method(typeof(LauncherWeapon), "get_Target").Invoke(launchers[i], null);
                launchers[i].Fire(target); // normal ownership, ammo, damage/disabled checks and RPC path
            }
            return false;
        }
        private static bool FirePair(BeamTurret __instance, Vector3 __0, PhaserData __1, object __2)
        {
            if (pairing) return true;
            var craft = Owner(__instance);
            if (!IsDefiant(craft)) return true;
            var target = (SectorBody)AccessTools.Method(typeof(BeamTurret), "get_Target").Invoke(__instance, null);
            if (!Forward(craft, target)) return false;
            var state = State(craft);
            if (state.Muzzles == null) return true;
            int pair = (state.PairIndex++ % 2) * 2;
            pairing = true;
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    muzzle = state.Muzzles[pair + i];
                    visualOnly = i == 1;
                    AccessTools.Method(typeof(BeamTurret), "FireShortShot").Invoke(__instance, new object[] { __0, __1, __2 });
                }
                state.PlaySound(pulseClip);
                if (state.PairIndex == 1) Plugin.Log.LogInfo("Defiant paired phaser burst started from model hardpoints");
            }
            finally { pairing = false; muzzle = null; visualOnly = false; }
            return false;
        }
        private static void BoltBefore(BeamTurretBolt __instance, SectorBody __0, ref Transform[] __1, ref BeamTurret.CNJOCMPEIHF __2)
        {
            var visual = __instance.GetComponent<DefiantBoltVisual>();
            if (visual != null) visual.Restore(); // pooled projectiles may subsequently belong to another ship
            if (!IsDefiant(__0 as BaseSpacecraft) || muzzle == null) return;
            __1 = new[] { muzzle };
            // Each bolt needs its own origin object. Reusing the turret's mutable origin moves in-flight shots.
            var origin = new BeamTurret.CNJOCMPEIHF();
            __2 = origin.UseEmitter(muzzle);
        }
        private static void BoltReady(BeamTurretBolt __instance, SectorBody __0)
        {
            if (!IsDefiant(__0 as BaseSpacecraft)) return;
            var visual = __instance.GetComponent<DefiantBoltVisual>() ?? __instance.gameObject.AddComponent<DefiantBoltVisual>();
            visual.Tint(__instance);
            if (muzzle != null)
            {
                var state = State((BaseSpacecraft)__0);
                int bit = visualOnly ? 2 : 1;
                if ((state.CheckedMuzzles & bit) == 0)
                {
                    var fx = AccessTools.Field(typeof(BeamTurretBolt), "MNNEECLMIPA").GetValue(__instance);
                    var emitters = fx == null ? null : AccessTools.Field(fx.GetType(), "OELCEMMNJNK").GetValue(fx) as Transform[];
                    var origin = (BeamTurret.CNJOCMPEIHF)AccessTools.Field(typeof(BeamTurretBolt), "BOLFFKLENOB").GetValue(__instance);
                    bool correct = emitters != null && emitters.Length == 1 && emitters[0] == muzzle && Vector3.Distance(origin.GetPosition(), muzzle.position) < 0.001f;
                    Plugin.Log.LogInfo("Defiant actual bolt origin: " + muzzle.name + " local=" + muzzle.localPosition + "; FX and ray match=" + correct + "; visualOnly=" + visualOnly);
                    state.CheckedMuzzles |= bit;
                }
            }
            // The right cannon mirrors the burst, but never sends a second damage result.
            if (visualOnly) AccessTools.Method(typeof(BeamTurretBolt), "SetHandler").Invoke(__instance, new object[] { null });
        }
        private static void HitData(SectorBody __0, ref PhaserData __3)
        {
            var craft = __0 as BaseSpacecraft;
            if (!IsDefiant(craft) || __3 == null) return;
            // This runs after the network ID has been resolved on each peer, including the authority.
            // One damage-bearing shot plus its visual twin; keep stock damage and use burst impact metadata.
            __3 = State(craft).SingleDamage(__3);
        }
        private static void SoundScope(BeamWeaponClient __instance, out BaseSpacecraft __state)
        {
            __state = soundCraft;
            soundCraft = Owner(__instance);
        }
        private static Exception SoundScopeEnd(Exception __exception, BaseSpacecraft __state)
        {
            soundCraft = __state;
            return __exception;
        }
        private static bool SoundBefore() { return pulseClip == null || !IsDefiant(soundCraft); }
    }

    internal sealed class DefiantState : MonoBehaviour
    {
        internal bool EngineReady, PhasersReady;
        internal Transform[] Muzzles;
        internal int PairIndex, CheckedMuzzles;
        internal readonly List<PhaserData> OwnedModes = new List<PhaserData>();
        private readonly Dictionary<PhaserData, PhaserData> damageModes = new Dictionary<PhaserData, PhaserData>();
        private AudioSource source;
        private float nextSound;
        internal PhaserData SingleDamage(PhaserData original)
        {
            PhaserData result;
            if (damageModes.TryGetValue(original, out result)) return result;
            result = UnityEngine.Object.Instantiate(original);
            // Stock pulse damage is retained. The visual twin has no hit handler.
            AccessTools.Field(typeof(PhaserData), "_burstFire").SetValue(result, true);
            AccessTools.Field(typeof(PhaserData), "_numberOfVirtualShotsInBurst").SetValue(result, 3);
            damageModes.Add(original, result);
            OwnedModes.Add(result);
            return result;
        }
        internal void PlaySound(AudioClip clip)
        {
            if (clip == null || Time.unscaledTime < nextSound) return;
            if (source == null)
            {
                source = gameObject.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.spatialBlend = 1f;
                source.minDistance = 5f;
                source.maxDistance = 250f;
                source.rolloffMode = AudioRolloffMode.Linear;
            }
            source.Stop();
            source.PlayOneShot(clip, 0.85f);
            nextSound = Time.unscaledTime + 0.25f;
        }
        private void OnDestroy() { foreach (var mode in OwnedModes) if (mode != null) Destroy(mode); }
    }

    internal sealed class DefiantBoltVisual : MonoBehaviour
    {
        private readonly Dictionary<Material, Dictionary<string, Color>> colors = new Dictionary<Material, Dictionary<string, Color>>();
        private readonly Dictionary<ParticleSystem, Color> particles = new Dictionary<ParticleSystem, Color>();
        private readonly Dictionary<Material, float> tails = new Dictionary<Material, float>();
        private readonly Dictionary<Material, Texture> textures = new Dictionary<Material, Texture>();
        private static readonly Dictionary<Texture, Texture2D> goldTextures = new Dictionary<Texture, Texture2D>();
        internal void Restore()
        {
            foreach (var entry in colors) if (entry.Key != null) foreach (var color in entry.Value) entry.Key.SetColor(color.Key, color.Value);
            foreach (var entry in particles) if (entry.Key != null) entry.Key.startColor = entry.Value;
            foreach (var entry in textures) if (entry.Key != null) entry.Key.SetTexture("_MainTex", entry.Value);
            foreach (var entry in tails) if (entry.Key != null) entry.Key.SetFloat("_TailLength", entry.Value);
            colors.Clear(); particles.Clear(); textures.Clear(); tails.Clear();
        }
        private static Texture2D GoldTexture(Texture original)
        {
            Texture2D result;
            if (goldTextures.TryGetValue(original, out result) && result != null) return result;
            var previous = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(original.width, original.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Texture2D copy = null;
            try
            {
                Graphics.Blit(original, rt);
                RenderTexture.active = rt;
                copy = new Texture2D(original.width, original.height, TextureFormat.RGBA32, true);
                copy.ReadPixels(new Rect(0, 0, original.width, original.height), 0, 0);
                var pixels = copy.GetPixels32();
                for (int i = 0; i < pixels.Length; i++)
                {
                    var p = pixels[i];
                    float intensity = Math.Max(p.r, Math.Max(p.g, p.b)) / 255f;
                    float core = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((intensity - 0.35f) / 0.6f));
                    var gold = Color.Lerp(new Color(1f, 0.55f, 0.07f), new Color(1f, 0.96f, 0.83f), core) * intensity;
                    gold.a = p.a / 255f;
                    pixels[i] = gold;
                }
                copy.name = "Defiant gold " + original.name;
                copy.wrapMode = original.wrapMode;
                copy.filterMode = original.filterMode;
                copy.anisoLevel = original.anisoLevel;
                copy.SetPixels32(pixels);
                copy.Apply(true, true);
                UnityEngine.Object.DontDestroyOnLoad(copy);
                goldTextures[original] = copy;
                Plugin.Log.LogInfo("Defiant gold effect texture ready: " + original.name + " -> " + copy.name);
                return copy;
            }
            catch { if (copy != null) UnityEngine.Object.Destroy(copy); throw; }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt); }
        }
        private void TintObject(Component root)
        {
            if (root == null) return;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                foreach (var material in renderer.materials)
                {
                    if (material == null || colors.ContainsKey(material)) continue;
                    var saved = new Dictionary<string, Color>();
                    if (material.HasProperty("_TailLength"))
                    {
                        tails[material] = material.GetFloat("_TailLength");
                        material.SetFloat("_TailLength", 6f);
                    }
                    bool recolored = material.HasProperty("_MainTex") && material.GetTexture("_MainTex") != null;
                    if (recolored)
                    {
                        var original = material.GetTexture("_MainTex");
                        textures[material] = original;
                        material.SetTexture("_MainTex", GoldTexture(original));
                    }
                    foreach (string property in new[] { "_Color", "_TintColor", "_EmissionColor", "_CoreColor", "_GlowColor" })
                    {
                        if (!material.HasProperty(property)) continue;
                        var old = material.GetColor(property);
                        saved[property] = old;
                        var tint = property == "_CoreColor" ? new Color(1f, 0.96f, 0.83f, old.a) :
                            property == "_GlowColor" ? new Color(1f, 0.58f, 0.12f, old.a) : Defiant.PulseColor;
                        if (recolored && (property == "_Color" || property == "_TintColor")) tint = Color.white;
                        tint.a = old.a;
                        material.SetColor(property, tint);
                    }
                    colors.Add(material, saved);
                }
            foreach (var p in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (particles.ContainsKey(p)) continue;
                particles[p] = p.startColor;
                p.startColor = Defiant.PulseColor;
            }
        }
        internal void Tint(BeamTurretBolt bolt)
        {
            TintObject(bolt);
            // Aegis shader-driven bolt FX and muzzle glows live outside the projectile's child hierarchy.
            var fx = AccessTools.Field(typeof(BeamTurretBolt), "MNNEECLMIPA").GetValue(bolt) as Component;
            TintObject(fx);
            if (fx != null)
            {
                var glows = AccessTools.Field(fx.GetType(), "_originGlow").GetValue(fx) as MeshRenderer[];
                if (glows != null) foreach (var glow in glows) TintObject(glow);
            }
        }
        private void OnDestroy() { Restore(); }
    }
}
