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
    /// </summary>
    internal static class LastStandBridgeRepair
    {
        private static readonly AccessTools.FieldRef<PersistentBridgeDamageEffect, MeshRenderer[]> MeshesToDisable =
            AccessTools.FieldRefAccess<PersistentBridgeDamageEffect, MeshRenderer[]>("_meshesToDisable");

        // DamageManager is internal to the game: reach it by name.
        private static readonly Type Manager = AccessTools.TypeByName("DamageManager");
        private static readonly System.Reflection.MethodInfo Hull = AccessTools.PropertyGetter(Manager, "AHOLJBPEIDO");
        private static readonly System.Reflection.MethodInfo MaxHull = AccessTools.PropertyGetter(Manager, "KEEIKMDLMJO");
        private static bool failed;

        internal static void Patch(Harmony harmony)
        {
            var update = Manager == null || Hull == null || MaxHull == null ? null : AccessTools.Method(Manager, "MKKPCHBHOJK");
            if (update == null)
            {
                Plugin.Log.LogWarning("Last Stand: DamageManager.MKKPCHBHOJK not found; bridge damage stays after repairs");
                return;
            }
            harmony.Patch(update, postfix: new HarmonyMethod(typeof(LastStandBridgeRepair), nameof(Update_Postfix)));
        }

        // Same hull percentage and threshold walk as the game's; effect i is on below threshold i.
        private static void Update_Postfix(object __instance, List<PersistentBridgeDamageEffect> ____persistentDamageEffectObjects,
                                           float[] ____damageThresholds, int[] ___HIHNENIJNKE)
        {
            if (failed || !LastStandLocks.Locked || ____persistentDamageEffectObjects == null || ____damageThresholds == null || ___HIHNENIJNKE == null) return;
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
                    foreach (var mesh in MeshesToDisable(effect) ?? new MeshRenderer[0])
                        if (mesh != null) mesh.enabled = true;
                    cleared++;
                }
                if (cleared > 0) Plugin.Log.LogInfo($"Last Stand: hull at {percent}%, cleared {cleared} bridge damage effect(s)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Last Stand: clearing bridge damage: " + e.Message);
                failed = true;
            }
        }
    }
}
