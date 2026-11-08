namespace StbcRevival.Missions
{
    /// <summary>
    /// The Warfare Anomaly's Borg cubes: our own variant, set up as the game's BorgVariants do (a spawn configurator
    /// run on the cube as it spawns). 6000 hull, regenerating 2 a second (10 every 5 s) all the time (no wait after damage). The
    /// game's Phase 2 (1500 hull, 50 a second after 10 s untouched) didn't hold out against forty Romulan ships.
    /// (Keeping the cubes apart is PatrolWarfareAggro's: their exclusion-zone avoidance is already on, and does nothing.)
    /// </summary>
    public sealed class BorgWarfareVariant : Mission.Tables.GOCDBGLDMFC // SpawnConfigurator
    {
        internal const float Hull = 6000f, RegenPerSecond = 2f; // 10 every 5 s, applied smoothly (the game adds rate x frame time)

        public override void Run(Sector.SectorBody body)
        {
            if (!(body is AI.BorgCube cube)) return;
            cube.DCPNJKKALHJ = Hull;                     // MaxHealth
            cube.BJFNOOMJLON = true;                     // HullRegenerationEnabled
            cube.CHHOOOBLDIG = RegenPerSecond;           // HullRegenerationRate
            cube.CFCBEOKFDHG = 0f;                       // HullRegenerationWaitTime
            Plugin.Log.LogInfo($"Patrol: warfare variant on {cube.name}: {Hull:0} hull, regenerating {RegenPerSecond:0}/s");
        }
    }
}
