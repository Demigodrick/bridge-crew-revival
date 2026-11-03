using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// PvP: each ship is any of the three (the Aegis, the original Enterprise, the Enterprise-D), picked by its own
    /// captain in the briefing room, and each crew sits on its own ship's bridge.
    ///  - The picks are room properties PSA and PSB (0 Aegis, 1 original Enterprise, 2 Enterprise-D). In a PvP room
    ///    a SHIP toggle sets the clicking captain's (slot 0: ship A, slot 4: ship B), not the game's one choice
    ///    for the room (Loader's retro / TNG bridge flags, sent to everyone).
    ///  - Those flags are read everywhere (the consoles, the bridge scene loaded, the crew, the ship spawned), so
    ///    each game keeps them at its own ship's pick: its consoles show its own ship, it loads its own bridge, and
    ///    its bridge works as that ship's does.
    ///  - The host spawns ship A (MissionManager.NJHADEBHMNN) with ship A's pick whatever its own ship, and ship B
    ///    (the PvP mission) with ship B's.
    /// </summary>
    internal static class PvpShipChoice
    {
        internal const int Aegis = 0, Enterprise = 1, EnterpriseD = 2;
        private const string KeyA = "PSA", KeyB = "PSB";
        private const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;

        private static readonly FieldInfo RetroFlag = AccessTools.Field(typeof(Loader), "KHGLJDMCEFE"); // using the original Enterprise
        private static readonly FieldInfo TngFlag = AccessTools.Field(typeof(Loader), "LJILDCNBPAN");   // using the Enterprise-D
        private static int logged = -1;

        internal static void Patch(Harmony harmony)
        {
            if (RetroFlag == null || TngFlag == null)
            {
                Plugin.Log.LogWarning("PvP: the loader's ship flags weren't found; both ships are the room's ship");
                return;
            }
            // The briefing console's SHIP toggles: Aegis, original Enterprise, Enterprise-D.
            string[] handlers = { "NGNOCCOOOBM", "IFNGPJDAMAF", "PFMBDEEEHHL" };
            string[] prefixes = { nameof(Aegis_Prefix), nameof(Enterprise_Prefix), nameof(EnterpriseD_Prefix) };
            for (int i = 0; i < handlers.Length; i++)
            {
                var target = AccessTools.Method(typeof(ConsoleBriefingRoom), handlers[i], new[] { typeof(bool) });
                if (target == null) Plugin.Log.LogWarning($"PvP: ConsoleBriefingRoom.{handlers[i]} (a ship toggle) not found; captains can't pick their ship");
                else harmony.Patch(target, prefix: new HarmonyMethod(typeof(PvpShipChoice), prefixes[i]));
            }
            foreach (var name in new[] { "NJHADEBHMNN", "DJPEDKDPHDD" }) // the player ship's spawn (and its decoy twin)
            {
                var spawn = AccessTools.Method(typeof(Mission.MissionManager), name);
                if (spawn != null)
                    harmony.Patch(spawn, prefix: new HarmonyMethod(typeof(PvpShipChoice), nameof(Spawn_Prefix)),
                        finalizer: new HarmonyMethod(typeof(PvpShipChoice), nameof(Spawn_Finalizer)));
            }
        }

        // ---- The picks ------------------------------------------------------------------------------

        /// <summary>A ship's pick (A or B): 0 Aegis, 1 original Enterprise, 2 Enterprise-D.</summary>
        internal static int Of(string ship)
        {
            var room = Patches.PhotonNetworkType?.GetMethod("get_room", Static)?.Invoke(null, null);
            var props = room?.GetType().GetMethod("get_customProperties")?.Invoke(room, null) as IDictionary;
            var key = ship == "B" ? KeyB : KeyA;
            return props != null && props.Contains(key) && props[key] is int pick && pick >= Aegis && pick <= EnterpriseD ? pick : Aegis;
        }

        internal static string Name(int pick) => pick == Enterprise ? "the original Enterprise" : pick == EnterpriseD ? "the Enterprise-D" : "the Aegis";

        private static bool Aegis_Prefix(bool __0) => Picked(Aegis, __0);
        private static bool Enterprise_Prefix(bool __0) => Picked(Enterprise, __0);
        private static bool EnterpriseD_Prefix(bool __0) => Picked(EnterpriseD, __0);

        // In a PvP room a SHIP toggle never sets the room's one ship: a captain's click is their ship's pick, and
        // the toggles themselves show this game's own ship's pick (the console's refresh, from the flags).
        private static bool Picked(int pick, bool on)
        {
            if (!PvpLobby.InPvpRoom) return true;
            if (!on) return false;
            int slot = PvpRoles.SlotOf(PvpShips.Actor(PvpShips.Me));
            if (slot != 0 && slot != 4) return false;
            if (pick == EnterpriseD && !Loader.FEJCJINLJKP) return false; // IsTNGContentAvailableAndEnabled
            var ship = slot == 0 ? "A" : "B";
            if (Of(ship) == pick) return false;
            var room = Patches.PhotonNetworkType?.GetMethod("get_room", Static)?.Invoke(null, null);
            if (PhotonProps.Set(room, ship == "A" ? KeyA : KeyB, pick))
                Plugin.Log.LogInfo($"PvP: ship {ship}'s captain picked {Name(pick)}");
            return false;
        }

        /// <summary>Can this player pick a ship (their ship's captain, in a PvP room)?</summary>
        internal static bool CanPick()
        {
            int slot = PvpRoles.SlotOf(PvpShips.Actor(PvpShips.Me));
            return slot == 0 || slot == 4;
        }

        // ---- This game's flags: its own ship's pick -----------------------------------------------------

        /// <summary>Each frame, in a PvP room (briefing room and bridge): the loader's flags at this player's ship's pick.</summary>
        internal static void Update()
        {
            if (RetroFlag == null || Loader.EJLNDBGLIFL == null || spawning) return;
            if (!PvpLobby.InPvpRoom) { logged = -1; return; }
            int pick = Of(PvpShips.Mine ?? "A");
            if (Set(pick) && logged != pick)
            {
                logged = pick;
                Plugin.Log.LogInfo($"PvP: your ship ({PvpShips.Mine ?? "A"}) is {Name(pick)}: its bridge and consoles");
            }
        }

        // The flags at a pick; true if they changed.
        private static bool Set(int pick)
        {
            var loader = Loader.EJLNDBGLIFL;
            bool retro = pick == Enterprise, tng = pick == EnterpriseD;
            if (Equals(RetroFlag.GetValue(loader), retro) && Equals(TngFlag.GetValue(loader), tng)) return false;
            RetroFlag.SetValue(loader, retro);
            TngFlag.SetValue(loader, tng);
            return true;
        }

        // ---- The host spawning ship A -------------------------------------------------------------------

        private static bool spawning;

        private static void Spawn_Prefix(out int __state)
        {
            __state = -1;
            if (RetroFlag == null || !PvpLobby.InPvpRoom || Loader.EJLNDBGLIFL == null) return;
            __state = Of(PvpShips.Mine ?? "A");
            spawning = true;
            Set(Of("A"));
            Plugin.Log.LogInfo($"PvP: ship A spawned as {Name(Of("A"))}");
        }

        private static Exception Spawn_Finalizer(int __state, Exception __exception)
        {
            if (__state >= 0)
            {
                Set(__state);
                spawning = false;
            }
            return __exception;
        }

        /// <summary>The prefab for ship B (the PvP mission spawns it), from ship B's pick.</summary>
        internal static string ShipBPrefabField()
        {
            int pick = Of("B");
            return pick == Enterprise ? "_retroPlayerCraftPrefabPath" : pick == EnterpriseD ? "_TNGPlayerCraftPrefabPath" : "_playerCraftPrefabPath";
        }
    }
}
