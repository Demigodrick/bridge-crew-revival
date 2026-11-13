using System;
using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// PvP: crew orders. The game's crew menus and crew don't know there are two ships:
    ///   - In 2D, a crew bot's menu opens with the mouse only at a station no other player holds
    ///     (NonVRCrewBotManager.UpdateAIStations: on the Enterprise-D and the original Enterprise, a captain's only
    ///     the centre console's, station 0). The other ship's players sit at the same roles on their own ship, so
    ///     they took your stations: the other captain at "Captain" switched yours off, and no order could be given.
    ///     A station held only by a player on the other ship counts as free again.
    ///   - A ship's AI crew sets itself up at its fourth seated player (USSAegisCrew.OnPlayerSeatedEvent, on the host):
    ///     with two ships' players seated, ship A's could set up before its own last crew bot sat, that station's
    ///     crew bot then doing nothing. Each crew counts only its own ship's seats; ship B's is set up by the PvP
    ///     test itself (PvpPrototype.SetUpCrew).
    /// </summary>
    internal static class PvpCrewMenu
    {
        private static readonly AccessTools.FieldRef<NonVRCrewBotManager, bool[]> AIStation = AccessTools.FieldRefAccess<NonVRCrewBotManager, bool[]>("GLBPIGKNLKM");
        private static readonly System.Collections.Generic.HashSet<string> told = new System.Collections.Generic.HashSet<string>();

        internal static void Patch(Harmony harmony)
        {
            var stations = AccessTools.Method(typeof(NonVRCrewBotManager), "LALBCOEECDL"); // UpdateAIStations
            var seated = AccessTools.Method(typeof(AI.USSAegisCrew), "BGNJBHLOFJF");     // OnPlayerSeatedEvent
            if (stations != null) harmony.Patch(stations, postfix: new HarmonyMethod(typeof(PvpCrewMenu), nameof(Stations_Postfix)));
            if (seated != null) harmony.Patch(seated, prefix: new HarmonyMethod(typeof(PvpCrewMenu), nameof(Seated_Prefix)));
            if (stations == null || seated == null) Plugin.Log.LogWarning("PvP: the crew menu's stations or the crew's seat count weren't found; crew orders may not work");
            else Plugin.Log.LogInfo("PvP: crew menus and crews count only their own ship's players");
        }

        private static bool On => PvpLobby.InPvpRoom && PvpPrototype.Active;

        private static void Stations_Postfix(NonVRCrewBotManager __instance)
        {
            if (!On || __instance == null) return;
            var ai = AIStation(__instance);
            var me = NovaPlayerManager.IEFBKOMBFGO?.AJPNCBOKCLC as NovaPlayerInfo;
            if (ai == null || me == null) return;
            var myStation = me.Station as NovaStation;
            int myRole = myStation == null ? -1 : (int)myStation.OGBAPMIPPID;
            bool captainOnly = (Loader.JCEFNOJHCKO || Loader.IOMGCNMDDCK) && myRole == 0; // IsTOS, IsTNG
            var mine = new bool[ai.Length];   // roles a player on your ship holds (besides you)
            var theirs = new bool[ai.Length]; // roles a player on the other ship holds
            string myShip = PvpShips.Mine ?? "A";
            foreach (var player in Tracked.All<NovaPlayerInfo>())
            {
                if (player == null || player.AOLEJMABHPE || player.AOMGCCGDJJC || !(player.Station is NovaStation station)) continue; // you, a bot
                int role = (int)station.OGBAPMIPPID;
                if (role < 0 || role >= ai.Length) continue;
                if (PvpShips.Of(PvpShips.Player(player.NJNHJKEPFAK)) == myShip) mine[role] = true;
                else theirs[role] = true;
            }
            for (int r = 0; r < ai.Length; r++)
            {
                if (!theirs[r] || mine[r] || ai[r]) continue;
                if (myRole != 0 && (r == 0 || r == myRole)) continue; // the game's own rule for a non-captain
                bool free = captainOnly ? r == 0 : true;
                if (!free) continue;
                ai[r] = true;
                if (told.Add("station" + r)) Plugin.Log.LogInfo($"PvP: crew menu station {r} free again (held only by the other ship's player)");
            }
        }

        private static bool Seated_Prefix(AI.USSAegisCrew __instance, object __0)
        {
            if (!On || __instance == null || __0 == null) return true;
            var ship = __instance.GetComponent<Sector.Spacecraft.PlayerSpacecraft>();
            if (ship == null) return true;
            string crewShip = PvpPrototype.Letter(ship);
            if (crewShip == "B") return false; // set up by the PvP test
            var info = Traverse.Create(__0).Field("PlayerInfo").GetValue() as NovaPlayerInfo;
            var station = info == null ? null : info.Station as NovaStation;
            if (station == null) return true;
            string seatShip = PvpConsoles.IsCopy(station) ? "B" : "A";
            if (seatShip == crewShip) return true;
            if (told.Add("seat")) Plugin.Log.LogInfo($"PvP: ship {crewShip}'s crew doesn't count ship {seatShip}'s seated players");
            return false;
        }
    }
}
