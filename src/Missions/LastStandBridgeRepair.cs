using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival.Missions
{
    /// <summary>
    /// The bridge's damage effects (fires, sparks, scorch marks, blown-out panels) come on as the hull drops
    /// past thresholds (DamageManager.MKKPCHBHOJK) and the game never turns them off, since its ships don't
    /// heal. Last Stand repairs the hull between waves, so turn off the ones the hull is back above.
    /// Runs on every player's machine; the effects are local.
    /// Also put right after a repair:
    ///   - The Enterprise-D's bridge (TNGBridgeLightingManager) goes to auxiliary power lighting when the hull
    ///     drops far enough (its character lights, reflections and ceiling) and never comes back from it; and its
    ///     backwall science terminal turns to a dead panel with the damage there. Both are undone once the hull
    ///     is back above where they started.
    ///   - A damaged subsystem starts a looping alarm, which stops when it's repaired back into a window just
    ///     above where it started (PlayerSpacecraftSoundManager.PlaySubsystemAlarmRepaired): the engineer's
    ///     repairs climb through it, but a repair at a station jumps straight to 100% and the alarm went on
    ///     beeping. Past the window with an alarm still going, it's stopped as if in it. (For every mission.)
    /// </summary>
    internal static class LastStandBridgeRepair
    {
        private static readonly AccessTools.FieldRef<PersistentBridgeDamageEffect, MeshRenderer[]> MeshesToDisable =
            AccessTools.FieldRefAccess<PersistentBridgeDamageEffect, MeshRenderer[]>("_meshesToDisable");

        // DamageManager is internal to the game: reach it by name.
        private static readonly Type Manager = AccessTools.TypeByName("DamageManager");
        private static readonly System.Reflection.MethodInfo Hull = AccessTools.PropertyGetter(Manager, "AHOLJBPEIDO");
        private static readonly System.Reflection.MethodInfo MaxHull = AccessTools.PropertyGetter(Manager, "KEEIKMDLMJO");
        private static bool warned;

        internal static void Patch(Harmony harmony)
        {
            var update = Manager == null || Hull == null || MaxHull == null ? null : AccessTools.Method(Manager, "MKKPCHBHOJK");
            if (update == null)
            {
                Plugin.Log.LogWarning("Last Stand: DamageManager.MKKPCHBHOJK not found; bridge damage stays after repairs");
                return;
            }
            harmony.Patch(update, postfix: new HarmonyMethod(typeof(LastStandBridgeRepair), nameof(Update_Postfix)));
            var lightingEvent = AccessTools.Method(typeof(TNGBridgeLightingManager), "StartLightingEvent");
            if (lightingEvent != null) harmony.Patch(lightingEvent, prefix: new HarmonyMethod(typeof(LastStandBridgeRepair), nameof(LightingEvent_Prefix)));
            var alarmRepaired = AccessTools.Method(typeof(PlayerSpacecraftSoundManager), "PlaySubsystemAlarmRepaired");
            if (alarmRepaired == null) Plugin.Log.LogWarning("Bridge repair: PlaySubsystemAlarmRepaired not found; a subsystem alarm may go on after a station repair");
            else harmony.Patch(alarmRepaired, prefix: new HarmonyMethod(typeof(LastStandBridgeRepair), nameof(AlarmRepaired_Prefix)));
        }

        // ---- The subsystem alarm ----------------------------------------------------------------------

        private static readonly AccessTools.FieldRef<PlayerSpacecraftSoundManager, int> AlarmRepairPercent =
            AccessTools.FieldRefAccess<PlayerSpacecraftSoundManager, int>("_subsystemAlarmRepairPercentage");
        private static readonly AccessTools.FieldRef<PlayerSpacecraftSoundManager, int> AlarmsGoing =
            AccessTools.FieldRefAccess<PlayerSpacecraftSoundManager, int>("IPPNEKGMGCF");

        private static void AlarmRepaired_Prefix(PlayerSpacecraftSoundManager __instance, ref float __1)
        {
            int repaired = AlarmRepairPercent(__instance);
            if (__1 >= repaired && AlarmsGoing(__instance) > 0) __1 = repaired - 0.01f;
        }

        // ---- The Enterprise-D's lighting --------------------------------------------------------------

        private static readonly AccessTools.FieldRef<TNGBridgeLightingManager, bool> AuxiliaryPower =
            AccessTools.FieldRefAccess<TNGBridgeLightingManager, bool>("DNHINGPBHIF");
        private static readonly AccessTools.FieldRef<TNGBridgeLightingManager, float> AuxiliaryPowerPercent =
            AccessTools.FieldRefAccess<TNGBridgeLightingManager, float>("_auxiliaryPowerDamagePercent");
        private static readonly AccessTools.FieldRef<TNGBridgeLightingManager, Color> AuxiliaryCeiling =
            AccessTools.FieldRefAccess<TNGBridgeLightingManager, Color>("_auxiliaryPowerCeilingLightColor");
        private static readonly AccessTools.FieldRef<TNGBridgeLightingManager, Color> CeilingFadeFrom =
            AccessTools.FieldRefAccess<TNGBridgeLightingManager, Color>("CCJAGAHKINP");
        private static readonly AccessTools.FieldRef<TNGBridgeLightingManager, float> CeilingFade =
            AccessTools.FieldRefAccess<TNGBridgeLightingManager, float>("ODNFNELKJDL");   // its fade back (BatchUpdate), from CCJAGAHKINP
        private static readonly AccessTools.FieldRef<TNGBridgeLightingManager, float> AuxiliaryFade =
            AccessTools.FieldRefAccess<TNGBridgeLightingManager, float>("FPCJABMGCHB"); // its fade into auxiliary power
        private static readonly AccessTools.FieldRef<TNGBridgeLightingManager, MeshRenderer> BackwallTerminal =
            AccessTools.FieldRefAccess<TNGBridgeLightingManager, MeshRenderer>("_backwallScienceTerminalMesh");
        private static readonly AccessTools.FieldRef<BridgeLightingManager, ReflectionProbe> Probe =
            AccessTools.FieldRefAccess<BridgeLightingManager, ReflectionProbe>("_reflectionProbe");
        private static readonly System.Reflection.MethodInfo RedAlert = AccessTools.PropertyGetter(typeof(BridgeLightingManager), "CNNEPNDIIHM");

        private static Material backwallLive; // the backwall terminal's own panel, before the damage turned it dead

        // The terminal's damage (lighting event 0): its panel kept to put back.
        private static void LightingEvent_Prefix(TNGBridgeLightingManager __instance, int __0)
        {
            if (__0 == 0 && BackwallTerminal(__instance) != null) backwallLive = BackwallTerminal(__instance).sharedMaterial;
        }

        private static void RestoreLighting(float percent)
        {
            if (!(BridgeLightingManager.IEFBKOMBFGO is TNGBridgeLightingManager tng) || !AuxiliaryPower(tng) || percent <= AuxiliaryPowerPercent(tng)) return;
            AuxiliaryPower(tng) = false;
            AuxiliaryFade(tng) = -1f;
            CeilingFadeFrom(tng) = AuxiliaryCeiling(tng);
            CeilingFade(tng) = 0f; // back to its full ceiling light, and the lightmaps with it
            bool red = RedAlert?.Invoke(tng, null) is true;
            var t = Traverse.Create(tng);
            string kind = red ? "_redAlert" : "_default";
            CharacterLightingManager.IEFBKOMBFGO?.SetLights(t.Field(kind + "CharacterLight1").GetValue<Light>(),
                t.Field(kind + "CharacterLight2").GetValue<Light>(), t.Field(kind + "CharacterLight3").GetValue<Light>());
            var probe = Probe(tng);
            if (probe != null) probe.customBakedTexture = t.Field(red ? "_redAlertReflectionsMap" : "_defaultReflectionsMap").GetValue<Cubemap>();
            Plugin.Log.LogInfo($"Bridge repair: hull at {percent}%, the bridge's lighting back from auxiliary power");
        }

        private static void RestoreBackwall(PersistentBridgeDamageEffect effect)
        {
            if (backwallLive == null || Traverse.Create(effect).Field("_bridgeLightingEventId").GetValue<int>() != 0) return;
            if (BridgeLightingManager.IEFBKOMBFGO is TNGBridgeLightingManager tng && BackwallTerminal(tng) != null) BackwallTerminal(tng).sharedMaterial = backwallLive;
        }

        // Same hull percentage and threshold walk as the game's; effect i is on below threshold i.
        private static void Update_Postfix(object __instance, List<PersistentBridgeDamageEffect> ____persistentDamageEffectObjects,
                                           float[] ____damageThresholds, int[] ___HIHNENIJNKE)
        {
            if (!RevivalMissions.RepairsShip || ____persistentDamageEffectObjects == null || ____damageThresholds == null || ___HIHNENIJNKE == null) return;
            try
            {
                float max = (float)MaxHull.Invoke(__instance, null);
                if (max <= 0f) return;
                float percent = Mathf.Ceil((float)Hull.Invoke(__instance, null) / max * 100f);
                int cleared = 0;
                for (int i = 0; i < ___HIHNENIJNKE.Length && i < ____damageThresholds.Length; i++)
                {
                    if (____damageThresholds[i] > percent) continue;
                    var effect = ____persistentDamageEffectObjects[___HIHNENIJNKE[i]];
                    if (effect == null || !effect.BDGCIDFPKOF) continue;
                    effect.Stop();
                    RestoreBackwall(effect);
                    foreach (var mesh in MeshesToDisable(effect) ?? new MeshRenderer[0])
                        if (mesh != null) mesh.enabled = true;
                    cleared++;
                }
                if (cleared > 0) Plugin.Log.LogInfo($"Last Stand: hull at {percent}%, cleared {cleared} bridge damage effect(s)");
                RestoreLighting(percent);
            }
            catch (Exception e)
            {
                // Expected while the bridge loads (no ship yet for the hull values); say so once.
                if (!warned) Plugin.Log.LogInfo("Last Stand: bridge damage check not ready yet: " + (e.InnerException ?? e).Message);
                warned = true;
            }
        }
    }
}
