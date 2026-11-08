using HarmonyLib;

namespace StbcRevival.Missions
{
    /// <summary>
    /// Fleet Battle: enemy ships don't cloak. In a battle of dozens, a cloaked ship only made the crew hunt
    /// for the last few for minutes, and ships without a way to find cloaked ones couldn't finish at all.
    /// AgentShip.CPMKGMJPINE is the AI's "cloak now" (it sends Multicast_SetCloak(true) to everyone); while
    /// Fleet Battle runs it does nothing. Decloaking is left alone.
    /// The same for the Romulans of Patrol's Warfare Anomaly while they fight the Borg (PatrolWarfareAggro.Ships):
    /// cloaking, they stalled the battle (the cubes lost their targets while the Romulans' shields came back).
    /// </summary>
    internal static class FleetBattleNoCloak
    {
        internal static void Patch(Harmony harmony)
        {
            var cloak = AccessTools.Method(typeof(AI.AgentShip), "CPMKGMJPINE");
            if (cloak == null)
            {
                Plugin.Log.LogWarning("Fleet battle: the AI's cloak wasn't found; enemies may cloak");
                return;
            }
            harmony.Patch(cloak, prefix: new HarmonyMethod(typeof(FleetBattleNoCloak), nameof(Cloak_Prefix)));
        }

        private static bool Cloak_Prefix(AI.AgentShip __instance)
        {
            try { return RevivalMissions.Selected != typeof(FleetBattleSequence) && !PatrolWarfareAggro.Ships.Contains(__instance); }
            catch { return true; }
        }
    }
}
