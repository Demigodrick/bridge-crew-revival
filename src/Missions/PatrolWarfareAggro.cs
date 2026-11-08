using System;
using System.Collections.Generic;
using HarmonyLib;

namespace StbcRevival.Missions
{
    /// <summary>
    /// Patrol's Warfare Anomaly: the Romulan ships fighting there leave the player's ship alone while the Borg are
    /// about (the Borg come for it, as cubes do). The AI picks its targets from an aggro list (AI blackboard
    /// AddAgro): anything of another team in range goes on it. For the ships in Ships, AddAgro does nothing when
    /// the body is the player's ship, so they only fight the Borg (even when fired on); Patrol empties it once the
    /// cubes are gone and orders the Romulans at the ship.
    /// Damage between the two sides is scaled as Patrol's auto-balance sets it: AI ships (cubes included) take phaser
    /// and torpedo hits in AgentShip.takeHit (no Romulan ship or cube overrides it), whose damage is multiplied by
    /// BorgDamage for a cube's hit on a Romulan and by RomulanDamage for a Romulan's hit on a cube. Hits on or from
    /// anything else (the player's ship among them) are left as they are.
    /// And the cubes keep apart: a cube flies by its own UpdateMoveVelocity (BorgCube's, MHHGGHHKMKN), which steers
    /// for its nav marker and takes no notice of other cubes (even with exclusion-zone avoidance on, which it already
    /// is), so they flew through each other. After it, a cube closer than CubeSpacing (beyond both hulls) to another of
    /// the battle's cubes gets a push straight away from it, up to CubePush m/s, harder the closer they are.
    /// </summary>
    internal static class PatrolWarfareAggro
    {
        /// <summary>The ships (their SectorBody) leaving the player's ship alone for now; empty when there's none.</summary>
        internal static readonly HashSet<object> Ships = new HashSet<object>();

        /// <summary>The battle's Borg cubes (their SectorBody).</summary>
        internal static readonly HashSet<object> Cubes = new HashSet<object>();

        /// <summary>Multipliers on the Borg's hits on Romulans and the Romulans' on the Borg (Patrol's auto-balance).</summary>
        internal static float BorgDamage = 1f, RomulanDamage = 1f;
        private const double CubeSpacing = 1500; // metres kept between two cubes' hulls
        private const double CubePush = 300;     // m/s pushing apart at the closest
        private static bool loggedBorgHit, loggedRomulanHit, loggedPush;
        private static readonly AccessTools.FieldRef<AI.AgentShip, UnityEngine.Vector3d> MoveVelocity =
            AccessTools.FieldRefAccess<AI.AgentShip, UnityEngine.Vector3d>("KEIEAFFMBPF"); // _moveVelocity: this step's move, metres

        internal static void Clear()
        {
            Ships.Clear();
            Cubes.Clear();
            loggedBorgHit = loggedRomulanHit = loggedPush = false;
            BorgDamage = RomulanDamage = 1f;
        }

        internal static void Patch(Harmony harmony)
        {
            var add = AccessTools.Method(typeof(AI.MNIJEDINGOI), "AddAgro");
            if (add == null)
            {
                Plugin.Log.LogWarning("Patrol: the AI's AddAgro wasn't found; the warfare anomaly's ships may attack the player");
                return;
            }
            harmony.Patch(add, prefix: new HarmonyMethod(typeof(PatrolWarfareAggro), nameof(AddAgro_Prefix)));

            // takeHit(Ray ray, float, float damage, Subsystem, attacker, ...): phasers;
            // takeHit(Vector3 at, float damage, Subsystem, attacker, ...): torpedoes. The attacker is the game's
            // sector body interface (HOLJLCLENCA), not SectorBody, so it's taken as an object.
            int patched = 0;
            foreach (var hit in typeof(AI.AgentShip).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (hit.Name != "takeHit") continue;
                var args = hit.GetParameters();
                if (args.Length >= 5 && args[0].ParameterType == typeof(UnityEngine.Ray) && args[2].ParameterType == typeof(float) && !args[4].ParameterType.IsValueType)
                    harmony.Patch(hit, prefix: new HarmonyMethod(typeof(PatrolWarfareAggro), nameof(PhaserHit_Prefix)));
                else if (args.Length >= 4 && args[0].ParameterType == typeof(UnityEngine.Vector3) && args[1].ParameterType == typeof(float) && !args[3].ParameterType.IsValueType)
                    harmony.Patch(hit, prefix: new HarmonyMethod(typeof(PatrolWarfareAggro), nameof(TorpedoHit_Prefix)));
                else continue;
                patched++;
            }
            if (patched == 2) Plugin.Log.LogInfo("Patrol: warfare anomaly damage balance ready (2 takeHit methods)");
            else Plugin.Log.LogWarning($"Patrol: {patched} of the AI's 2 takeHit methods found; the warfare anomaly's damage may not be balanced");

            var move = AccessTools.DeclaredMethod(typeof(AI.BorgCube), "MHHGGHHKMKN"); // UpdateMoveVelocity
            if (move == null) Plugin.Log.LogWarning("Patrol: the Borg cube's UpdateMoveVelocity wasn't found; the warfare anomaly's cubes may fly through each other");
            else harmony.Patch(move, postfix: new HarmonyMethod(typeof(PatrolWarfareAggro), nameof(CubeMove_Postfix)));
        }

