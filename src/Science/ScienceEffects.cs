using System;
using System.Reflection;
using HarmonyLib;
using Sector;
using Sector.Spacecraft;
using Sector.Spacecraft.Subsystems;
using UnityEngine;

namespace StbcRevival.Science
{
    /// <summary>
    /// What the science ledger does to a fight, in a Patrol with the console on (ScienceSession.Active) and nowhere
    /// else. Crew upgrades: the player ship's hits on AI ships (AgentShip.takeHit, both overloads) hit harder, by the
    /// weapons share plus the weak-spot share for the victim's team; its shields take less from each hit
    /// (BubbleShieldServer.shieldHit); its top speed and top turn rate (SimpleEngineClient._maxSpeed and _maxYawVel,
    /// raised while the engine steps) go up; its subsystems' auto-repair adds more. Enemy
    /// steps: hostile AI ships hit the player ship harder (BaseSpacecraft.takeHit, which PlayerSpacecraft's override
    /// calls once), have more shield (AgentShip.MaxShieldHealth) and fly faster (the move AgentShip.UpdateMoveVelocity
    /// and BorgCube's override work out). Damage, repair and AI flying are the host's; the engine runs where the ship
    /// is flown and the shield maximum is read everywhere, so those two apply on every machine (each has the ledger).
    /// Anything that throws leaves the game's value as it was.
    /// </summary>
    internal static class ScienceEffects
    {
        private enum Fx { Weapons, EnemyDamage, Shields, EnemyShields, Impulse, EnemySpeed, Repair, Count }
        private static readonly float[] nextLog = new float[(int)Fx.Count];

        private static readonly AccessTools.FieldRef<AI.AgentShip, Vector3d> MoveVelocity =
            AccessTools.FieldRefAccess<AI.AgentShip, Vector3d>("KEIEAFFMBPF"); // _moveVelocity: this step's move, metres
        private static readonly AccessTools.FieldRef<SimpleEngineClient, float> MaxYawVel =
            AccessTools.FieldRefAccess<SimpleEngineClient, float>("_maxYawVel");
        private static readonly AccessTools.FieldRef<SimpleEngineClient, float> MaxSpeed =
            AccessTools.FieldRefAccess<SimpleEngineClient, float>("_maxSpeed");

        /// <summary>What an engine step changed, put back after it (Set false: nothing).</summary>
        private struct EngineSaved { public bool Set; public float Speed, Yaw; }

        private static SimpleEngineClient lastEngine; // the engine last asked about, and whether it's a player ship's
        private static bool lastEngineIsPlayer;

