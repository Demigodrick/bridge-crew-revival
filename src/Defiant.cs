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
    /// The Defiant's systems (only on a player ship whose body is the Defiant's, never by the room's choice alone):
    /// quicker turning and acceleration, pulse cannons (one command, a short gold burst from a pair of the cannons in
    /// its nacelle roots, upper and lower pairs in turn; the second cannon's pulses are for show, so the damage and
    /// energy are one burst's), a +/-25 degree forward cone, and both forward tubes on a single torpedo command.
    ///
    /// These hooks sit in the game's weapons and engines for every ship, so each one returns at once unless a
    /// Defiant body has been built on this PC, the game members they use are all looked up once (any missing: no
    /// hooks at all), and any failure inside one is logged once and leaves the game's own behaviour.
    /// </summary>
    internal static class Defiant
    {
        private const string ClassName = "Defiant";
        internal static readonly Color PulseColor = new Color(1f, 0.76f, 0.28f, 1f);
        private static readonly string[] CannonNames = { "Top Left Turret", "Top Right Turret", "Bottom Left Turret", "Bottom Right Turret" };

        private static CustomShip shipClass;
        private static AudioClip pulseClip;
        private static bool audioLoaded;
        private static bool pairing, visualOnly;
        private static Transform muzzle;
        private static BaseSpacecraft soundCraft;

        // ---- the game's members, looked up once ----

        private static MethodInfo getBody, turretInfoCraft, turretTarget, turretFireShortShot, launcherTarget, launcherArcOffset, boltSetHandler, phaserNetId;
        private static FieldInfo turretInfo, groupLaunchers, groupStandard, groupSubsystem, groupWeapon, boltFx, fxGlows, clientMaxEnergy;
        private static FieldInfo burstFire, shotsInBurst, virtualShots, fireRate, timeBetweenPulses, firingArc, boltSpeed, boltLength, energyPerPulse;
        private static FieldInfo clientShotDelay, clientVolleyDelay, maxYawVel, maxYawAcc, maxAcc, maxReverseAcc;

        private static FieldInfo Field(Type type, string name)
        {
            var f = AccessTools.Field(type, name);
            if (f == null) throw new MissingFieldException(type.FullName, name);
            return f;
        }

        private static MethodInfo Method(Type type, string name)
        {
            var m = AccessTools.Method(type, name);
            if (m == null) throw new MissingMethodException(type.FullName, name);
            return m;
        }

        private static void Resolve()
        {
            getBody = Method(typeof(BaseSpacecraft), "get_body");
            turretInfo = Field(typeof(BeamTurret), "DAIHMGBLNDH");
            turretInfoCraft = Method(turretInfo.FieldType, "get_Craft");
            turretTarget = Method(typeof(BeamTurret), "get_Target");
            turretFireShortShot = Method(typeof(BeamTurret), "FireShortShot");
            groupLaunchers = Field(typeof(LauncherGroup), "_launchers");
            launcherTarget = Method(typeof(LauncherWeapon), "get_Target");
            launcherArcOffset = Method(typeof(LauncherWeapon), "set_CurrentFireArcOffset");
            groupStandard = Field(typeof(PhaserGroup), "_standardFiringMode");
            groupSubsystem = Field(typeof(PhaserGroup), "_subsystemFiringMode");
            groupWeapon = Field(typeof(PhaserGroup), "_weapon");
            phaserNetId = Method(typeof(PhaserData), "get_NetID");
            boltFx = Field(typeof(BeamTurretBolt), "MNNEECLMIPA");
            boltSetHandler = Method(typeof(BeamTurretBolt), "SetHandler");
            fxGlows = Field(boltFx.FieldType, "_originGlow");
            burstFire = Field(typeof(PhaserData), "_burstFire");
            shotsInBurst = Field(typeof(PhaserData), "_numberOfShotsInBurst");
            virtualShots = Field(typeof(PhaserData), "_numberOfVirtualShotsInBurst");
            fireRate = Field(typeof(PhaserData), "_fireRate");
            timeBetweenPulses = Field(typeof(PhaserData), "_timeBetweenPulses");
            firingArc = Field(typeof(PhaserData), "_firingArc");
            boltSpeed = Field(typeof(PhaserData), "_beamBoltSpeed");
            boltLength = Field(typeof(PhaserData), "_beamBoltLength");
            energyPerPulse = Field(typeof(PhaserData), "_energyPerPulse");
            clientShotDelay = Field(typeof(BeamWeaponClient), "_delayBetweenShots");
            clientVolleyDelay = Field(typeof(BeamWeaponClient), "_delayBetweenVollies");
            clientMaxEnergy = Field(typeof(BeamWeaponClient), "_maxEnergy");
            maxYawVel = Field(typeof(SimpleEngineClient), "_maxYawVel");
            maxYawAcc = Field(typeof(SimpleEngineClient), "_maxYawAcc");
            maxAcc = Field(typeof(SimpleEngineClient), "_maxAcc");
            maxReverseAcc = Field(typeof(SimpleEngineClient), "_maxReverseAcc");
            foreach (var f in new[] { maxYawVel, maxYawAcc, maxAcc, maxReverseAcc, fireRate, timeBetweenPulses, firingArc, boltSpeed, boltLength, clientShotDelay, clientVolleyDelay })
                if (f.FieldType != typeof(float)) throw new MissingFieldException(f.DeclaringType.Name, f.Name + " (not a float)");
        }

        // ---- which ships ----

        /// <summary>A Defiant body has been built on this PC: no ship can be one otherwise (every hook's first check).</summary>
        private static bool Active => shipClass != null && shipClass.HasBody;

        private static readonly Dictionary<int, bool> isDefiant = new Dictionary<int, bool>();
        private static readonly Dictionary<int, BaseSpacecraft> owners = new Dictionary<int, BaseSpacecraft>();
        private static int cachedScene = -1;

        /// <summary>The caches below hold scene objects: emptied when the scene changes (and if they ever grow large).</summary>
        private static void CheckCaches()
        {
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetHashCode(); // its handle: no string each frame
            if (scene != cachedScene || isDefiant.Count > 512 || owners.Count > 4096) { cachedScene = scene; isDefiant.Clear(); owners.Clear(); }
        }

        internal static bool IsDefiant(BaseSpacecraft craft)
        {
            if (!Active || !(craft is PlayerSpacecraft)) return false;
            int id = craft.GetInstanceID();
            if (isDefiant.TryGetValue(id, out var known)) return known;
            var body = getBody.Invoke(craft, null) as SpacecraftBody;
            if (body == null) return false; // not set up yet: asked again later
            bool yes = body.name.StartsWith("Body - " + ClassName + " (", StringComparison.Ordinal);
            isDefiant[id] = yes;
            return yes;
        }

        /// <summary>The ship a weapon part belongs to (its body and turrets can be moved under the simulation's or the skybox's roots).</summary>
        private static BaseSpacecraft Owner(Component component)
        {
            if (component == null) return null;
            int id = component.GetInstanceID();
            if (owners.TryGetValue(id, out var known) && known != null) return known;
            BaseSpacecraft craft = null;
            if (component is BeamTurret turret)
            {
                var info = turretInfo.GetValue(turret);
                craft = info == null ? null : turretInfoCraft.Invoke(info, null) as BaseSpacecraft;
            }
            if (craft == null) craft = component.GetComponentInParent<BaseSpacecraft>();
            if (craft == null)
            {
                var getter = AccessTools.Method(component.GetType(), "get_facade");
                object facade = getter == null ? null : getter.Invoke(component, null);
                var getCraft = facade == null ? null : AccessTools.Method(facade.GetType(), "get_craft");
                craft = getCraft == null ? null : getCraft.Invoke(facade, null) as BaseSpacecraft;
            }
            if (craft != null) owners[id] = craft;
            return craft;
        }

        private static DefiantState State(BaseSpacecraft craft) => craft.GetComponent<DefiantState>() ?? craft.gameObject.AddComponent<DefiantState>();

        private static readonly HashSet<string> failed = new HashSet<string>();
        private static void Failed(string hook, Exception e)
        {
            if (failed.Add(hook)) Plugin.Log.LogWarning($"Defiant: {hook}: {e}; the game's own behaviour there");
        }

        // ---- set up ----

        private static readonly string[] HookNames = { nameof(EngineReady), nameof(PhasersBefore), nameof(PhasersReady), nameof(LaunchPair), nameof(FirePair),
            nameof(BoltBefore), nameof(BoltReady), nameof(HitData), nameof(Cone), nameof(SoundScope), nameof(SoundScopeEnd), nameof(SoundBefore) };

        private static void Hook(Harmony h, Type type, string method, string prefix = null, string postfix = null, string finalizer = null)
        {
            var target = Method(type, method);
            h.Patch(target, prefix == null ? null : new HarmonyMethod(typeof(Defiant), prefix),
                postfix == null ? null : new HarmonyMethod(typeof(Defiant), postfix),
                finalizer: finalizer == null ? null : new HarmonyMethod(typeof(Defiant), finalizer));
        }

        internal static void Patch(Harmony harmony)
        {
            shipClass = CustomShip.Find(ClassName);
            try
            {
                Resolve();
                Hook(harmony, typeof(SimpleEngine), "init", postfix: nameof(EngineReady));
                Hook(harmony, typeof(PhaserGroup), "Init", prefix: nameof(PhasersBefore), postfix: nameof(PhasersReady));
                Hook(harmony, typeof(LauncherGroup), "Fire", prefix: nameof(LaunchPair));
                Hook(harmony, typeof(BeamTurret), "FireShortShot", prefix: nameof(FirePair));
                Hook(harmony, typeof(BeamTurretBolt), "FireShortShot", prefix: nameof(BoltBefore), postfix: nameof(BoltReady));
                Hook(harmony, typeof(BaseSpacecraft), "OnPhaserAttackResultsEvent", prefix: nameof(HitData));
                Hook(harmony, typeof(BeamWeaponClient), "IsTargetInCone", postfix: nameof(Cone));
                Hook(harmony, typeof(BeamWeaponClient), "BatchUpdate", prefix: nameof(SoundScope), finalizer: nameof(SoundScopeEnd));
                Hook(harmony, typeof(PlayerSpacecraftSoundManager), "StartPhaserFireSound", prefix: nameof(SoundBefore));
                Plugin.Log.LogInfo("Defiant: systems hooked (turning, pulse cannons, paired torpedoes)");
            }
            catch (Exception e)
            {
                // Some of the hooks may be in already: all of ours out again, so it's all or nothing.
                try
                {
                    foreach (var m in new List<MethodBase>(harmony.GetPatchedMethods()))
                        foreach (var name in HookNames) harmony.Unpatch(m, AccessTools.Method(typeof(Defiant), name));
                }
                catch { }
                shipClass = null; // never Active
                Plugin.Log.LogWarning("Defiant: its systems are off (the game's differ): " + e.Message);
            }
        }

        internal static void Update()
        {
            CheckCaches();
            if (audioLoaded || !Active) return;
            audioLoaded = true;
            try
            {
                pulseClip = PcmWave.Read(File.ReadAllBytes(Path.Combine(shipClass.Folder, "pulse_phaser.wav"))).Clip("Defiant pulse salvo");
                Plugin.Log.LogInfo($"Defiant: pulse sound {pulseClip.length:F2} s");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Defiant: pulse sound: " + e.Message + "; the game's phaser sound"); }
        }

        // ---- engines ----

        private static void EngineReady(SimpleEngine __instance, BaseSpacecraft __0)
        {
            try
            {
                if (!IsDefiant(__0)) return;
                var state = State(__0);
                if (state.EngineReady) return;
                var engine = __instance.GetComponent<SimpleEngineClient>();
                if (engine == null) return;
                void Scale(FieldInfo f, float by) => f.SetValue(engine, (float)f.GetValue(engine) * by);
                Scale(maxYawVel, 3f); Scale(maxYawAcc, 4f); Scale(maxAcc, 1.6f); Scale(maxReverseAcc, 1.6f);
                state.EngineReady = true;
                Plugin.Log.LogInfo($"Defiant: {__0.name} turns 3x as fast (4x the turning acceleration) and accelerates 1.6x; top speed, mass, hull and shields as they were");
            }
            catch (Exception e) { Failed("engines", e); }
        }

        // ---- phasers ----

        private static void PhasersBefore(PhaserGroup __instance, BaseSpacecraft __0)
        {
            try
            {
                if (!IsDefiant(__0)) return;
                var state = State(__0);
                if (state.PhasersReady) return;
                // The firing modes are assets shared by every Aegis-built ship: copied for this one before any change.
                var modes = new List<KeyValuePair<FieldInfo, PhaserData>>();
                foreach (var f in new[] { groupStandard, groupSubsystem })
                {
                    var original = (PhaserData)f.GetValue(__instance);
                    if (original == null) { foreach (var kv in modes) UnityEngine.Object.Destroy(kv.Value); return; }
                    var mode = UnityEngine.Object.Instantiate(original);
                    mode.name = "Defiant " + original.name;
                    modes.Add(new KeyValuePair<FieldInfo, PhaserData>(f, mode));
                    mode.SetNetID((int)phaserNetId.Invoke(original, null));
                    burstFire.SetValue(mode, true);
                    shotsInBurst.SetValue(mode, 1);
                    virtualShots.SetValue(mode, 3);
                    fireRate.SetValue(mode, 0.12f);
                    timeBetweenPulses.SetValue(mode, 0.12f);
                    firingArc.SetValue(mode, 50f);
                    // A visible gap between the pulses, even at the stock bolt speed.
                    boltLength.SetValue(mode, Mathf.Min(8f, (float)boltSpeed.GetValue(mode) * 0.06f));
                }
                // Only once every copy is made: a failure part way leaves the ship's phasers as they were.
                foreach (var kv in modes) { kv.Key.SetValue(__instance, kv.Value); state.OwnedModes.Add(kv.Value); }
                state.PhasersReady = true;
            }
            catch (Exception e) { Failed("phasers", e); }
        }

        private static void PhasersReady(PhaserGroup __instance, BaseSpacecraft __0)
        {
            try
            {
                if (!IsDefiant(__0)) return;
                var state = State(__0);
                if (!state.PhasersReady || state.Muzzles != null) return;
                var body = (SpacecraftBody)getBody.Invoke(__0, null);
                var all = body.GetComponentsInChildren<Transform>(true);
                var muzzles = new Transform[CannonNames.Length];
                for (int i = 0; i < CannonNames.Length; i++)
                    foreach (var t in all) if (t.name == CannonNames[i]) { muzzles[i] = t; break; }
                if (Array.IndexOf(muzzles, null) >= 0) { Plugin.Log.LogWarning("Defiant: a cannon's hardpoint is missing; single shots from its turrets"); return; }
                var weapon = groupWeapon.GetValue(__instance) as Component;
                var client = weapon != null ? weapon.GetComponent<BeamWeaponClient>() : null;
                if (client == null) return;
                clientShotDelay.SetValue(client, 0.12f);
                clientVolleyDelay.SetValue(client, 0.28f);
                int cost = Math.Max(1, Mathf.CeilToInt(Convert.ToSingle(clientMaxEnergy.GetValue(client)) / 8f));
                foreach (var mode in state.OwnedModes) energyPerPulse.SetValue(mode, Convert.ChangeType(cost, energyPerPulse.FieldType));
                state.Muzzles = muzzles; // the pairs fire only once everything is set
                Plugin.Log.LogInfo("Defiant: pulse cannons, one burst per command, eight to a full bank, +/-25 degrees ahead");
            }
            catch (Exception e) { Failed("phaser hardpoints", e); }
        }

        private static bool Forward(BaseSpacecraft craft, SectorBody target) =>
            target != null && Vector3.Angle(craft.transform.forward, target.transform.position - craft.transform.position) <= 25f;

        private static void Cone(BeamWeaponClient __instance, SectorBody __0, ref bool __result)
        {
            if (!__result || !Active) return;
            try
            {
                var craft = Owner(__instance);
                if (IsDefiant(craft)) __result = Forward(craft, __0);
            }
            catch (Exception e) { Failed("firing cone", e); }
        }

        private static bool FirePair(BeamTurret __instance, Vector3 __0, PhaserData __1, object __2)
        {
            if (pairing || !Active) return true;
            int fired = 0;
            try
            {
                var craft = Owner(__instance);
                if (!IsDefiant(craft)) return true;
                var state = State(craft);
                if (state.Muzzles == null) return true;
                if (!Forward(craft, turretTarget.Invoke(__instance, null) as SectorBody)) return false;
                int pair = (state.PairIndex++ % 2) * 2;
                pairing = true;
                for (int i = 0; i < 2; i++)
                {
                    muzzle = state.Muzzles[pair + i];
                    visualOnly = i == 1;
                    turretFireShortShot.Invoke(__instance, new object[] { __0, __1, __2 });
                    fired++;
                }
                if (PvpPrototype.IsOwn(craft as PlayerSpacecraft)) state.PlaySound(pulseClip); // the game's phaser sound is our own ship's only
                return false;
            }
            catch (Exception e)
            {
                Failed("pulse cannons", e);
                return fired == 0; // nothing went: the game's own shot instead
            }
            finally { pairing = false; muzzle = null; visualOnly = false; }
        }

        private static void BoltBefore(BeamTurretBolt __instance, SectorBody __0, ref Transform[] __1, ref BeamTurret.CNJOCMPEIHF __2)
        {
            if (!Active) return;
            try
            {
                var visual = __instance.GetComponent<DefiantBoltVisual>();
                if (visual != null) visual.Restore(); // a pooled bolt may be another ship's now
                if (muzzle == null || !IsDefiant(__0 as BaseSpacecraft)) return;
                __1 = new[] { muzzle };
                // Each bolt its own origin: the turret's is moved for the next shot while this one flies.
                __2 = new BeamTurret.CNJOCMPEIHF().UseEmitter(muzzle);
            }
            catch (Exception e) { Failed("pulse origin", e); }
        }

        private static void BoltReady(BeamTurretBolt __instance, SectorBody __0)
        {
            if (!Active) return;
            try
            {
                if (!IsDefiant(__0 as BaseSpacecraft)) return;
                var visual = __instance.GetComponent<DefiantBoltVisual>() ?? __instance.gameObject.AddComponent<DefiantBoltVisual>();
                visual.Tint(__instance, boltFx, fxGlows);
                // The second cannon mirrors the burst but never sends a hit of its own.
                if (visualOnly) boltSetHandler.Invoke(__instance, new object[] { null });
            }
            catch (Exception e) { Failed("pulse look", e); }
        }

        private static void HitData(SectorBody __0, ref PhaserData __3)
        {
            if (!Active || __3 == null) return;
            try
            {
                var craft = __0 as BaseSpacecraft;
                // On every peer, once the network ID is resolved: the burst's hit, stock damage spread over its pulses.
                if (IsDefiant(craft)) __3 = State(craft).SingleDamage(__3, burstFire, virtualShots);
            }
            catch (Exception e) { Failed("hit", e); }
        }

        // ---- sound: the cannons' own instead of the game's phaser sound ----

        private static void SoundScope(BeamWeaponClient __instance, out BaseSpacecraft __state)
        {
            __state = soundCraft;
            if (!Active || pulseClip == null) { soundCraft = null; return; }
            try { soundCraft = Owner(__instance); }
            catch (Exception e) { soundCraft = null; Failed("sound", e); }
        }

        private static Exception SoundScopeEnd(Exception __exception, BaseSpacecraft __state)
        {
            soundCraft = __state;
            return __exception;
        }

        private static bool SoundBefore()
        {
            try { return soundCraft == null || pulseClip == null || !IsDefiant(soundCraft); }
            catch { return true; }
        }

        // ---- torpedoes: both forward tubes on a single shot ----

        private static bool LaunchPair(LauncherGroup __instance, int __0)
        {
            if (!Active) return true;
            int fired = 0;
            try
            {
                var launchers = groupLaunchers.GetValue(__instance) as LauncherWeapon[];
                if (launchers == null || launchers.Length < 2 || !IsDefiant(Owner(launchers[0]))) return true;
                // Single shots only (spread and forward salvos are the game's), and never through the group again.
                if (Convert.ToInt32(__instance.Pattern) != 0 || __0 < 0 || __0 > 1) return true;
                if (!launchers[__0].CanFire()) return false;
                for (int i = 0; i < 2; i++)
                {
                    if (!launchers[i].CanFire()) continue;
                    launcherArcOffset.Invoke(launchers[i], new object[] { 0f });
                    launchers[i].Fire(launcherTarget.Invoke(launchers[i], null) as SectorBody); // its own ammo, checks and network path
                    fired++;
                }
                return false;
            }
            catch (Exception e)
            {
                Failed("paired torpedoes", e);
                return fired == 0;
            }
        }
    }

    internal sealed class DefiantState : MonoBehaviour
    {
        internal bool EngineReady, PhasersReady;
        internal Transform[] Muzzles;
        internal int PairIndex;
        internal readonly List<PhaserData> OwnedModes = new List<PhaserData>();
        private readonly Dictionary<PhaserData, PhaserData> damageModes = new Dictionary<PhaserData, PhaserData>();
        private AudioSource source;
        private float nextSound;

        internal PhaserData SingleDamage(PhaserData original, FieldInfo burstFire, FieldInfo virtualShots)
        {
            if (damageModes.TryGetValue(original, out var result) && result != null) return result;
            result = Instantiate(original);
            // Stock damage, spread by the game over the burst's three pulses (the second cannon's are for show).
            burstFire.SetValue(result, true);
            virtualShots.SetValue(result, 3);
            damageModes[original] = result;
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
                source.spatialBlend = 0f; // on the bridge, as the game's phaser sound it replaces
            }
            source.Stop();
            source.PlayOneShot(clip, 0.85f);
            nextSound = Time.unscaledTime + 0.25f;
        }

        private void OnDestroy() { foreach (var mode in OwnedModes) if (mode != null) Destroy(mode); }
    }

    /// <summary>A pulse bolt (and its effects) in gold, put back as it was when the pooled bolt is fired again.</summary>
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

        /// <summary>The effect's texture in gold (white-hot in the middle), made once per texture.</summary>
        private static Texture2D GoldTexture(Texture original)
        {
            if (goldTextures.TryGetValue(original, out var result) && result != null) return result;
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
                DontDestroyOnLoad(copy);
                goldTextures[original] = copy;
                return copy;
            }
            catch { if (copy != null) Destroy(copy); throw; }
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
                        var tint = property == "_CoreColor" ? new Color(1f, 0.96f, 0.83f) :
                            property == "_GlowColor" ? new Color(1f, 0.58f, 0.12f) : Defiant.PulseColor;
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

        internal void Tint(BeamTurretBolt bolt, FieldInfo boltFx, FieldInfo fxGlows)
        {
            TintObject(bolt);
            // The Aegis's bolt effect and muzzle glows sit outside the bolt's own children.
            var fx = boltFx.GetValue(bolt) as Component;
            TintObject(fx);
            if (fx != null && fxGlows.GetValue(fx) is MeshRenderer[] glows) foreach (var glow in glows) TintObject(glow);
        }

        private void OnDestroy() { Restore(); }
    }
}
