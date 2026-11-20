using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// Stage 5 of the enemy AI work: per-ship tuning as ships spawn.
    ///
    /// - Weapons with no pattern to use them: the Romulan Scout carries a sniper beam and
    ///   mines, and the D'deridex a sniper beam, but no prefab gives them the attack patterns that fire those (only some
    ///   spawn variants do). A ship with a PhaserSnipe group and no SnipeSubSystem pattern now gets "Romulan Scout -
    ///   Snipe"; one with a Mine group and no LayMines pattern gets "Romulan Scout - Mine Layer". Added to AttackPatterns
    ///   in a prefix on DefaultShip.OnBodyEnabled (JFBMOICFCEL), after the spawn variants ran and before the trees are
    ///   built; the patterns are tried in a random order (EnemyAiTactics), so these come up among the others.
    /// Enemy ships fire at the game's own rate (the fire rate setting is gone).
    ///
    /// Left as the game has them on purpose: hailing (the hail branch makes a ship stop firing and circle a damaged target
    /// while it hails, but only mission scripts hear the hail and Revival's don't, so it would look like ships giving up),
    /// and retreats (Romulans and pirates already retreat on shields, and Klingons never do, which stays).
    /// </summary>
    internal static class EnemyAiTuning
    {
        private const string SnipePattern = "AI Attack Patterns/Romulan Scout - Snipe";
        private const string MinePattern = "AI Attack Patterns/Romulan Scout - Mine Layer";

        private static AI.AttackPattern snipe, mines;
        private static bool loggedSnipe, loggedMines;
        private static System.Reflection.FieldInfo teamField;

        internal static void Patch(Harmony harmony)
        {
            var enable = AccessTools.DeclaredMethod(typeof(AI.DefaultShip), "JFBMOICFCEL"); // OnBodyEnabled (calls AgentShip's, which builds the trees)
            teamField = AccessTools.Field(typeof(AI.AgentShip), "_teamID");
            if (enable == null)
            {
                Plugin.Log.LogWarning("Enemy AI tuning: DefaultShip.OnBodyEnabled wasn't found; left as the game has it");
                return;
            }
            harmony.Patch(enable, prefix: new HarmonyMethod(typeof(EnemyAiTuning), nameof(Enabled_Prefix)));
            Plugin.Log.LogInfo("Enemy AI tuning: patched (unused weapons get attack patterns)");
        }

        private static void Enabled_Prefix(AI.DefaultShip __instance)
        {
            if (__instance == null) return;
            try { AddUnusedWeaponPatterns(__instance); }
            catch (Exception e) { Plugin.Log.LogWarning("Enemy AI tuning: patterns: " + e.Message); }
        }

        // ---- Sniper beams and mines ------------------------------------------------------------------------------------

        private static void AddUnusedWeaponPatterns(AI.DefaultShip ship)
        {
            var patterns = ship.AttackPatterns;
            if (patterns == null || !(ship is AI.DefaultShip) || ship is AI.BorgCube) return;
            bool hasSnipe = false, hasMines = false;
            foreach (var group in ship.GetComponentsInChildren<Sector.Spacecraft.NPCWeaponGroup>(true))
                if (group != null && group.FCBJFOCMKEA == Sector.Spacecraft.MDMEPIGBOFJ.PhaserSnipe) hasSnipe = true;
            foreach (var group in ship.GetComponentsInChildren<Sector.Spacecraft.NPCTorpedoGroup>(true))
                if (group != null && group.FCBJFOCMKEA == Sector.Spacecraft.MDMEPIGBOFJ.Mine) hasMines = true;
            if (!hasSnipe && !hasMines) return;

            bool snipes = false, lays = false;
            foreach (var pattern in patterns)
            {
                if (pattern is AI.SnipeSubSystem) snipes = true;
                if (pattern is AI.LayMines) lays = true;
            }
            if (hasSnipe && !snipes)
            {
                if (snipe == null) snipe = Resources.Load(SnipePattern) as AI.AttackPattern;
                if (snipe != null)
                {
                    patterns.Add(snipe);
                    if (!loggedSnipe) { loggedSnipe = true; Plugin.Log.LogInfo($"Enemy AI tuning: {ship.name} has a sniper beam: sniper attack added"); }
                }
            }
            if (hasMines && !lays)
            {
                if (mines == null) mines = Resources.Load(MinePattern) as AI.AttackPattern;
                if (mines != null)
                {
                    patterns.Add(mines);
                    if (!loggedMines) { loggedMines = true; Plugin.Log.LogInfo($"Enemy AI tuning: {ship.name} carries mines: mine laying added"); }
                }
            }
        }

        internal static bool HostileTeam(AI.AgentShip ship)
        {
            if (teamField == null) return false;
            int team = Convert.ToInt32(teamField.GetValue(ship)); // Sector.Team: 2 Klingon, 4 hostile, 5 Romulan, 6 Borg
            return team == 2 || team == 4 || team == 5 || team == 6;
        }
    }
}