        private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        internal static void Patch(Harmony harmony)
        {
            // Crew weapons: AgentShip.takeHit(Ray, float, float damage, Subsystem, attacker, ...) for phasers and
            // takeHit(Vector3, float damage, Subsystem, attacker, ...) for torpedoes. The attacker is the game's sector
            // body interface (HOLJLCLENCA), so it's taken as an object.
            MethodInfo phaser = null, torpedo = null;
            foreach (var m in typeof(AI.AgentShip).GetMethods(Declared))
            {
                if (m.Name != "takeHit") continue;
                var p = m.GetParameters();
                if (p.Length >= 5 && p[0].ParameterType == typeof(Ray) && p[2].ParameterType == typeof(float) && !p[4].ParameterType.IsValueType) phaser = m;
                else if (p.Length >= 4 && p[0].ParameterType == typeof(Vector3) && p[1].ParameterType == typeof(float) && !p[3].ParameterType.IsValueType) torpedo = m;
            }
            Hook(harmony, phaser, "crew weapons (phasers)", "AgentShip.takeHit(Ray, ...)", prefix: nameof(PhaserHit_Prefix));
            Hook(harmony, torpedo, "crew weapons (torpedoes)", "AgentShip.takeHit(Vector3, ...)", prefix: nameof(TorpedoHit_Prefix));

            // Enemy damage: BaseSpacecraft's own takeHit(Vector3, float damage, Subsystem, attacker, ...), where every
            // hit on a player ship ends up (the Ray overload and explosions call it; PlayerSpacecraft's override calls
            // base once). Only the base method is patched, so a hit is scaled once.
            MethodInfo shipHit = null;
            foreach (var m in typeof(BaseSpacecraft).GetMethods(Declared))
            {
                var p = m.GetParameters();
                if (m.Name == "takeHit" && p.Length >= 4 && p[0].ParameterType == typeof(Vector3) && p[1].ParameterType == typeof(float) && !p[3].ParameterType.IsValueType) shipHit = m;
            }
            Hook(harmony, shipHit, "enemy damage", "BaseSpacecraft.takeHit(Vector3, ...)", prefix: nameof(ShipHit_Prefix));

            // Crew shields: BubbleShieldServer.shieldHit(float damage, attacker, out float leftover).
            MethodInfo shieldHit = null;
            foreach (var m in typeof(BubbleShieldServer).GetMethods(Declared))
            {
                var p = m.GetParameters();
                if (m.Name == "shieldHit" && p.Length == 3 && p[0].ParameterType == typeof(float) && p[2].ParameterType.IsByRef) shieldHit = m;
            }
            Hook(harmony, shieldHit, "crew shields", "BubbleShieldServer.shieldHit(float, attacker, out float)", prefix: nameof(ShieldHit_Prefix), postfix: nameof(ShieldHit_Postfix));

            Hook(harmony, AccessTools.DeclaredMethod(typeof(AI.AgentShip), "get_MaxShieldHealth"), "enemy shields", "AgentShip.MaxShieldHealth (LPAMAFAACEJ) getter", postfix: nameof(MaxShield_Postfix));

            // Crew impulse. The getters currentMaxSpeed (JGAFJNCGLIM, maxSpeed x speedModifier) and currentMaxYaw
            // (PMJCEJAEPHD) are ~19 bytes and final, so Mono may inline them into the engine step and its drag getters;
            // a postfix there could log while the ship flies no faster. Instead the fields they read, _maxSpeed and
            // _maxYawVel, are raised for the length of the engine step (BatchFixedUpdate, whose speed cap, drags and
            // cosmetic bank are their only readers there; nothing writes them after construction) and put back after.
            Hook(harmony, MaxSpeed == null || MaxYawVel == null ? null : AccessTools.DeclaredMethod(typeof(SimpleEngineClient), "BatchFixedUpdate"), "crew impulse (speed, turning)", "SimpleEngineClient.BatchFixedUpdate (_maxSpeed, _maxYawVel)", prefix: nameof(EngineStep_Prefix), finalizer: nameof(EngineStep_Finalizer));

            // Enemy speed: the move worked out by UpdateMoveVelocity (MHHGGHHKMKN), AgentShip's and BorgCube's own
            // (which doesn't call base). _moveVelocity is worked out afresh each step, so scaling it doesn't add up. Ahead
            // of Patrol's cube push, which is left as it is.
            var ok = MoveVelocity != null;
            Hook(harmony, ok ? AccessTools.DeclaredMethod(typeof(AI.AgentShip), "MHHGGHHKMKN") : null, "enemy speed", "AgentShip.UpdateMoveVelocity (MHHGGHHKMKN)", postfix: nameof(Move_Postfix), first: true);
            Hook(harmony, ok ? AccessTools.DeclaredMethod(typeof(AI.BorgCube), "MHHGGHHKMKN") : null, "enemy speed (cubes)", "BorgCube.UpdateMoveVelocity (MHHGGHHKMKN)", postfix: nameof(Move_Postfix), first: true);

            // Crew repair: SubsystemServer.AutoRepair (PPBDHDJCFKF) adds subsystemAutoRepairPoints x repair crews; the
            // points are raised for the one call, so the game's own caps and status update still apply.
            Hook(harmony, AccessTools.DeclaredMethod(typeof(SubsystemServer), "PPBDHDJCFKF"), "crew repair", "SubsystemServer.AutoRepair (PPBDHDJCFKF)", prefix: nameof(Repair_Prefix), finalizer: nameof(Repair_Finalizer));
        }

