using HarmonyLib;
using Sector.Spacecraft;
using Sector.Spacecraft.Subsystems;
using UnityEngine;

namespace StbcRevival.Missions
{
    /// <summary>
    /// Fleet Battle: your ship (any player ship) takes 40% less damage, to its hull, shields and systems: the
    /// battle is meant to be won by the crew, and with dozens of enemies about a player ship went down too fast.
    /// Damage is worked out on the host (the subsystem servers' systemHit: HullServer's own, and the base
    /// SubsystemServer's that the shields and the rest use), so it's the host's game that takes it off.
    /// </summary>
    internal static class FleetBattleArmor
    {
        private const float DamageTaken = 0.6f;

        internal static void Patch(Harmony harmony)
        {
            foreach (var type in new[] { typeof(HullServer), typeof(SubsystemServer) })
            {
                var hit = AccessTools.DeclaredMethod(type, "systemHit", new[] { typeof(float) });
                if (hit == null) Plugin.Log.LogWarning($"Fleet battle: {type.Name}.systemHit not found; player ships take full damage");
                else harmony.Patch(hit, prefix: new HarmonyMethod(typeof(FleetBattleArmor), nameof(Hit_Prefix)));
            }
        }

        private static void Hit_Prefix(Component __instance, ref float __0)
        {
            if (__0 <= 0f) return;
            try
            {
                if (RevivalMissions.Selected != typeof(FleetBattleSequence)) return;
                if (__instance != null && __instance.GetComponentInParent<PlayerSpacecraft>() != null) __0 *= DamageTaken;
            }
            catch { /* full damage */ }
        }
    }
}
