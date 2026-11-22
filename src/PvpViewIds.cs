using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace StbcRevival
{
    /// <summary>
    /// PvP, each crew on its own bridge: every network object has an ID, and a bridge scene's own (the Bridge, its red
    /// shirts) are baked in, different per bridge (the Bridge: Aegis 130, original Enterprise 127, Enterprise-D 884;
    /// red shirts 150, 150, 357). The host numbers the star system's objects (SectorBody: PhotonNetwork.
    /// AllocateSceneViewID) skipping only the numbers in use on its own game, so with the host on the original
    /// Enterprise an anomaly cloud got 130; on a crew member's Aegis bridge that was the Bridge's, and the network code
    /// destroyed the older one ("PhotonView ID duplicate found ... Destroying old entry"): the whole bridge, its
    /// stations with it, and every seating after that failed (GetStation: index out of range).
    ///   - The host skips the known bridges' numbers.
    ///   - Each game, if a system object still arrives with a number its own bridge scene holds, moves its bridge's
    ///     object to a free number instead of losing it (the Bridge's RPCs are already found by its own view:
    ///     PvpBridgeViews).
    /// </summary>
    internal static class PvpViewIds
    {
        private static readonly HashSet<int> BridgeIds = new HashSet<int> { 127, 130, 150, 357, 884 };
        private static FieldInfo viewList, manual;
        private static bool allocating;

        internal static void Patch(Harmony harmony)
        {
            var photon = AccessTools.TypeByName("NDIOGIGMPBH");                                    // PhotonNetwork
            var peer = AccessTools.TypeByName("PKJCFCINBFA");                                      // NetworkingPeer
            var allocate = photon == null ? null : AccessTools.Method(photon, "AllocateSceneViewID", System.Type.EmptyTypes);
            var register = peer == null ? null : AccessTools.Method(peer, "RegisterPhotonView");
            viewList = peer == null ? null : AccessTools.Field(peer, "IKCMGIMLHOF");              // photonViewList
            manual = photon == null ? null : AccessTools.Field(photon, "EIDAMJNKMDK");            // manuallyAllocatedViewIds
            if (allocate != null) harmony.Patch(allocate, postfix: new HarmonyMethod(typeof(PvpViewIds), nameof(Allocate_Postfix)));
            if (register != null && viewList != null) harmony.Patch(register, prefix: new HarmonyMethod(typeof(PvpViewIds), nameof(Register_Prefix)));
            if (allocate == null || register == null || viewList == null)
                Plugin.Log.LogWarning("PvP: the network ID parts weren't all found; a crew on another bridge may lose theirs to a star system object");
            else Plugin.Log.LogInfo("PvP: the star system's network IDs keep clear of every crew's bridge");
        }

        // The host: a number one of the bridges holds is passed over.
        private static void Allocate_Postfix(MethodBase __originalMethod, ref int __result)
        {
            if (allocating || !PvpLobby.InPvpRoom || !BridgeIds.Contains(__result)) return;
            allocating = true;
            try
            {
                int skipped = __result;
                for (int tries = 0; tries < 16 && BridgeIds.Contains(__result); tries++)
                {
                    (manual?.GetValue(null) as IList)?.Remove(__result);
                    if (manual?.GetValue(null) is ICollection<int> set) set.Remove(__result);
                    __result = (int)__originalMethod.Invoke(null, null);
                }
                Plugin.Log.LogInfo($"PvP: network ID {skipped} passed over for a star system object (a bridge's); {__result} instead");
            }
            finally { allocating = false; }
        }

        // Each game: a new object with the number of one of this bridge scene's own moves that one aside.
        private static void Register_Prefix(object __instance, PhotonView __0)
        {
            if (!PvpLobby.InPvpRoom || __0 == null || __0.LJIBDPHGLMM == 0) return;
            if (!(viewList.GetValue(__instance) is Dictionary<int, PhotonView> list)) return;
            if (!list.TryGetValue(__0.LJIBDPHGLMM, out var old) || old == null || old == __0) return;
            if (!OnBridge(old) || OnBridge(__0)) return;
            int id = __0.LJIBDPHGLMM, free = 0;
            for (int n = 999; n > 0 && free == 0; n--)
                if (!list.ContainsKey(n) && !BridgeIds.Contains(n)) free = n;
            if (free == 0) return;
            list.Remove(id);
            old.LJIBDPHGLMM = free; // already registered once: the setter doesn't register again
            list[free] = old;
            Plugin.Log.LogWarning($"PvP: network ID {id} came for '{__0.name}' (the star system's) but this bridge's '{old.name}' had it: that one now {free}, not destroyed");
        }

        private static bool OnBridge(PhotonView view)
        {
            var scene = view.gameObject.scene.name ?? "";
            return scene.StartsWith("Bridge_scene", System.StringComparison.Ordinal) || scene.IndexOf("Console", System.StringComparison.Ordinal) >= 0;
        }
    }
}
