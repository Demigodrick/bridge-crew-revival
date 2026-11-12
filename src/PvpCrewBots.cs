using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// PvP: ship B's own crew bots. The game makes one crew (helm, tactical, engineering bots: NovaSpawnManager.AddNPC
    /// on the host as the bridge loads, PlayerManager.AddNPC → AddNPC_RPC to everyone), seated at ship A's stations;
    /// ship B's consoles borrowed them (PvpConsoles.ShareBots), one body for both ships: a player taking one ship's
    /// helm switched the helmsman off for both, and both ships' crews drove the same hands. Here the host makes three
    /// more alongside the game's (AddNPC_RPC with profile 101-103: the same crew looks, PlayerManager.GetCrewbotProfile
    /// takes 101 as 1; the profile number reaches every game, late joiners included, so every game knows them as
    /// ship B's), and every game seats them at ship B's stations (PvpConsoles.Seat_Prefix). While ship B's crew works,
    /// "the station for a role" (NovaStationRegistry.GetStation: where its bots reach) is ship B's. Each game shows the
    /// other ship's bots no more than its players, and its 2D crew menu only its own ship's.
    /// If they aren't there, ship B's consoles borrow ship A's as before.
    /// </summary>
    internal static class PvpCrewBots
    {
        private const int Marker = 100;
        private static bool adding;
        private static readonly PropertyInfo ProfileNdx = AccessTools.Property(typeof(PlayerInfo), "CDCANJPAGNN");
        private static readonly PropertyInfo PlayerRole = AccessTools.Property(typeof(NovaPlayerInfo), "FJMHMOOJNJG"); // Role
        private static MethodInfo ssrpc, getStation;
        private static object allTargets;
        private static readonly System.Collections.Generic.HashSet<string> told = new System.Collections.Generic.HashSet<string>();

        internal static void Patch(Harmony harmony)
        {
            var addNpc = AccessTools.Method(AccessTools.TypeByName("NovaSpawnManager"), "GHKIFGOIGDI");            // AddNPC(Station)
            var profile = AccessTools.Method(AccessTools.TypeByName("SVR.PlayerManager"), "BINCAOICBHB");          // GetCrewbotProfile(int)
            var registry = AccessTools.TypeByName("GGPMPCDFHAK");                                                 // NovaStationRegistry
            getStation = registry == null ? null : AccessTools.Method(registry, "GetStation", new[] { typeof(FLCHMJJDKDL) });
            var pawnSpawned = AccessTools.Method(typeof(NonVRCrewBotManager), "BGNJBHLOFJF");                      // OnPawnSpawned
            var targets = AccessTools.TypeByName("PDPMOIKGAAE");                                                  // PhotonTargets
            ssrpc = AccessTools.Method(AccessTools.TypeByName("MCGGBALEIDD"), "SSRPC",
                new[] { AccessTools.TypeByName("PhotonView"), typeof(object), typeof(string), targets, typeof(object[]) });
            allTargets = targets == null ? null : Enum.Parse(targets, "All");
            if (addNpc == null || profile == null || getStation == null || ssrpc == null || ProfileNdx == null || PlayerRole == null)
            {
                Plugin.Log.LogWarning("PvP: ship B's own crew bots' parts weren't all found; ship B borrows ship A's");
                return;
            }
            harmony.Patch(addNpc, postfix: new HarmonyMethod(typeof(PvpCrewBots), nameof(AddNpc_Postfix)));
            harmony.Patch(profile, prefix: new HarmonyMethod(typeof(PvpCrewBots), nameof(Profile_Prefix)));
            harmony.Patch(getStation, postfix: new HarmonyMethod(typeof(PvpCrewBots), nameof(GetStation_Postfix)));
            if (pawnSpawned != null) harmony.Patch(pawnSpawned, prefix: new HarmonyMethod(typeof(PvpCrewBots), nameof(PawnSpawned_Prefix)));
            Plugin.Log.LogInfo("PvP: ship B gets its own crew bots");
        }

        private static bool IsMaster => Patches.PhotonNetworkType?.GetMethod("get_isMasterClient", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) is true;

        /// <summary>One of ship B's own crew bots (profile 101-103).</summary>
        internal static bool IsShipBBot(PlayerInfo info)
        {
            try { return info != null && ProfileNdx?.GetValue(info, null) is int n && n > Marker; }
            catch { return false; }
        }

        /// <summary>Ship B has bots of its own in this game.</summary>
        internal static bool ShipBHasBots()
        {
            var npcs = Traverse.Create(NovaPlayerManager.IEFBKOMBFGO).Method("GetNPCs").GetValue() as IList;
            return npcs != null && npcs.Cast<object>().Any(n => IsShipBBot(n as PlayerInfo));
        }

        // The host made one of the game's crew bots: one for ship B's same station too.
        private static void AddNpc_Postfix(object __instance, FLCHMJJDKDL __0)
        {
            if (adding || !PvpPrototype.Active || !PvpLobby.InPvpRoom || !IsMaster) return;
            int n = __0 == FLCHMJJDKDL.Helm ? 1 : __0 == FLCHMJJDKDL.Tactical ? 2 : __0 == FLCHMJJDKDL.Engineering ? 3 : 0;
            if (n == 0) return;
            adding = true;
            try
            {
                var players = NovaPlayerManager.IEFBKOMBFGO;
                var view = players == null ? null : players.GetComponent("PhotonView");
                var allocate = Patches.PhotonNetworkType?.GetMethod("AllocateViewID", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (view == null || allocate == null) return;
                int id;
                do id = UnityEngine.Random.Range(1000, 10000);
                while (Traverse.Create(players).Method("GetInfoForPlayer", new object[] { id }).GetValue() != null);
                int viewId = (int)allocate.Invoke(null, null);
                ssrpc.Invoke(null, new object[] { view, players, "AddNPC_RPC", allTargets, new object[] { id, viewId, Marker + n } });
                var info = Traverse.Create(players).Method("GetInfoForPlayer", new object[] { id }).GetValue() as NovaPlayerInfo;
                if (info == null) { Plugin.Log.LogWarning($"PvP: ship B's {__0} crew bot wasn't made"); return; }
                PlayerRole.SetValue(info, __0, null);
                Traverse.Create(__instance).Method("NetworkViewIDAllocatedRPC", new object[] { id, (int)allocate.Invoke(null, null) }).GetValue();
                Plugin.Log.LogInfo($"PvP: ship B's own {__0} crew bot made ({info.FAJABLKBGHN}, player {id})");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"PvP: making ship B's {__0} crew bot: {(e.InnerException ?? e).Message}");
            }
            finally { adding = false; }
        }

        // Ship B's bots look like the game's: profile 101 is 1 (helm), 102 tactical, 103 engineering.
        private static void Profile_Prefix(ref int __0)
        {
            if (__0 > Marker) __0 -= Marker;
        }

        // While ship B's crew works (its view on: PvpScope, or its code: PvpRouting), "the station for a role" is ship
        // B's, so its bots reach for ship B's consoles (MoveHandToControl, SendAIInputIfNotHovering).
        private static void GetStation_Postfix(ref NovaStation __result)
        {
            if (__result == null || !PvpPrototype.Active) return;
            string ship = PvpScope.Applied ?? PvpRouting.Context;
            if (ship != "B" || PvpConsoles.IsCopy(__result)) return;
            var twin = PvpConsoles.TwinStation(__result);
            if (twin != null) __result = twin;
        }

        // The 2D crew menu marks each crew bot's station (by role): only your own ship's bots.
        private static bool PawnSpawned_Prefix(object __0)
        {
            if (!PvpPrototype.Active || __0 == null) return true;
            var info = Traverse.Create(__0).Field("PlayerInfo").GetValue() as PlayerInfo;
            if (info == null || !info.AOMGCCGDJJC) return true; // players: as ever
            string ship = IsShipBBot(info) ? "B" : "A";
            if (!ShipBHasBots() || ship == (PvpShips.Mine ?? "A")) return true;
            if (told.Add("menu" + ship)) Plugin.Log.LogInfo($"PvP: ship {ship}'s crew bots kept out of your crew menu");
            return false;
        }
    }
}
