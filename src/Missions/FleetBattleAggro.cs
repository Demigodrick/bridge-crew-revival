using System;
using HarmonyLib;

namespace StbcRevival.Missions
{
    /// <summary>
    /// Fleet Battle: the enemy has no preference for your ship. The AI picks the target it has the most aggro on
    /// (AI blackboard AddAgro), and AddAgro adds 50 whenever the body is the player's ship: on first sight (110
    /// against 60 for an ally) and on every hit you land on it (60 against 10). In Fleet Battle that 50 is taken off,
    /// so your ship counts as any allied ship does: the enemy goes for whoever is near or shooting at it.
    /// Aggro is the host's (the AI runs there), so it's the host's game that changes it.
    /// </summary>
    internal static class FleetBattleAggro
    {
        private const float PlayerBonus = 50f; // AddAgro's own, for the blackboard's PlayerShip
        private static bool logged;

        internal static void Patch(Harmony harmony)
        {
            var add = AccessTools.Method(typeof(AI.MNIJEDINGOI), "AddAgro");
            if (add == null)
            {
                Plugin.Log.LogWarning("Fleet battle: the AI's AddAgro wasn't found; the enemy prefers your ship as usual");
                return;
            }
            harmony.Patch(add, prefix: new HarmonyMethod(typeof(FleetBattleAggro), nameof(AddAgro_Prefix)));
        }

        // AddAgro(body, amount, convoy): the bonus is added to amount when body is PlayerShip, so it's taken off first.
        private static void AddAgro_Prefix(AI.MNIJEDINGOI __instance, object __0, ref float __1)
        {
            if (__0 == null) return;
            try
            {
                if (!ReferenceEquals(__0, __instance.PlayerShip) || RevivalMissions.Selected != typeof(FleetBattleSequence)) return;
                __1 -= PlayerBonus;
                if (!logged)
                {
                    logged = true;
                    Plugin.Log.LogInfo("Fleet battle: the enemy's preference for your ship is off (your ship's aggro counts as an ally's)");
                }
            }
            catch (Exception) { /* the game's own aggro */ }
        }
    }
}
