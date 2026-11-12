using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// PvP, each crew on its own ship's bridge: the bridge scenes' own Photon views have different IDs (the
    /// Bridge object's: Aegis 130, original Enterprise 127, Enterprise-D 884; the holodeck 130), so an RPC a
    /// crew member's bridge sends to the host goes to a view the host's bridge hasn't, and Photon drops it. The
    /// ones that matter are requests to the host on the Bridge object's view (DistributedSystems: the
    /// transporter and hacking, their tasks; DamageManager: the bridge damage resync): the host, in
    /// NetworkingPeer.ExecuteRpc, takes those addressed to another bridge's Bridge view as addressed to its own.
    /// Everything else stays where it was sent, on purpose: the other bridge's damage effects
    /// (Multicast_PlayTempDamageEffects, indices into that bridge's own effects) and messages aren't for this
    /// bridge, and the viewscreen request would switch the host's own ship's viewscreen.
    /// </summary>
    internal static class PvpBridgeViews
    {
        private static readonly int[] BridgeViews = { 130, 127, 884 };
        private static readonly HashSet<string> ToHost = new HashSet<string>
        {
            "RequestToUseTransporterRPC", "RequestToUseHackingRPC", "BeginDSTaskRPC", "DropDSTaskRPC", "Server_RequestDamageIndices",
            "RequestViewscreenModeChangeRPC",
        };

        // Requests the host carries out on "the" ship (Bridge.Captain and the like): run in the sending player's
        // ship's view (PvpScope), so a ship B helm's viewscreen request switches ship B's captain console, not the
        // host's (ConsoleHelm.ChangeViewscreenMode → Bridge.RequestViewscreenModeChangeRPC → Bridge.Captain.SwitchViewscreen).
        private static readonly HashSet<string> InSendersShip = new HashSet<string> { "RequestViewscreenModeChangeRPC" };
        private static bool scoped;
        private static string scopeBefore;

        // Bridge goings-on that belong to the sender's ship only, dropped when they come from the other ship's game:
        // a ship's hull hit shook every bridge (DamageManager.Multicast_PlayTempDamageEffects, sent by the game whose
        // own ship was hit, indices into its own bridge's effects); the host's red shirts are its own bridge's
        // (RedShirtManager, driven by the host alone: station indices into its bridge's red shirts).
        private static readonly HashSet<string> SendersShipOnly = new HashSet<string>
        {
            "Multicast_PlayTempDamageEffects",
            "Multicast_TriggerAmbientSingle", "Multicast_PlayTalkAnimation", "Multicast_PlayVOAnimation",
            "Multicast_SpiceIdle", "Multicast_PlayRespondAnimation", "Multicast_TriggerImpactReactions",
        };

        // Sounds and voice lines a crew member's game sends to everyone (its own console's and bridge's: a crew
        // member runs only its own ship's), kept off the other ship's bridge. The host's come here too, but it sends
        // for either ship (PvpSounds and PvpRouting route its own by ship), so only a non-host sender's are dropped.
        private static readonly HashSet<string> CrewSounds = new HashSet<string>
        {
            "PostEvent_Networked_RPC", "PostEvent_RPC", "SetRTPC_RPC", "SetSwitch_RPC", "Play_Networked_RPC",
            "Stop_Networked_RPC_Int", "Stop_Networked_RPC_String", "PlayVO_Networked_RPC",
        };

        private static IList rpcList;
        private static readonly HashSet<string> told = new HashSet<string>();

        internal static void Patch(Harmony harmony)
        {
            var peer = AccessTools.TypeByName("PKJCFCINBFA") /* NetworkingPeer */;
            var execute = peer == null ? null : AccessTools.Method(peer, "NGNCBBGNGKO"); // ExecuteRpc(Hashtable, PhotonPlayer)
            if (execute == null)
            {
                Plugin.Log.LogWarning("PvP: NetworkingPeer.ExecuteRpc not found; ship B's transporter and hacking requests from another bridge won't reach the host");
                return;
            }
            harmony.Patch(execute, prefix: new HarmonyMethod(typeof(PvpBridgeViews), nameof(Execute_Prefix)),
                finalizer: new HarmonyMethod(typeof(PvpBridgeViews), nameof(Execute_Finalizer)));
            PatchBridgeState(harmony);
            Plugin.Log.LogInfo("PvP: the host takes requests from the other bridges' Bridge views");
        }

        /// <summary>The player whose RPC is running (NetworkingPeer.ExecuteRpc), or null outside one.</summary>
        [ThreadStatic] internal static object RpcSender;

        private static bool Execute_Prefix(object __0, object __1)
        {
            RpcSender = __1;
            try
            {
                if (!PvpLobby.InPvpRoom || !PvpPrototype.Active || PvpPrototype.Bound == null) return true;
                if (!(__0 is IDictionary rpc) || !rpc.Contains((byte)0) || !(rpc[(byte)0] is int view)) return true;
                string name = RpcName(rpc);
                if (name == null) return true;
                if (CrewSounds.Contains(name) && __1 != null && PvpShips.Of(__1) != PvpShips.Mine && !IsMasterPlayer(__1))
                {
                    if (told.Add("sound" + name)) Plugin.Log.LogInfo($"PvP: {name} from ship {PvpShips.Of(__1)}'s crew kept off your bridge");
                    return false;
                }
                string from = !SendersShipOnly.Contains(name) ? null : __1 == null ? PvpShips.Mine : PvpShips.Of(__1);
                if (from != null && from != PvpShips.Mine)
                {
                    if (told.Add("drop" + name)) Plugin.Log.LogInfo($"PvP: {name} from ship {from}'s game kept off your bridge (its own bridge's)");
                    return false;
                }
                if (Array.IndexOf(BridgeViews, view) < 0 || !ToHost.Contains(name)) return true;
                if (!(Patches.PhotonNetworkType?.GetMethod("get_isMasterClient", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) is true)) return true;
                int own = OwnBridgeView();
                if (own > 0 && own != view)
                {
                    rpc[(byte)0] = own;
                    if (told.Add(name + view)) Plugin.Log.LogInfo($"PvP: {name} from another bridge (view {view}) taken by this bridge (view {own})");
                }
                if (InSendersShip.Contains(name) && __1 != null)
                {
                    string ship = PvpShips.Of(__1);
                    if (ship != null && ship != PvpShips.Mine)
                    {
                        scopeBefore = PvpScope.Applied;
                        PvpScope.Switch(ship);
                        scoped = true;
                        if (told.Add("scope" + name)) Plugin.Log.LogInfo($"PvP: {name} from ship {ship}'s crew carried out on ship {ship}");
                    }
                }
            }
            catch (Exception e)
            {
                if (told.Add("error")) Plugin.Log.LogWarning("PvP: reading an RPC for the other bridge: " + e.Message);
            }
            return true;
        }

        // ---- The bridge's own network objects (baked ids: DamageManager on 6, RedShirtManager on 2) --------
        // The ids match on every bridge, so the host's state reaches the other ship's crew: its persistent damage
        // effects' order (indices into the host's bridge's effects: an Enterprise-D or original Enterprise bridge has
        // fewer, so out of range every frame) and the red shirt it lost (killed on their bridge too), and its red
        // shirts' seats (indices into its bridge's red shirts). On a game crewing the other ship than the host's,
        // the damage state is read and left (DistributedSystems' transporter and hacking state follows it in the
        // same message, so it must be read through), and the red shirts' isn't taken (their message is theirs alone).

        internal static void PatchBridgeState(Harmony harmony)
        {
            var damage = AccessTools.Method(AccessTools.TypeByName("DamageManager"), "NetworkDeserialize");
            var redShirts = AccessTools.Method(AccessTools.TypeByName("RedShirtManager"), "NetworkDeserialize");
            if (damage != null) harmony.Patch(damage, prefix: new HarmonyMethod(typeof(PvpBridgeViews), nameof(Damage_Prefix)));
            if (redShirts != null) harmony.Patch(redShirts, prefix: new HarmonyMethod(typeof(PvpBridgeViews), nameof(RedShirts_Prefix)));
            if (damage == null || redShirts == null) Plugin.Log.LogWarning("PvP: the bridge damage or red shirt sync wasn't found; the other ship's crew takes the host's bridge state");
        }

        internal static bool OtherShipThanHost()
        {
            if (!PvpLobby.InPvpRoom || !PvpPrototype.Active) return false;
            if (Patches.PhotonNetworkType?.GetMethod("get_isMasterClient", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) is true) return false;
            var master = Patches.PhotonNetworkType?.GetMethod("get_masterClient", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
            string host = master == null ? null : PvpShips.Of(master);
            return host != null && host != PvpShips.Mine;
        }

        private static bool Damage_Prefix(SVR.Network.PLPKPDFOLDL __0, ref bool __result)
        {
            if (!OtherShipThanHost()) return true;
            int count = 0, index = 0, arg = 0;
            int[] order = null;
            __0.ReadInt(ref count);
            __0.ReadIntArray(ref order, (uint)Math.Max(0, count));
            __0.ReadInt(ref index);
            __0.ReadInt(ref arg);
            __result = !__0.OKCBBENHIBC; // HasError
            if (told.Add("damage")) Plugin.Log.LogInfo("PvP: the host's bridge damage state left alone here (its bridge's, not your ship's)");
            return false;
        }

        private static bool RedShirts_Prefix(ref bool __result)
        {
            if (!OtherShipThanHost()) return true;
            __result = false; // the rest of this message is the red shirts' alone
            if (told.Add("redshirts")) Plugin.Log.LogInfo("PvP: the host's red shirts' seats left alone here (its bridge's)");
            return false;
        }

        private static bool IsMasterPlayer(object player)
        {
            var master = Patches.PhotonNetworkType?.GetMethod("get_masterClient", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
            return master != null && PvpShips.Actor(master) == PvpShips.Actor(player);
        }

        private static Exception Execute_Finalizer(Exception __exception)
        {
            RpcSender = null;
            if (scoped)
            {
                scoped = false;
                try { PvpScope.Switch(scopeBefore); } catch { }
            }
            return __exception;
        }

        private static int OwnBridgeView()
        {
            var bridge = Bridge.Instance;
            var pv = bridge == null ? null : bridge.GetComponent("PhotonView");
            if (pv == null) return -1;
            var id = Traverse.Create(pv).Property("viewID").GetValue() ?? Traverse.Create(pv).Field("viewIdField").GetValue();
            return id is int i ? i : -1;
        }

        // The RPC's method: by its shortcut (key 5, an index into the server settings' RpcList), else by name (key 3).
        private static string RpcName(IDictionary rpc)
        {
            if (rpc.Contains((byte)5) && rpc[(byte)5] is byte index)
            {
                if (rpcList == null)
                {
                    var settings = Patches.PhotonNetworkType?.GetField("PhotonServerSettings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                    rpcList = settings == null ? null : Traverse.Create(settings).Field("RpcList").GetValue() as IList;
                }
                return rpcList != null && index < rpcList.Count ? rpcList[index] as string : null;
            }
            return rpc.Contains((byte)3) ? rpc[(byte)3] as string : null;
        }
    }
}