        private static void CubeMove_Postfix(AI.BorgCube __instance)
        {
            if (Cubes.Count < 2 || !Cubes.Contains(__instance)) return;
            try
            {
                var me = __instance.ALPAOOKFIEE;
                if (me == null) return;
                double step = CubePush * UnityEngine.Time.fixedDeltaTime;
                double x = 0, y = 0, z = 0;
                foreach (var other in Cubes)
                {
                    if (ReferenceEquals(other, __instance) || !(other is AI.BorgCube cube) || cube == null || !cube.gameObject.activeInHierarchy) continue;
                    var them = cube.ALPAOOKFIEE;
                    if (them == null || them.System != me.System) continue;
                    double dx = me.Position.x - them.Position.x, dy = me.Position.y - them.Position.y, dz = me.Position.z - them.Position.z;
                    double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    double keep = Radius(me) + Radius(them) + CubeSpacing;
                    if (distance >= keep) continue;
                    if (distance < 1) { dx = 1; dy = 0; dz = 0; distance = 1; } // on top of each other: any way apart
                    double push = step * Math.Min(1, 2 * (1 - distance / keep)); // harder the closer
                    x += dx / distance * push; y += dy / distance * push; z += dz / distance * push;
                    if (!loggedPush)
                    {
                        loggedPush = true;
                        Plugin.Log.LogInfo($"Patrol: warfare, {__instance.name} {distance / 1000:0.0} km from {cube.name}, keeping {keep / 1000:0.0} km apart; pushed away");
                    }
                }
                if (x == 0 && y == 0 && z == 0) return;
                ref var velocity = ref MoveVelocity(__instance);
                velocity = new UnityEngine.Vector3d(velocity.x + x, velocity.y + y, velocity.z + z);
            }
            catch (Exception)
            {
                // a cube going away mid-step
            }
        }

        private static double Radius(SystemTransform body)
        {
            double r = body.Radius;
            return r > 0 && r < 1e5 ? r : 0;
        }

        private static void PhaserHit_Prefix(AI.AgentShip __instance, ref float __2, object __4) => Scale(__instance, ref __2, __4, "phaser");

        private static void TorpedoHit_Prefix(AI.AgentShip __instance, ref float __1, object __3) => Scale(__instance, ref __1, __3, "torpedo");

        private static void Scale(AI.AgentShip target, ref float damage, object attacker, string weapon)
        {
            if (Cubes.Count == 0 || attacker == null || target == null) return;
            if (Cubes.Contains(attacker) && Ships.Contains(target))
            {
                if (!loggedBorgHit)
                {
                    loggedBorgHit = true;
                    Plugin.Log.LogInfo($"Patrol: warfare, a Borg cube's {weapon} hit on a Romulan: {damage:0.#}, made {damage * BorgDamage:0.#} (x{BorgDamage:0.0})");
                }
                damage *= BorgDamage;
            }
            else if (Ships.Contains(attacker) && Cubes.Contains(target))
            {
                if (!loggedRomulanHit)
                {
                    loggedRomulanHit = true;
                    Plugin.Log.LogInfo($"Patrol: warfare, a Romulan's {weapon} hit on a cube: {damage:0.#}, made {damage * RomulanDamage:0.#} (x{RomulanDamage:0.0})");
                }
                damage *= RomulanDamage;
            }
        }

        private static bool AddAgro_Prefix(AI.MNIJEDINGOI __instance, Sector.SectorBody __0)
        {
            if (Ships.Count == 0 || __0 == null) return true;
            try
            {
                var owner = __instance.Owner?.AHMPFHDGMKJ;
                if (owner == null || !Ships.Contains(owner)) return true;
                return !(__0 == __instance.PlayerShip || __0 is Sector.Spacecraft.PlayerSpacecraft);
            }
            catch (Exception)
            {
                return true;
            }
        }
    }
}
