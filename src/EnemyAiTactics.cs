using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// Enemy ships fly better (setting EnemyAI.FlyBetter; host only, where the AI runs). tools/xref/ai/movement.md
    /// (branch ai-map) has the game's movement model these change.
    ///
    /// - Throttle: the game flew every leg at the speed its tree asked for whatever the heading, so with turning circles of
    ///   1-3 km ships overshot and circled. Now the speed drops with the angle still to turn (down to MinTurnThrottle
    ///   when facing away), and, outside combat, it eases off near the nav point it is flying to. Done around
    ///   AgentShip.UpdateMoveVelocity (MHHGGHHKMKN) by scaling the blackboard's DesiredSpeed for that one call.
    /// - Turning: AgentShip.UpdateFacing (HICMEIBEAKI) turns the nose a fraction of the way each call, and is called by a
    ///   round robin, so the more ships there were the slower each one turned; and a stopped ship couldn't turn at all.
    ///   Now each call turns as much as the ship's own turn curve would at one call a physics step, for the time since its
    ///   last call; stopped ships turn too. (Borg cubes and alarm buoys have their own.)
    /// - Keeping apart: no ship avoided other ships, and the collision rays ignore the ship's own target (on purpose, so
    ///   attack runs aren't aborted), so ships flew through each other and rammed the player. Now, after each move, an AI
    ///   ship closer than KeepApart beyond both hulls to another AI ship or the player's ship gets a push away from it,
    ///   up to PushSpeed, harder the closer (as Patrol already did for its Borg cubes).
    /// </summary>
    internal static class EnemyAiFlying
    {
        private static bool On => Plugin.EnemyAiFlyBetter?.Value ?? false;

        private const float MinTurnThrottle = 0.35f;   // speed share when facing straight away from where it's going
        private const double EaseDistance = 3000;       // metres from the nav point where a ship out of combat eases off
        private const float MinEase = 0.3f;
        private const double KeepApart = 300;          // metres kept between two hulls
        private const double PushSpeed = 150;          // m/s pushing apart at contact

        private static readonly HashSet<AI.AgentShip> ships = new HashSet<AI.AgentShip>();
        private static readonly List<AI.AgentShip> gone = new List<AI.AgentShip>();
        private static readonly Dictionary<int, float> lastFacing = new Dictionary<int, float>();
        private static readonly AccessTools.FieldRef<AI.AgentShip, Vector3d> MoveVelocity = AccessTools.FieldRefAccess<AI.AgentShip, Vector3d>("KEIEAFFMBPF");
        private static readonly AccessTools.FieldRef<AI.AgentShip, bool> MasterSwitched = AccessTools.FieldRefAccess<AI.AgentShip, bool>("ACOECPIEEKJ");
        private static readonly AccessTools.FieldRef<AI.AgentShip, bool> IsStatic = AccessTools.FieldRefAccess<AI.AgentShip, bool>("_isStatic");
        private static readonly AccessTools.FieldRef<AI.AgentShip, bool> TngSmooth = AccessTools.FieldRefAccess<AI.AgentShip, bool>("_tngUseSmoothDamp");
        private static readonly AccessTools.FieldRef<AI.AgentShip, float> TngTime = AccessTools.FieldRefAccess<AI.AgentShip, float>("_tngFaceSmoothTime");
        private static readonly AccessTools.FieldRef<AI.AgentShip, float> TngMax = AccessTools.FieldRefAccess<AI.AgentShip, float>("_tngFaceMaxSpeed");
        private static readonly AccessTools.FieldRef<AI.AgentShip, Vector3> TngVelocity = AccessTools.FieldRefAccess<AI.AgentShip, Vector3>("JJCJFHCGHIM");
        private static readonly AccessTools.FieldRef<AI.AgentShip, AnimationCurve> TurnCurve = AccessTools.FieldRefAccess<AI.AgentShip, AnimationCurve>("_turnRateCurve");
        private static readonly AccessTools.FieldRef<AI.AgentShip, Quaternion> DesiredRotation = AccessTools.FieldRefAccess<AI.AgentShip, Quaternion>("HGAOKEFAKHF");
        private static readonly AccessTools.FieldRef<AI.AgentShip, int> LastFacingFrame = AccessTools.FieldRefAccess<AI.AgentShip, int>("LHFANEENMAA");
        private static bool loggedPush, loggedTurn;

        internal static void Patch(Harmony harmony)
        {
            HarmonyMethod Me(string name) => new HarmonyMethod(typeof(EnemyAiFlying), name);
            var enable = AccessTools.DeclaredMethod(typeof(AI.AgentShip), "JFBMOICFCEL");  // OnBodyEnabled
            var disable = AccessTools.DeclaredMethod(typeof(AI.AgentShip), "OIHBNJAKAMH"); // OnBodyDisabled
            var move = AccessTools.DeclaredMethod(typeof(AI.AgentShip), "MHHGGHHKMKN");    // UpdateMoveVelocity
            var face = AccessTools.DeclaredMethod(typeof(AI.AgentShip), "HICMEIBEAKI");    // UpdateFacing
            if (enable == null || disable == null || move == null || face == null)
            {
                Plugin.Log.LogWarning("Enemy AI flying: the ship's methods weren't found; left as the game has it");
                return;
            }
            harmony.Patch(enable, postfix: Me(nameof(Enabled_Postfix)));
            harmony.Patch(disable, postfix: Me(nameof(Disabled_Postfix)));
            harmony.Patch(move, prefix: Me(nameof(Move_Prefix)), postfix: Me(nameof(Move_Postfix)));
            harmony.Patch(face, prefix: Me(nameof(Facing_Prefix)));
            Plugin.Log.LogInfo($"Enemy AI flying: patched ({(On ? "on" : "off")})");
        }

        private static void Enabled_Postfix(AI.AgentShip __instance) { if (__instance != null) ships.Add(__instance); }

        private static void Disabled_Postfix(AI.AgentShip __instance)
        {
            if (__instance == null) return;
            ships.Remove(__instance);
            lastFacing.Remove(__instance.GetInstanceID());
        }

        // ---- Throttle ------------------------------------------------------------------------------------------------

        private static void Move_Prefix(AI.AgentShip __instance, out double __state)
        {
            __state = double.NaN;
            if (!On) return;
            try
            {
                var bb = __instance.NMJIEDJOOCJ;
                var body = __instance.ALPAOOKFIEE;
                if (bb == null || body == null || body.InAttic) return;
                double wanted = bb.EMFCFEPAGHL; // DesiredSpeed
                if (wanted <= 0) return;
                float scale = 1f;
                var facing = bb.DesiredFacing;
                if (facing.sqrMagnitude > 1e-6f)
                {
                    float angle = Vector3.Angle(body.Forward, facing);
                    scale = Mathf.Max(MinTurnThrottle, Mathf.Cos(angle * 0.5f * Mathf.Deg2Rad));
                }
                if (!InCombat(bb) && bb.NavMarker != null)
                {
                    var nav = bb.NavMarker.ALPAOOKFIEE;
                    if (nav != null && nav.System == body.System)
                    {
                        double distance = (nav.Position - body.Position).magnitude;
                        if (distance < EaseDistance) scale *= Mathf.Max(MinEase, (float)(distance / EaseDistance));
                    }
                }
                if (scale >= 0.999f) return;
                __state = wanted;
                bb.EMFCFEPAGHL = wanted * scale;
            }
            catch { }
        }

        private static bool InCombat(AI.MNIJEDINGOI bb) => bb.Scratch != null && bb.Scratch.TryGetValue("hasAgro", out int agro) && agro == 1;

        // ---- Keeping apart -------------------------------------------------------------------------------------------

        private static void Move_Postfix(AI.AgentShip __instance, double __state)
        {
            try
            {
                if (!double.IsNaN(__state)) __instance.NMJIEDJOOCJ.EMFCFEPAGHL = __state; // DesiredSpeed back as the tree set it
                if (!On) return;
                var me = __instance.ALPAOOKFIEE;
                if (me == null || me.InAttic) return;
                double step = PushSpeed * Time.fixedDeltaTime;
                double x = 0, y = 0, z = 0;
                void Away(SystemTransform them, string name)
                {
                    if (them == null || ReferenceEquals(them, me) || them.System != me.System || them.InAttic) return;
                    double dx = me.Position.x - them.Position.x, dy = me.Position.y - them.Position.y, dz = me.Position.z - them.Position.z;
                    double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    double keep = Radius(me) + Radius(them) + KeepApart;
                    if (distance >= keep) return;
                    if (distance < 1) { dx = 1; dy = 0; dz = 0; distance = 1; }
                    double push = step * Math.Min(1, 2 * (1 - distance / keep));
                    x += dx / distance * push; y += dy / distance * push; z += dz / distance * push;
                    if (!loggedPush)
                    {
                        loggedPush = true;
                        Plugin.Log.LogInfo($"Enemy AI flying: {__instance.name} {distance:0} m from {name}, keeping {keep:0} m; pushed apart");
                    }
                }
                foreach (var other in ships)
                {
                    if (other == null) { gone.Add(other); continue; }
                    if (ReferenceEquals(other, __instance) || !other.gameObject.activeInHierarchy) continue;
                    Away(other.ALPAOOKFIEE, other.name);
                }
                if (gone.Count > 0) { foreach (var g in gone) ships.Remove(g); gone.Clear(); }
                var player = __instance.NMJIEDJOOCJ?.PlayerShip;
                if (player != null) Away(player.ALPAOOKFIEE, "the player's ship");
                if (x == 0 && y == 0 && z == 0) return;
                ref var velocity = ref MoveVelocity(__instance);
                velocity = new Vector3d(velocity.x + x, velocity.y + y, velocity.z + z);
            }
            catch { } // a ship going away mid-step
        }

        private static double Radius(SystemTransform body)
        {
            double r = body.Radius;
            return r > 0 && r < 1e5 ? r : 0;
        }

        // ---- Turning -------------------------------------------------------------------------------------------------

        private const float LongestTurnGap = 0.5f; // a longer gap (just spawned, back from far away): one physics step

        private static bool Facing_Prefix(AI.AgentShip __instance)
        {
            if (!On) return true;
            try
            {
                if (!PhotonMaster() || MasterSwitched(__instance) || IsStatic(__instance)) return true;
                var body = __instance.ALPAOOKFIEE;
                var bb = __instance.NMJIEDJOOCJ;
                if (body == null || bb == null) return true;
                float now = Time.time, step = Time.fixedDeltaTime;
                int id = __instance.GetInstanceID();
                float since = lastFacing.TryGetValue(id, out var then) ? now - then : step;
                lastFacing[id] = now;
                if (since <= 0f || since > LongestTurnGap) since = step;

                Quaternion rotation = body.Rotation, desired = rotation;
                Vector3 facing = bb.DesiredFacing;
                if (facing.sqrMagnitude > 1e-6f)
                {
                    if (TngSmooth(__instance))
                    {
                        ref var smooth = ref TngVelocity(__instance);
                        var forward = Vector3.SmoothDamp(body.Forward, facing, ref smooth, TngTime(__instance), TngMax(__instance), since);
                        if (forward.sqrMagnitude > 1e-6f) desired = Quaternion.LookRotation(forward, Vector3.up);
                    }
                    else
                    {
                        var target = Quaternion.LookRotation(facing);
                        // The game's own per-call fraction at one call a physics step, then compounded for the real gap.
                        float input = Mathf.Clamp(Quaternion.Angle(rotation, target) * step, 0f, 180f);
                        float t = Mathf.Clamp01(TurnCurve(__instance).Evaluate(input));
                        float turned = 1f - Mathf.Pow(1f - t, since / step);
                        desired = Quaternion.Lerp(rotation, target, turned);
                    }
                }
                DesiredRotation(__instance) = desired;
                LastFacingFrame(__instance) = Time.frameCount;
                if (!loggedTurn)
                {
                    loggedTurn = true;
                    Plugin.Log.LogInfo($"Enemy AI flying: {__instance.name} turning for {since * 1000:0} ms since its last turn");
                }
                return false;
            }
            catch { return true; }
        }

        private static readonly Func<bool> isMaster = MasterGetter();

        private static Func<bool> MasterGetter()
        {
            var getter = AccessTools.Method(typeof(NDIOGIGMPBH), "get_isMasterClient"); // PhotonNetwork.isMasterClient
            return getter != null ? (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), getter) : () => false;
        }

        private static bool PhotonMaster()
        {
            try { return isMaster(); }
            catch { return false; }
        }
    }

    /// <summary>
    /// Enemy ships fight smarter (setting EnemyAI.FightSmarter; host only). tools/xref/ai/combat.md (branch ai-map) has
    /// the game's targeting, weapons and attack patterns these change.
    ///
    /// - Grudges fade: the AI's aggro list only ever grew, so a ship kept on whoever it first got angry with. Now what
    ///   it has gained above its starting level (60, the player 110, a Borg cube 160) fades with a half-life of
    ///   GrudgeHalfLife, and every hit adds DamageGrudge per point of damage on top of the game's flat 10, so ships turn on
    ///   whoever is hurting them now. (AgentShip.UpdateTargetAndAgroList and ApplyDamage, JJEBPBHKBIE.)
    /// - Attack patterns in a random order: the game tried a ship's patterns in list order and the first that could start
    ///   always ran (the Bird of Prey never orbits). Now every new attack tries them in a shuffled order
    ///   (AgentShip.CreateAttackSubtree, MLJDADGCJPH, built at spawn: ships spawned after the setting changes).
    /// - A fallback attack: when every pattern was cooling down the attack branch failed and the ship wandered off. Now
    ///   it holds station off the target's beam and keeps firing until a pattern can start again.
    /// - Torpedoes when shields are down: PickWeaponGroup (OLKGIDHOMIG) picks the torpedo group when it can fire and the
    ///   target's shields are gone.
    /// - Firing on the approach: attack patterns cut phaser power to 1-2 on their approach legs, which cut phaser range
    ///   to 35-45 %. NPCWeaponGroup.Range now never drops below the 3-pip range (57 %) while phasers have any power.
    /// </summary>
    internal static class EnemyAiCombat
    {
        private static bool On => Plugin.EnemyAiFightSmarter?.Value ?? false;

        private const float GrudgeHalfLife = 15f;  // seconds
        private const float DamageGrudge = 0.25f;  // aggro per point of damage dealt
        private const float FallbackDistance = 5000f, FallbackSpeed = 0.9f;

        private static readonly Dictionary<int, float> lastFade = new Dictionary<int, float>();
        private static readonly List<Sector.HOLJLCLENCA> keys = new List<Sector.HOLJLCLENCA>();
        // Typed accessors, made in Patch (these run every AI tick: no reflection boxing there).
        private static System.Reflection.FieldInfo selectorChildren;
        private static Func<Sector.FAMOMDAAPCH, float> shieldHealth;
        private static Func<AI.OLKGIDHOMIG, Sector.Spacecraft.FJKHPJOOAHN, AI.MNIJEDINGOI, bool> canUseGroup;
        private static AccessTools.FieldRef<AI.OLKGIDHOMIG, Sector.Spacecraft.MDMEPIGBOFJ> pickType;
        private static AccessTools.FieldRef<AI.OLKGIDHOMIG, bool> pickSecondary;
        private static AccessTools.FieldRef<Sector.Spacecraft.NPCWeaponGroup, PhaserData> phaserData;
        private static AccessTools.FieldRef<Sector.Spacecraft.NPCWeaponGroup, float> threePips;
        private static AccessTools.FieldRef<PhaserData, float> phaserRange;
        private static bool loggedPatterns, loggedTorpedo;

        internal static void Patch(Harmony harmony)
        {
            HarmonyMethod Me(string name) => new HarmonyMethod(typeof(EnemyAiCombat), name);
            int ok = 0, of = 0;
            void Try(string what, Func<bool> patch)
            {
                of++;
                try { if (patch()) ok++; else Plugin.Log.LogWarning($"Enemy AI combat: {what}: not found, left as the game has it"); }
                catch (Exception e) { Plugin.Log.LogWarning($"Enemy AI combat: {what}: {e.Message}"); }
            }
            Try("fading grudges", () =>
            {
                var update = AccessTools.DeclaredMethod(typeof(AI.AgentShip), "UpdateTargetAndAgroList");
                var damage = AccessTools.DeclaredMethod(typeof(AI.AgentShip), "JJEBPBHKBIE"); // ApplyDamage
                if (update == null || damage == null) return false;
                harmony.Patch(update, postfix: Me(nameof(Aggro_Postfix)));
                harmony.Patch(damage, postfix: Me(nameof(Damage_Postfix)));
                return true;
            });
            Try("attack patterns", () =>
            {
                var attack = AccessTools.DeclaredMethod(typeof(AI.AgentShip), "MLJDADGCJPH"); // CreateAttackSubtree
                selectorChildren = AccessTools.Field(typeof(AI.EPMNKCFGGAG), "JEGNDMPCDEG");  // BTSelector._children
                if (attack == null || selectorChildren == null) return false;
                harmony.Patch(attack, postfix: Me(nameof(Attack_Postfix)));
                return true;
            });
            Try("torpedo choice", () =>
            {
                var pick = AccessTools.DeclaredMethod(typeof(AI.OLKGIDHOMIG), "IKLNPCHBLCA"); // PickWeaponGroup.OnUpdate
                var canUse = AccessTools.Method(typeof(AI.OLKGIDHOMIG), "NBABDMBCPNF");          // CanUseWeaponGroup
                var shields = AccessTools.Method(typeof(Sector.FAMOMDAAPCH), "get_ShieldHealth"); // TargetInfo
                if (pick == null || canUse == null || shields == null) return false;
                canUseGroup = AccessTools.MethodDelegate<Func<AI.OLKGIDHOMIG, Sector.Spacecraft.FJKHPJOOAHN, AI.MNIJEDINGOI, bool>>(canUse);
                shieldHealth = AccessTools.MethodDelegate<Func<Sector.FAMOMDAAPCH, float>>(shields);
                pickType = AccessTools.FieldRefAccess<AI.OLKGIDHOMIG, Sector.Spacecraft.MDMEPIGBOFJ>("HBDGLAAINBJ"); // _weaponType
                pickSecondary = AccessTools.FieldRefAccess<AI.OLKGIDHOMIG, bool>("IJHMKEDAOHJ");                  // _isSecondary
                harmony.Patch(pick, postfix: Me(nameof(Pick_Postfix)));
                return true;
            });
            Try("approach phaser range", () =>
            {
                var range = AccessTools.DeclaredMethod(typeof(Sector.Spacecraft.NPCWeaponGroup), "get_Range"); // the property itself is scrambled
                if (range == null) return false;
                phaserData = AccessTools.FieldRefAccess<Sector.Spacecraft.NPCWeaponGroup, PhaserData>("_phaserData");
                threePips = AccessTools.FieldRefAccess<Sector.Spacecraft.NPCWeaponGroup, float>("_threeUnitRangeModifier");
                phaserRange = AccessTools.FieldRefAccess<PhaserData, float>("_range");
                harmony.Patch(range, postfix: Me(nameof(Range_Postfix)));
                return true;
            });
            Plugin.Log.LogInfo($"Enemy AI combat: {ok} of {of} patched ({(On ? "on" : "off")})");
        }

        // ---- Fading grudges ------------------------------------------------------------------------------------------

        private static void Aggro_Postfix(AI.AgentShip __instance)
        {
            if (!On) return;
            try
            {
                var bb = __instance.NMJIEDJOOCJ;
                if (bb?.AgroList == null || bb.AgroList.Count == 0) return;
                float now = Time.time;
                int id = __instance.GetInstanceID();
                float since = lastFade.TryGetValue(id, out var then) ? now - then : 0f;
                lastFade[id] = now;
                if (since <= 0f || since > 5f) return;
                float keep = Mathf.Pow(0.5f, since / GrudgeHalfLife);
                keys.Clear();
                keys.AddRange(bb.AgroList.Keys);
                foreach (var key in keys)
                {
                    if (key == null) continue;
                    float floor = 60f + (ReferenceEquals(key, bb.PlayerShip) ? 50f : 0f) + (key is AI.BorgCube ? 100f : 0f);
                    float value = bb.AgroList[key];
                    if (value > floor) bb.AgroList[key] = floor + (value - floor) * keep;
                }
            }
            catch { }
        }

        private static void Damage_Postfix(AI.AgentShip __instance, float __0, Sector.HOLJLCLENCA __1)
        {
            if (!On || __1 == null || __0 <= 0f) return;
            try
            {
                var list = __instance.NMJIEDJOOCJ?.AgroList;
                if (list != null && list.TryGetValue(__1, out var value)) list[__1] = value + __0 * DamageGrudge; // only enemies are on it
            }
            catch { }
        }

        // ---- Attack patterns: shuffled, with a fallback ----------------------------------------------------------------

        private static void Attack_Postfix(AI.AgentShip __instance, ref AI.GGCBOJKCOMJ __result)
        {
            if (!On || !(__result is AI.EPMNKCFGGAG selector)) return;
            try
            {
                var patterns = (AI.GGCBOJKCOMJ[])selectorChildren.GetValue(selector);
                if (patterns == null) return;
                var children = new List<AI.GGCBOJKCOMJ>(patterns);
                children.Add(Fallback(__instance));
                __result = new ShuffledSelector(children.ToArray(), shuffled: patterns.Length);
                if (!loggedPatterns)
                {
                    loggedPatterns = true;
                    Plugin.Log.LogInfo($"Enemy AI combat: {__instance.name}: {patterns.Length} attack patterns in a random order, then the fallback");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Enemy AI combat: attack patterns: " + e.Message); }
        }

        // Fire whatever is ready, then hold station off the target's beam: one step, so the patterns are tried again
        // on the next tick.
        private static AI.GGCBOJKCOMJ Fallback(AI.AgentShip ship)
        {
            float side = (ship.GetInstanceID() & 1) == 0 ? 90f : -90f;
            return new AI.JMDIFJIGIBC(new AI.GGCBOJKCOMJ[]                                      // BTSequence
            {
                new AI.GMOMNCADEJC(AI.GBEFAHLEGIO.Target),                                       // IsSlotValid(Target)
                new AI.GFKLFKNCDJK("attackNeedsClear", 0),                                       // SetScratch
                new AI.HAHOAOCHNKA(new AI.GGCBOJKCOMJ[]                                          // OptionalSequence
                {
                    new AI.MMIHFPNOHHG(new AI.BMIBPOJLOPD()),                                    // Inverter(IsCloakActive)
                    AI.NCFNINLMBCP.PickWeaponAndAttack(),
                }),
                new AI.EPMNKCFGGAG(new AI.GGCBOJKCOMJ[]                                          // BTSelector
                {
                    AI.NCFNINLMBCP.MoveToAvoidMarker(0.5f),
                    new AI.JMDIFJIGIBC(new AI.GGCBOJKCOMJ[]
                    {
                        new AI.GINLNFBFJEB(AI.GBEFAHLEGIO.Target, side, FallbackDistance, 0.0), // SetNavRelativeToSlot
                        new AI.DNCGOHCCBCB(AI.GBEFAHLEGIO.Nav),                                  // FaceSlot(Nav)
                        new AI.JIJPOKNLMBM(FallbackSpeed, false, 0f, AI.NFFNHMHIMML.NONE, false), // SetSpeed
                    }),
                }),
            });
        }

        /// <summary>A selector that tries its first <c>shuffled</c> children in a new random order each time it starts, then
        /// the rest in order. Like the game's BTSelector: Success on the first child that succeeds, Fail when all fail.</summary>
        private sealed class ShuffledSelector : AI.GGCBOJKCOMJ
        {
            private readonly AI.GGCBOJKCOMJ[] children;
            private readonly int[] order;
            private readonly int shuffled;
            private int current = -1;

            public ShuffledSelector(AI.GGCBOJKCOMJ[] children, int shuffled)
            {
                this.children = children;
                this.shuffled = Math.Min(shuffled, children.Length);
                order = new int[children.Length];
                for (int i = 0; i < order.Length; i++) order[i] = i;
            }

            public override void Init(AI.CJJGGNNFNLJ scheduler)
            {
                base.Init(scheduler);
                foreach (var child in children) child?.Init(scheduler);
                current = -1;
            }

            protected override AI.LECHLIEBBEM IKLNPCHBLCA(AI.MNIJEDINGOI bb, AI.LECHLIEBBEM childResult)
            {
                if (current >= 0 && childResult == AI.LECHLIEBBEM.Success) { current = -1; return AI.LECHLIEBBEM.Success; }
                if (current >= 0 && childResult == AI.LECHLIEBBEM.Fail) current++;
                else
                {
                    current = 0;
                    var rng = bb.Rng;
                    for (int i = shuffled - 1; i > 0; i--) // Fisher-Yates over the patterns only
                    {
                        int j = rng != null ? rng.Next(0, i + 1) : UnityEngine.Random.Range(0, i + 1);
                        int swap = order[i]; order[i] = order[j]; order[j] = swap;
                    }
                }
                for (; current < children.Length; current++)
                {
                    var child = children[order[current]];
                    if (child == null) continue;
                    var result = child.ProcessUpdate(bb, AI.LECHLIEBBEM.NONE, false);
                    if (result == AI.LECHLIEBBEM.Running) return result;
                    if (result == AI.LECHLIEBBEM.Success) { current = -1; return result; }
                }
                current = -1;
                return AI.LECHLIEBBEM.Fail;
            }
        }

        // ---- Torpedoes when the shields are down -----------------------------------------------------------------------

        private static void Pick_Postfix(AI.OLKGIDHOMIG __instance, AI.MNIJEDINGOI __0, AI.LECHLIEBBEM __result)
        {
            if (!On || __result != AI.LECHLIEBBEM.Success) return;
            try
            {
                var bb = __0;
                if (pickSecondary(__instance) || pickType(__instance) != 0) return; // a pattern asking for one kind
                if (!(bb.Owner is AI.AgentShip ship) || bb.Target == null) return;
                var info = bb.Target.GetTargetInfo();
                if (info == null || shieldHealth(info) > 0f) return;
                var groups = ship.BOKKOEPBHKF; // WeaponGroups
                if (groups == null || bb.CurrentWeaponGroup >= 0 && bb.CurrentWeaponGroup < groups.Length
                    && groups[bb.CurrentWeaponGroup].FCBJFOCMKEA == Sector.Spacecraft.MDMEPIGBOFJ.Torpedo) return;
                for (int i = 0; i < groups.Length; i++)
                {
                    var group = groups[i];
                    if (group == null || group.FCBJFOCMKEA != Sector.Spacecraft.MDMEPIGBOFJ.Torpedo || !bb.AvailableWeapons.Contains(group)) continue;
                    if (!canUseGroup(__instance, group, bb)) continue;
                    bb.CurrentWeaponGroup = i;
                    if (!loggedTorpedo)
                    {
                        loggedTorpedo = true;
                        Plugin.Log.LogInfo($"Enemy AI combat: {ship.name}: target's shields are down, torpedoes");
                    }
                    return;
                }
            }
            catch { }
        }

        // ---- Phaser range on the approach --------------------------------------------------------------------------------

        private static void Range_Postfix(Sector.Spacecraft.NPCWeaponGroup __instance, ref float __result)
        {
            if (!On || __result <= 0f) return; // 0 pips: no phasers, as the game has it
            try
            {
                var data = phaserData(__instance);
                if (data == null) return;
                float floor = phaserRange(data) * threePips(__instance);
                if (__result < floor) __result = floor;
            }
            catch { }
        }
    }
}
