using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// The smaller enemy AI changes after stage 5, each under the setting it belongs with (all host only, where the AI
    /// runs, except the turret lock-on, which every game works out):
    ///
    /// Bug fixes (EnemyAI.BugFixes):
    /// - Turret lock-on: BeamTurret.UpdateLockOn set LockedOn from each of its emitters in turn, so only the last one
    ///   counted. Now it's locked on when any emitter has the target in its arc.
    /// - A phaser hit on a ship other than the weapon's target (a burst landing after the target changed, or a bolt
    ///   hitting a ship in the way) did no damage (AgentShip.OnPhaserAttackResultsEvent). Now it damages that ship if it
    ///   is on another team; friendly ships in the way still take nothing.
    ///
    /// Fights smarter (EnemyAI.FightSmarter):
    /// - With an AI ship's shields down, a phaser hit on a subsystem took all of the damage into that subsystem and none
    ///   into the hull (AgentShip.takeHit(Ray...)). Now SubsystemShare goes to the subsystem and the rest to the hull.
    ///
    /// Flies better (EnemyAI.FlyBetter):
    /// - Collision rays: the forward rays were spread over the middle third of the hull's width (CastCollisionRays'
    ///   NextRayOffset, GNNKBPMLPFI). Now over the whole width.
    /// - Navigation exclusion zones (planets, stations, anomalies a mission marks): only Borg cubes and the Planet
    ///   Killer steered around them. Now every AI ship does (_avoidNavigationExclusionZones on as it spawns). When
    ///   where it's going is inside a zone (a freighter parked off a planet, a ship by a station), the game's step
    ///   (SetAvoidMarkerAroundExclusionZone, DCPPEEIGJAN) sends it to the zone's edge instead, so it never closes in;
    ///   for the ships switched on here that step is skipped and they fly straight in, as before.
    /// - Engine power: see EnemyAiFlying (the throttle there takes the ship's engine pips into account).
    /// </summary>
    internal static class EnemyAiExtras
    {
        private static bool Fixes => Plugin.EnemyAiFixes?.Value ?? false;
        private static bool Smarter => Plugin.EnemyAiFightSmarter?.Value ?? false;
        private static bool Flying => Plugin.EnemyAiFlyBetter?.Value ?? false;

        private const float SubsystemShare = 0.6f; // of a phaser hit on a subsystem with the shields down; the rest to the hull
        private const float RayWidth = 3f;         // the game's ray offsets reach a third of the hull's half-width: x3

        private static Func<BeamTurret, Sector.HOLJLCLENCA> turretTarget;
        private static Func<BeamTurret, float> minYaw, maxYaw, minPitch, maxPitch;
        private static Action<BeamTurret, bool> setLockedOn;
        private static AccessTools.FieldRef<BeamTurret, Transform[]> emitters;
        private static Func<Sector.FAMOMDAAPCH, Sector.DGGJOECIFAF> teamOf;
        private delegate void ApplyDamageCall(AI.AgentShip ship, float damage, Sector.HOLJLCLENCA attacker, AI.LGGMICILCMN type, bool toHull, bool toShields);
        private static ApplyDamageCall applyDamage;
        private static FieldInfo avoidZones;
        private static bool loggedLock, loggedStray, loggedSplit, loggedZones, loggedZoneTarget;

        internal static void Patch(Harmony harmony)
        {
            int ok = 0, of = 0;
            void Try(string what, Func<bool> patch)
            {
                of++;
                try { if (patch()) ok++; else Plugin.Log.LogWarning($"Enemy AI extras: {what}: not found, left as the game has it"); }
                catch (Exception e) { Plugin.Log.LogWarning($"Enemy AI extras: {what}: {e.Message}"); }
            }
            HarmonyMethod Me(string name) => new HarmonyMethod(typeof(EnemyAiExtras), name);
            T Getter<T>(Type type, string name) where T : Delegate
            {
                var m = AccessTools.Method(type, name);
                return m != null ? AccessTools.MethodDelegate<T>(m) : null;
            }

            Try("turret lock-on", () =>
            {
                var update = AccessTools.DeclaredMethod(typeof(BeamTurret), "UpdateLockOn");
                turretTarget = Getter<Func<BeamTurret, Sector.HOLJLCLENCA>>(typeof(BeamTurret), "get_Target");
                minYaw = Getter<Func<BeamTurret, float>>(typeof(BeamTurret), "get_MinYaw");
                maxYaw = Getter<Func<BeamTurret, float>>(typeof(BeamTurret), "get_MaxYaw");
                minPitch = Getter<Func<BeamTurret, float>>(typeof(BeamTurret), "get_MinPitch");
                maxPitch = Getter<Func<BeamTurret, float>>(typeof(BeamTurret), "get_MaxPitch");
                setLockedOn = Getter<Action<BeamTurret, bool>>(typeof(BeamTurret), "JGCFPKGNHKC"); // set_LockedOn
                emitters = AccessTools.FieldRefAccess<BeamTurret, Transform[]>("_emitters");
                if (update == null || turretTarget == null || minYaw == null || maxYaw == null || minPitch == null || maxPitch == null || setLockedOn == null) return false;
                harmony.Patch(update, prefix: Me(nameof(LockOn_Prefix)));
                return true;
            });
            Try("stray phaser hits", () =>
            {
                var results = AccessTools.DeclaredMethod(typeof(AI.AgentShip), "OnPhaserAttackResultsEvent");
                teamOf = Getter<Func<Sector.FAMOMDAAPCH, Sector.DGGJOECIFAF>>(typeof(Sector.FAMOMDAAPCH), "get_TeamID");
                if (results == null || teamOf == null) return false;
                transpiled = false;
                harmony.Patch(results, transpiler: Me(nameof(Stray_Transpiler)));
                return transpiled;
            });
            Try("subsystem hits", () =>
            {
                var hit = AccessTools.GetDeclaredMethods(typeof(AI.AgentShip)).FirstOrDefault(m => m.Name == "takeHit" && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(Ray));
                var damage = AccessTools.DeclaredMethod(typeof(AI.AgentShip), "JJEBPBHKBIE"); // ApplyDamage
                if (hit == null || damage == null) return false;
                applyDamage = AccessTools.MethodDelegate<ApplyDamageCall>(damage);
                transpiled = false;
                harmony.Patch(hit, transpiler: Me(nameof(Subsystem_Transpiler)));
                return transpiled;
            });
            Try("collision ray width", () =>
            {
                var offset = AccessTools.DeclaredMethod(typeof(AI.MBDOLNMLFDM), "GNNKBPMLPFI"); // CastCollisionRays.NextRayOffset
                if (offset == null) return false;
                harmony.Patch(offset, postfix: Me(nameof(RayOffset_Postfix)));
                return true;
            });
            Try("exclusion zones", () =>
            {
                avoidZones = AccessTools.Field(typeof(AI.AgentShip), "_avoidNavigationExclusionZones");
                var enable = AccessTools.DeclaredMethod(typeof(AI.DefaultShip), "JFBMOICFCEL"); // OnBodyEnabled
                if (avoidZones == null || enable == null) return false;
                harmony.Patch(enable, prefix: Me(nameof(Spawn_Prefix)));
                var marker = AccessTools.DeclaredMethod(typeof(AI.DCPPEEIGJAN), "IKLNPCHBLCA"); // SetAvoidMarkerAroundExclusionZone's tick
                if (marker != null) harmony.Patch(marker, prefix: Me(nameof(ZoneMarker_Prefix)));
                return true;
            });
            Plugin.Log.LogInfo($"Enemy AI extras: {ok} of {of} patched");
        }

        private static bool transpiled;

        // ---- Turret lock-on: any emitter ----------------------------------------------------------------------------------

        private static bool LockOn_Prefix(BeamTurret __instance)
        {
            if (!Fixes) return true;
            try
            {
                var target = turretTarget(__instance);
                if (target == null) return false; // the game's: nothing to change without a target
                var at = target.ALPAOOKFIEE.UnityPosition;
                var list = emitters(__instance);
                if (list == null) return true;
                float yawMin = minYaw(__instance), yawMax = maxYaw(__instance), pitchMin = minPitch(__instance), pitchMax = maxPitch(__instance);
                bool locked = false;
                foreach (var emitter in list)
                {
                    if (emitter == null) continue;
                    var toTarget = at - emitter.position;
                    float yaw = Vector3.Angle(emitter.forward, Vector3.ProjectOnPlane(toTarget, emitter.up));
                    float pitch = Vector3.Angle(emitter.forward, Vector3.ProjectOnPlane(toTarget, emitter.right));
                    if (yaw > yawMin && yaw < yawMax && pitch > pitchMin && pitch < pitchMax) { locked = true; break; }
                }
                setLockedOn(__instance, locked);
                if (locked && list.Length > 1 && !loggedLock)
                {
                    loggedLock = true;
                    Plugin.Log.LogInfo($"Enemy AI extras: {__instance.name}: locked on with any of its {list.Length} emitters");
                }
                return false;
            }
            catch { return true; }
        }

        // ---- Stray phaser hits ---------------------------------------------------------------------------------------------

        // Replaces "if (hit == group.Target)" with StrayHitCounts(hit, target, shooter).
        private static bool StrayHitCounts(Sector.HOLJLCLENCA hit, Sector.HOLJLCLENCA target, AI.AgentShip shooter)
        {
            if (ReferenceEquals(hit, target)) return true;
            if (!Fixes || hit == null || shooter == null) return false;
            try
            {
                var theirs = hit.GetTargetInfo();
                var ours = shooter.GetTargetInfo();
                if (theirs == null || ours == null) return false;
                var team = teamOf(theirs);
                if (Convert.ToInt32(team) == 0 || team.Equals(teamOf(ours))) return false; // no team, or our own side
                if (!loggedStray)
                {
                    loggedStray = true;
                    Plugin.Log.LogInfo($"Enemy AI extras: {shooter.name}: a phaser hit on a ship it wasn't aiming at did damage");
                }
                return true;
            }
            catch { return false; }
        }

        private static IEnumerable<CodeInstruction> Stray_Transpiler(IEnumerable<CodeInstruction> code)
        {
            var list = code.ToList();
            // ldarg.2 (hit), ..., callvirt FJKHPJOOAHN::get_Target(), bne.un <no damage>
            for (int i = 1; i < list.Count; i++)
            {
                if (list[i].opcode != OpCodes.Bne_Un && list[i].opcode != OpCodes.Bne_Un_S) continue;
                if (!(list[i - 1].operand is MethodInfo get) || get.Name != "get_Target") continue;
                var skip = list[i].operand;
                list[i] = new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(list[i]);
                list.Insert(i + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(EnemyAiExtras), nameof(StrayHitCounts))));
                list.Insert(i + 2, new CodeInstruction(OpCodes.Brfalse, skip));
                transpiled = true;
                break;
            }
            return list;
        }

        // ---- Subsystem hits: a share to the hull -----------------------------------------------------------------------

        private static float SubsystemDamage(float damage) => Smarter ? damage * SubsystemShare : damage;

        // Runs where the game checks "did a subsystem take it?": when one did, the hull takes the rest.
        private static bool HullTakesRest(AI.AgentShip ship, bool subsystemTookIt, float damage, Sector.HOLJLCLENCA attacker)
        {
            if (!subsystemTookIt || !Smarter || damage <= 0f) return subsystemTookIt;
            try
            {
                applyDamage(ship, damage * (1f - SubsystemShare), attacker, AI.LGGMICILCMN.Phaser, false, false);
                if (!loggedSplit)
                {
                    loggedSplit = true;
                    Plugin.Log.LogInfo($"Enemy AI extras: {ship.name}: a subsystem hit, {SubsystemShare:P0} to the subsystem and the rest to the hull");
                }
            }
            catch { }
            return true;
        }

        private static IEnumerable<CodeInstruction> Subsystem_Transpiler(IEnumerable<CodeInstruction> code)
        {
            var list = code.ToList();
            bool share = false, rest = false;
            for (int i = 1; i < list.Count; i++)
            {
                // ldarg.3 (damage), callvirt SubSystemTarget::Damage(Ray, float)
                if (!share && list[i].operand is MethodInfo damage && damage.Name == "Damage" && damage.DeclaringType == typeof(AI.SubSystemTarget)
                    && list[i - 1].opcode == OpCodes.Ldarg_3)
                {
                    list.Insert(i, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(EnemyAiExtras), nameof(SubsystemDamage))));
                    share = true;
                    i++;
                    continue;
                }
                // ldloc.0 (flag), brtrue <skip ApplyDamage>, ldarg.0, ldarg.3, ... callvirt JJEBPBHKBIE
                if (share && !rest && list[i].opcode == OpCodes.Ldloc_0 && i + 1 < list.Count
                    && (list[i + 1].opcode == OpCodes.Brtrue || list[i + 1].opcode == OpCodes.Brtrue_S))
                {
                    var load = list[i];
                    var first = new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(load);
                    list.Insert(i, first);
                    list.InsertRange(i + 2, new[]
                    {
                        new CodeInstruction(OpCodes.Ldarg_3),
                        new CodeInstruction(OpCodes.Ldarg_S, (byte)5), // the attacker
                        new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(EnemyAiExtras), nameof(HullTakesRest))),
                    });
                    rest = true;
                    break;
                }
            }
            transpiled = share && rest;
            return list;
        }

        // ---- Flying: ray width, exclusion zones, engine power ----------------------------------------------------------

        private static void RayOffset_Postfix(ref float __2)
        {
            if (Flying) __2 *= RayWidth;
        }

        private static readonly HashSet<int> switchedOn = new HashSet<int>(); // ships whose prefab had zones off (pooled: kept)

        private static void Spawn_Prefix(AI.DefaultShip __instance)
        {
            if (!Flying || __instance == null || __instance is AI.BorgCube) return;
            try
            {
                if (avoidZones.GetValue(__instance) is bool on && !on)
                {
                    avoidZones.SetValue(__instance, true);
                    switchedOn.Add(__instance.GetInstanceID());
                    if (!loggedZones)
                    {
                        loggedZones = true;
                        Plugin.Log.LogInfo($"Enemy AI extras: {__instance.name} steers round navigation exclusion zones");
                    }
                }
            }
            catch { }
        }

        // Destination inside the zone: no avoidance marker (Fail, as when the path misses the zone), so the ship flies
        // straight to it. Only for ships switched on above; Borg cubes and the Planet Killer keep the game's step.
        private static bool ZoneMarker_Prefix(AI.MNIJEDINGOI __0, ref AI.LECHLIEBBEM __result)
        {
            try
            {
                if (!(__0?.Owner is AI.AgentShip ship) || !switchedOn.Contains(ship.GetInstanceID()) || __0.NavMarker == null) return true;
                if (!AI.AINavigationExclusionZoneManager.IEFBKOMBFGO.IsBodyInExclusionZoneCluster(__0.ExclusionZoneClusterIndex, __0.NavMarker)) return true;
                __result = AI.LECHLIEBBEM.Fail;
                if (!loggedZoneTarget)
                {
                    loggedZoneTarget = true;
                    Plugin.Log.LogInfo($"Enemy AI extras: {ship.name}: where it's going is inside a zone; flying straight in");
                }
                return false;
            }
            catch { return true; }
        }
    }
}