        private static void Hook(Harmony harmony, MethodInfo target, string effect, string where, string prefix = null, string postfix = null, string finalizer = null, bool first = false)
        {
            if (target == null)
            {
                Plugin.Log.LogWarning($"Science effects: {where} not found; {effect} has no effect");
                return;
            }
            HarmonyMethod Make(string name)
            {
                if (name == null) return null;
                var h = new HarmonyMethod(typeof(ScienceEffects), name);
                if (first) h.priority = Priority.High;
                return h;
            }
            try
            {
                harmony.Patch(target, prefix: Make(prefix), postfix: Make(postfix), finalizer: Make(finalizer));
                Plugin.Log.LogInfo($"Science effects: {effect} ready ({where}, 1 found)");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Science effects: patching {where} failed ({e.Message}); {effect} has no effect"); }
        }

        // ------------------------------------------------------------------------------------------- crew weapons
        private static void PhaserHit_Prefix(AI.AgentShip __instance, ref float __2, object __4) => PlayerHit(__instance, ref __2, __4, "phaser");
        private static void TorpedoHit_Prefix(AI.AgentShip __instance, ref float __1, object __3) => PlayerHit(__instance, ref __1, __3, "torpedo");

        private static void PlayerHit(AI.AgentShip victim, ref float damage, object attacker, string weapon)
        {
            if (!ScienceSession.Active || damage <= 0f || !(attacker is PlayerSpacecraft) || victim == null) return;
            try
            {
                int team = TeamOf(victim);
                var ledger = ScienceSession.Ledger;
                float k = ledger.Crew(UpgradeArea.Weapons) + ledger.CrewVsFaction(team);
                if (k <= 0f) return;
                float was = damage;
                damage *= 1f + k;
                if (Due(Fx.Weapons)) Log($"crew weapons: {weapon} hit on team {team}, {was:0.##} -> {damage:0.##} (x{1f + k:0.00})");
            }
            catch (Exception) { /* as it was */ }
        }

        // ------------------------------------------------------------------------------------------- enemy damage
        private static void ShipHit_Prefix(BaseSpacecraft __instance, ref float __1, object __3)
        {
            if (!ScienceSession.Active || __1 <= 0f || !(__instance is PlayerSpacecraft) || !(__3 is AI.AgentShip enemy)) return;
            try
            {
                float k = ScienceSession.Ledger.Enemy(EnemyArea.Damage);
                if (k <= 0f || !Ledger.IsHostileTeam(TeamOf(enemy))) return;
                float was = __1;
                __1 *= 1f + k;
                if (Due(Fx.EnemyDamage)) Log($"enemy damage: {enemy.name}'s hit on the ship, {was:0.##} -> {__1:0.##} (x{1f + k:0.00})");
            }
            catch (Exception) { /* as it was */ }
        }

        // ------------------------------------------------------------------------------------------- crew shields
        // The shield takes damage / (1 + k). What gets through (when the shield is down or breaks) is put back in
        // the hit's own terms, so the hull isn't spared too.
        private static void ShieldHit_Prefix(BubbleShieldServer __instance, ref float __0, out float __state)
        {
            __state = 0f;
            if (!ScienceSession.Active || __0 <= 0f) return;
            try
            {
                float k = ScienceSession.Ledger.Crew(UpgradeArea.Shields);
                if (k <= 0f || __instance == null || __instance.GetComponentInParent<PlayerSpacecraft>() == null) return;
                float was = __0;
                __0 /= 1f + k;
                __state = k;
                if (Due(Fx.Shields)) Log($"crew shields: hit on the shield {was:0.##} -> {__0:0.##} (/{1f + k:0.00})");
            }
            catch (Exception) { /* as it was */ }
        }

        private static void ShieldHit_Postfix(bool __result, ref float __2, float __state)
        {
            if (__state > 0f && !__result) __2 *= 1f + __state;
        }

        // ------------------------------------------------------------------------------------------- enemy shields
        private static void MaxShield_Postfix(AI.AgentShip __instance, ref float __result)
        {
            if (!ScienceSession.Active) return;
            try
            {
                float k = ScienceSession.Ledger.Enemy(EnemyArea.Shields);
                if (k <= 0f || __result <= 0f || !Ledger.IsHostileTeam(TeamOf(__instance))) return;
                float was = __result;
                __result *= 1f + k;
                if (Due(Fx.EnemyShields)) Log($"enemy shields: {__instance.name} max shield {was:0.#} -> {__result:0.#}");
            }
            catch (Exception) { /* as it was */ }
        }

        // ------------------------------------------------------------------------------------------- crew impulse
        private static void EngineStep_Prefix(SimpleEngineClient __instance, out EngineSaved __state)
        {
            __state = default(EngineSaved);
            if (!ScienceSession.Active) return;
            try
            {
                float k = ScienceSession.Ledger.Crew(UpgradeArea.Impulse);
                if (k <= 0f || !IsPlayerEngine(__instance)) return;
                ref float speed = ref MaxSpeed(__instance);
                ref float yaw = ref MaxYawVel(__instance);
                __state = new EngineSaved { Set = true, Speed = speed, Yaw = yaw };
                speed *= 1f + k;
                yaw *= 1f + k;
                if (Due(Fx.Impulse)) Log($"crew impulse: top speed {__state.Speed:0.###} -> {speed:0.###}, turn rate {__state.Yaw:0.##} -> {yaw:0.##}");
            }
            catch (Exception) { /* as it was */ }
        }

        private static Exception EngineStep_Finalizer(SimpleEngineClient __instance, EngineSaved __state, Exception __exception)
        {
            if (__state.Set)
                try { MaxSpeed(__instance) = __state.Speed; MaxYawVel(__instance) = __state.Yaw; } catch (Exception) { }
            return __exception;
        }

        /// <summary>The Patrol or the room ended: forget the engine looked up (the ship goes with it).</summary>
        internal static void Forget()
        {
            lastEngine = null;
            lastEngineIsPlayer = false;
        }

        private static bool IsPlayerEngine(SimpleEngineClient engine)
        {
            if (!ReferenceEquals(engine, lastEngine))
            {
                lastEngine = engine;
                lastEngineIsPlayer = engine != null && engine.GetComponentInParent<PlayerSpacecraft>() != null;
            }
            return lastEngineIsPlayer;
        }

        // ------------------------------------------------------------------------------------------- enemy speed
        private static void Move_Postfix(AI.AgentShip __instance)
        {
            if (!ScienceSession.Active) return;
            try
            {
                float k = ScienceSession.Ledger.Enemy(EnemyArea.Speed);
                if (k <= 0f || __instance == null || !Ledger.IsHostileTeam(TeamOf(__instance))) return;
                ref var v = ref MoveVelocity(__instance);
                double f = 1.0 + k;
                if (v.x == 0 && v.y == 0 && v.z == 0) return;
                var was = v;
                v = new Vector3d(v.x * f, v.y * f, v.z * f);
                if (Due(Fx.EnemySpeed))
                    Log($"enemy speed: {__instance.name} moves {Math.Sqrt(was.x * was.x + was.y * was.y + was.z * was.z):0.##} -> {Math.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z):0.##} m this step (x{f:0.00})");
            }
            catch (Exception) { /* as it was */ }
        }

        // ------------------------------------------------------------------------------------------- crew repair
        private static void Repair_Prefix(SubsystemServer __instance, out float __state)
        {
            __state = float.NaN;
            if (!ScienceSession.Active) return;
            try
            {
                float k = ScienceSession.Ledger.Crew(UpgradeArea.Repair);
                if (k <= 0f || __instance == null || __instance.GetComponentInParent<PlayerSpacecraft>() == null) return;
                __state = __instance.subsystemAutoRepairPoints;
                __instance.subsystemAutoRepairPoints = __state * (1f + k);
                if (Due(Fx.Repair)) Log($"crew repair: {__instance.GetType().Name} repairs {__state:0.##} -> {__instance.subsystemAutoRepairPoints:0.##} points per crew");
            }
            catch (Exception) { /* as it was */ }
        }

        private static Exception Repair_Finalizer(SubsystemServer __instance, float __state, Exception __exception)
        {
            if (!float.IsNaN(__state) && __instance != null) __instance.subsystemAutoRepairPoints = __state;
            return __exception;
        }

        // ------------------------------------------------------------------------------------------- helpers
        private static int TeamOf(SectorBody body) =>
            body != null && body.GetTargetInfo() is TargetInfoComponent info ? (int)info.DGGJOECIFAF : 0;

        private static bool Due(Fx fx)
        {
            float now = Time.unscaledTime;
            if (now < nextLog[(int)fx]) return false;
            nextLog[(int)fx] = now + 1f;
            return true;
        }

        // At most once a second per effect: Info while a test ledger is set ([Debug] ScienceLedger), else Debug.
        private static void Log(string line)
        {
            line = "Science effects: " + line + (ScienceSession.IsHost ? "" : " (crew)");
            if (Plugin.ScienceLedger != null && !string.IsNullOrEmpty(Plugin.ScienceLedger.Value)) Plugin.Log.LogInfo(line);
            else Plugin.Log.LogDebug(line);
        }
    }
}
