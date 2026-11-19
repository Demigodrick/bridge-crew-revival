using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// Stage 5 of the enemy AI work: per-ship tuning as ships spawn.
    ///
    /// - Weapons with no pattern to use them (setting EnemyAI.FightSmarter): the Romulan Scout carries a sniper beam and
    ///   mines, and the D'deridex a sniper beam, but no prefab gives them the attack patterns that fire those (only some
    ///   spawn variants do). A ship with a PhaserSnipe group and no SnipeSubSystem pattern now gets "Romulan Scout -
    ///   Snipe"; one with a Mine group and no LayMines pattern gets "Romulan Scout - Mine Layer". Added to AttackPatterns
    ///   in a prefix on DefaultShip.OnBodyEnabled (JFBMOICFCEL), after the spawn variants ran and before the trees are
    ///   built; with FightSmarter on, the patterns are tried in a random order, so these come up among the others.
    /// - Enemy fire rate (setting EnemyAI.FireRate, a share of the game's): every weapon group of a ship on a team hostile
    ///   to the player (Klingon, Romulan, pirates, Borg) has its cooldowns and shot-token return times divided by it. Set
    ///   from the ship's own values each spawn (ships are pooled), so it applies to ships that spawn after a change.
    ///
    /// Left as the game has them on purpose: hailing (the hail branch makes a ship stop firing and circle a damaged target
    /// while it hails, but only mission scripts hear the hail and Revival's don't, so it would look like ships giving up),
    /// and retreats (Romulans and pirates already retreat on shields, and Klingons never do, which stays).
    /// </summary>
    internal static class EnemyAiTuning
    {
        internal static readonly float[] FireRates = { 0.75f, 1f, 1.25f, 1.5f };

        private const string SnipePattern = "AI Attack Patterns/Romulan Scout - Snipe";
        private const string MinePattern = "AI Attack Patterns/Romulan Scout - Mine Layer";

        private static AI.AttackPattern snipe, mines;
        private static bool loggedSnipe, loggedMines, loggedRate;
        private static System.Reflection.FieldInfo teamField;
        private static readonly Dictionary<int, float[]> gameValues = new Dictionary<int, float[]>(); // per weapon group: its own values

        private static readonly string[] PhaserFields = { "_phaserCooldown", "_weakCooldown", "_strongCooldown", "_tokenReturnTimeout" };
        private static readonly string[] TorpedoFields = { "_tokenReturnTimeout" };

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
            Plugin.Log.LogInfo($"Enemy AI tuning: patched (unused weapons {(Plugin.EnemyAiFightSmarter.Value ? "on" : "off")}, fire rate {Plugin.EnemyAiFireRate.Value:0.##})");
        }

        private static void Enabled_Prefix(AI.DefaultShip __instance)
        {
            if (__instance == null) return;
            try { if (Plugin.EnemyAiFightSmarter.Value) AddUnusedWeaponPatterns(__instance); }
            catch (Exception e) { Plugin.Log.LogWarning("Enemy AI tuning: patterns: " + e.Message); }
            try { SetFireRate(__instance); }
            catch (Exception e) { Plugin.Log.LogWarning("Enemy AI tuning: fire rate: " + e.Message); }
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

        // ---- Fire rate --------------------------------------------------------------------------------------------------

        private static bool HostileTeam(AI.AgentShip ship)
        {
            if (teamField == null) return false;
            int team = Convert.ToInt32(teamField.GetValue(ship)); // Sector.Team: 2 Klingon, 4 hostile, 5 Romulan, 6 Borg
            return team == 2 || team == 4 || team == 5 || team == 6;
        }

        private static void SetFireRate(AI.AgentShip ship)
        {
            float rate = Mathf.Clamp(Plugin.EnemyAiFireRate.Value, 0.25f, 4f);
            bool hostile = HostileTeam(ship);
            foreach (var group in ship.GetComponentsInChildren<Sector.Spacecraft.NPCWeaponGroup>(true)) Scale(group, PhaserFields, hostile ? rate : 1f);
            foreach (var group in ship.GetComponentsInChildren<Sector.Spacecraft.NPCTorpedoGroup>(true)) Scale(group, TorpedoFields, hostile ? rate : 1f);
            if (hostile && rate != 1f && !loggedRate)
            {
                loggedRate = true;
                Plugin.Log.LogInfo($"Enemy AI tuning: {ship.name}: weapons fire at {rate:0.##}x the game's rate");
            }
        }

        private static void Scale(Component group, string[] fields, float rate)
        {
            if (group == null) return;
            int id = group.GetInstanceID();
            var type = group.GetType();
            if (!gameValues.TryGetValue(id, out var original))
            {
                if (rate == 1f) return; // never changed: nothing to put back
                original = new float[fields.Length];
                for (int i = 0; i < fields.Length; i++) original[i] = (float)AccessTools.Field(type, fields[i]).GetValue(group);
                gameValues[id] = original;
            }
            for (int i = 0; i < fields.Length; i++) AccessTools.Field(type, fields[i]).SetValue(group, original[i] / rate);
        }
    }
}
