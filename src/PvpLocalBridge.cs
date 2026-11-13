using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace StbcRevival
{
    /// <summary>
    /// PvP, each crew on its own ship's bridge: the bridge's own life (its red shirts' idling, chatter, red alert and
    /// reactions to hits; the hull-hit shakes, sparks and lasting damage) is run by the host alone, for the host's
    /// bridge and ship (RedShirtManager.BatchUpdate and DamageManager.CheckForHullDamage are master-only), and sent
    /// to everyone. The other ship's crew now keep the host's bridge out (PvpBridgeViews), so their bridge stood
    /// still: idle red shirts, no reaction to their own ship's hits. On a game crewing the other ship than the
    /// host's, both run here, for this bridge and this game's ship, and what they'd send to the room
    /// (Extensions.SSRPC multicasts) is called here instead: nothing of it leaves this game.
    /// </summary>
    internal static class PvpLocalBridge
    {
        [ThreadStatic] private static bool localOnly;
        private static FieldInfo captainCache, effectOrderReceived;
        private static MethodInfo checkHull;
        private static readonly HashSet<string> told = new HashSet<string>();

        internal static void Patch(Harmony harmony)
        {
            var redShirts = AccessTools.TypeByName("RedShirtManager");
            var damage = AccessTools.TypeByName("DamageManager");
            var update = redShirts == null ? null : AccessTools.Method(redShirts, "BatchUpdate");
            var damageUpdate = damage == null ? null : AccessTools.Method(damage, "BatchUpdate");
            var extensions = AccessTools.TypeByName("MCGGBALEIDD"); // Extensions
            var ssrpc = extensions == null ? null : AccessTools.Method(extensions, "SSRPC",
                new[] { AccessTools.TypeByName("PhotonView"), typeof(object), typeof(string), AccessTools.TypeByName("PDPMOIKGAAE") /* PhotonTargets */, typeof(object[]) });
            captainCache = redShirts == null ? null : AccessTools.Field(redShirts, "JCMENEPLBBD");     // _consoleCaptain
            effectOrderReceived = damage == null ? null : AccessTools.Field(damage, "GDGCJGNFJHJ");   // _effectOrderReceived
            checkHull = damage == null ? null : AccessTools.Method(damage, "FPFFHMHBCNC");            // CheckForHullDamage
            if (update == null || damageUpdate == null || ssrpc == null || effectOrderReceived == null || checkHull == null)
            {
                Plugin.Log.LogWarning("PvP: the bridge's red shirts or hull damage weren't all found; the other ship's bridge stays still");
                return;
            }
            harmony.Patch(update, prefix: new HarmonyMethod(typeof(PvpLocalBridge), nameof(RedShirts_Prefix)),
                transpiler: new HarmonyMethod(typeof(PvpLocalBridge), nameof(RedShirts_Transpiler)),
                finalizer: new HarmonyMethod(typeof(PvpLocalBridge), nameof(Local_Finalizer)));
            harmony.Patch(damageUpdate, postfix: new HarmonyMethod(typeof(PvpLocalBridge), nameof(Damage_Postfix)));
            harmony.Patch(ssrpc, prefix: new HarmonyMethod(typeof(PvpLocalBridge), nameof(Send_Prefix)));
            Plugin.Log.LogInfo("PvP: the other ship's crew run their own bridge's red shirts and hull damage");
        }

        /// <summary>This game runs its own bridge: it crews the other ship than the host's.</summary>
        private static bool Local => PvpBridgeViews.OtherShipThanHost();

        /// <summary>RedShirtManager.BatchUpdate's "am I the master" check: the master, or a game running its own bridge.</summary>
        public static bool Drives()
        {
            var master = Patches.PhotonNetworkType?.GetMethod("get_isMasterClient", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) is true;
            return master || Local;
        }

        private static IEnumerable<CodeInstruction> RedShirts_Transpiler(IEnumerable<CodeInstruction> code)
        {
            var isMaster = Patches.PhotonNetworkType?.GetMethod("get_isMasterClient", BindingFlags.Public | BindingFlags.Static);
            var drives = AccessTools.Method(typeof(PvpLocalBridge), nameof(Drives));
            bool replaced = false;
            foreach (var instruction in code)
            {
                if (!replaced && isMaster != null && (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && instruction.operand as MethodInfo == isMaster)
                {
                    replaced = true;
                    yield return new CodeInstruction(OpCodes.Call, drives) { labels = instruction.labels, blocks = instruction.blocks };
                    continue;
                }
                yield return instruction;
            }
            if (!replaced) Plugin.Log.LogWarning("PvP: RedShirtManager.BatchUpdate's master check not found; the other ship's red shirts stay still");
        }

        private static void RedShirts_Prefix(object __instance)
        {
            if (!Local) return;
            localOnly = true;
            // Its red alert follows the captain console it cached at level load: this game's own ship's.
            if (captainCache != null && PvpScope.Applied == null && Bridge.PKCFGMELINM != null && !ReferenceEquals(captainCache.GetValue(__instance), Bridge.PKCFGMELINM))
                captainCache.SetValue(__instance, Bridge.PKCFGMELINM);
            if (told.Add("redshirts")) Plugin.Log.LogInfo("PvP: this bridge's red shirts run here (your ship's bridge)");
        }

        private static Exception Local_Finalizer(Exception __exception)
        {
            localOnly = false;
            return __exception;
        }

        // DamageManager.BatchUpdate checks the hull only on the master: here too, for this game's own ship, with
        // its own effect order (its Awake's, valid for its own bridge; the host's isn't taken: PvpBridgeViews).
        private static void Damage_Postfix(object __instance)
        {
            if (!Local) return;
            effectOrderReceived.SetValue(__instance, true);
            localOnly = true;
            try { checkHull.Invoke(__instance, null); }
            catch (Exception e) { if (told.Add("hull")) Plugin.Log.LogWarning("PvP: this bridge's hull damage check: " + (e.InnerException ?? e).Message); }
            finally { localOnly = false; }
            if (told.Add("damage")) Plugin.Log.LogInfo("PvP: this bridge's hull damage runs here (your ship's hits)");
        }

        // A multicast from this bridge's own red shirts or damage: called here, as SSRPC does offline, and not sent.
        private static bool Send_Prefix(object __1, string __2, object[] __4)
        {
            if (!localOnly || __1 == null || __2 == null) return true;
            var method = __1.GetType().GetMethod(__2, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
            if (method == null) return true;
            PvpRouteDiag.Count($"{__2} kept on this bridge");
            try { method.Invoke(__1, __4); }
            catch (Exception e) { if (told.Add("call" + __2)) Plugin.Log.LogWarning($"PvP: {__2} on this bridge: " + (e.InnerException ?? e).Message); }
            return false;
        }
    }
}
