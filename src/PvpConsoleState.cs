using System;
using HarmonyLib;
using Reader = SVR.Network.PLPKPDFOLDL;

namespace StbcRevival
{
    /// <summary>
    /// PvP, each crew on its own ship's bridge: a console's state is written by the sender's console type (its own
    /// bridge's: the Aegis, the original Enterprise or the Enterprise-D) and read by the receiver's, and the types
    /// write differently. Loading the other ship's console scenes instead doesn't work (a console needs its own
    /// bridge: they failed to start, ConsoleHelm.Start and the rest), so the readers here take the other types'
    /// state (ConsoleBase.DeserializeState, PMJDLIMBHEH, for helm, tactical and engineering):
    ///   - helm: the original Enterprise writes a block of its own (the flag is in the state); a reader of another
    ///     type gave up before the ship part (impulse, steering: ship B's helm didn't reach the host);
    ///   - tactical: the same, with no flag (the sender's type is known from who sent it);
    ///   - engineering: the Enterprise-D's has an ops panel, the Aegis's a repair grid; a reader without a panel the
    ///     sender has dropped it all, power included.
    /// Each reads the sender's layout, applies the ship part (power, impulse, weapons: what moves the ship), and
    /// returns false: the game skips the rest of that console's state by its length (ConsoleBase.ConsoleDeserialize).
    /// The sender's type: a player's console state is their game's (their ship's pick, NovaPlayerInfo's state); an
    /// unmanned station's comes from the host (UnmannedStationSync: the host's ship's pick).
    /// </summary>
    internal static class PvpConsoleState
    {
        private const int Enterprise = 1; // PvpShipChoice's original Enterprise pick
        [ThreadStatic] private static int senderPick;
        [ThreadStatic] private static bool senderKnown;

        private static readonly AccessTools.FieldRef<ConsoleBase, bool> TosMode = AccessTools.FieldRefAccess<ConsoleBase, bool>("_originalBridgeMode");
        private static readonly AccessTools.FieldRef<ConsoleHelm, bool> FullStop = AccessTools.FieldRefAccess<ConsoleHelm, bool>("NDLFLDDLJHF");
        private static readonly AccessTools.FieldRef<ConsoleHelm, ConsoleShipInterface> HelmShip = AccessTools.FieldRefAccess<ConsoleHelm, ConsoleShipInterface>("IIOCGDJHNCG");
        private static readonly AccessTools.FieldRef<ConsoleTactical, ConsoleShipInterface> TacticalShip = AccessTools.FieldRefAccess<ConsoleTactical, ConsoleShipInterface>("IJGFOIMOEOB");
        private static readonly AccessTools.FieldRef<ConsoleEngineering, ConsoleShipInterface> EngineeringShip = AccessTools.FieldRefAccess<ConsoleEngineering, ConsoleShipInterface>("BJEEEMECEEF");
        private static readonly AccessTools.FieldRef<ConsoleEngineering, Transporter> EngTransporter = AccessTools.FieldRefAccess<ConsoleEngineering, Transporter>("_transporter");
        private static readonly AccessTools.FieldRef<ConsoleEngineering, EngineerRepairGrid> EngRepair = AccessTools.FieldRefAccess<ConsoleEngineering, EngineerRepairGrid>("_repairGrid");
        private static readonly AccessTools.FieldRef<ConsoleEngineering, OpsPanel> EngOps = AccessTools.FieldRefAccess<ConsoleEngineering, OpsPanel>("_opsPanel");
        private static readonly System.Collections.Generic.HashSet<string> told = new System.Collections.Generic.HashSet<string>();

        internal static void Patch(Harmony harmony)
        {
            var helm = AccessTools.Method(typeof(ConsoleHelm), "PMJDLIMBHEH");
            var tactical = AccessTools.Method(typeof(ConsoleTactical), "PMJDLIMBHEH");
            var engineering = AccessTools.Method(typeof(ConsoleEngineering), "PMJDLIMBHEH");
            var playerState = AccessTools.Method(typeof(NovaPlayerInfo), "NetworkDeserialize");
            var unmanned = AccessTools.Method(typeof(UnmannedStationSync), "NetworkDeserialize");
            if (helm == null || tactical == null || engineering == null || playerState == null || unmanned == null)
            {
                Plugin.Log.LogWarning("PvP: the console state readers weren't all found; consoles of different ships' types drop each other's state");
                return;
            }
            harmony.Patch(playerState, prefix: new HarmonyMethod(typeof(PvpConsoleState), nameof(PlayerState_Prefix)), finalizer: new HarmonyMethod(typeof(PvpConsoleState), nameof(Sender_Finalizer)));
            harmony.Patch(unmanned, prefix: new HarmonyMethod(typeof(PvpConsoleState), nameof(Unmanned_Prefix)), finalizer: new HarmonyMethod(typeof(PvpConsoleState), nameof(Sender_Finalizer)));
            harmony.Patch(helm, prefix: new HarmonyMethod(typeof(PvpConsoleState), nameof(Helm_Prefix)));
            harmony.Patch(tactical, prefix: new HarmonyMethod(typeof(PvpConsoleState), nameof(Tactical_Prefix)));
            harmony.Patch(engineering, prefix: new HarmonyMethod(typeof(PvpConsoleState), nameof(Engineering_Prefix)));
            Plugin.Log.LogInfo("PvP: consoles read the other ships' console types' state");
        }

        private static bool On => PvpLobby.InPvpRoom && PvpPrototype.Active;

        // ---- Who wrote it ---------------------------------------------------------------------------

        private struct Saved { public int Pick; public bool Known; }

        private static void PlayerState_Prefix(NovaPlayerInfo __instance, out Saved __state)
        {
            __state = new Saved { Pick = senderPick, Known = senderKnown };
            if (!On || __instance == null) return;
            string ship = PvpShips.Of(PvpShips.Player(__instance.NJNHJKEPFAK));
            senderKnown = ship != null;
            if (senderKnown) senderPick = PvpShipChoice.Of(ship);
        }

        private static void Unmanned_Prefix(out Saved __state)
        {
            __state = new Saved { Pick = senderPick, Known = senderKnown };
            if (!On) return;
            var master = Patches.PhotonNetworkType?.GetMethod("get_masterClient", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.Invoke(null, null);
            string ship = master == null ? null : PvpShips.Of(master);
            senderKnown = ship != null;
            if (senderKnown) senderPick = PvpShipChoice.Of(ship);
        }

        private static Exception Sender_Finalizer(Saved __state, Exception __exception)
        {
            senderPick = __state.Pick;
            senderKnown = __state.Known;
            return __exception;
        }

        private static void Told(string what, ConsoleBase console)
        {
            if (told.Add(what)) Plugin.Log.LogInfo($"PvP: {console.GetType().Name} read another console type's state ({what})");
        }

        // ---- Helm -------------------------------------------------------------------------------------

        private static bool Helm_Prefix(ConsoleHelm __instance, Reader __0, float __1, ref bool __result)
        {
            if (!On || __instance == null) return true;
            uint start = __0.LLPAKGEPJJP; // Pos
            bool stop = false, has = false, tos = false;
            byte b = 0;
            __0.ReadBool(ref stop);
            __0.ReadBool(ref has);
            if (has) { __0.ReadByte(ref b); __0.ReadByte(ref b); __0.ReadBool(ref tos); }
            bool mismatch = has && !__0.OKCBBENHIBC && tos != TosMode(__instance);
            __0.Seek(start);
            if (!mismatch) return true;
            // The sender's layout: full stop, has, view mode, panel state, its type, [its original Enterprise block], 4 floats, the ship part.
            __0.ReadBool(ref FullStop(__instance));
            __0.ReadBool(ref has);
            __0.ReadByte(ref b);
            __0.ReadByte(ref b);
            __0.ReadBool(ref tos);
            int i = 0; bool flag = false; float f = 0f;
            if (tos) { __0.ReadInt(ref i); __0.ReadInt(ref i); __0.ReadBool(ref flag); __0.ReadInt(ref i); __0.ReadBool(ref flag); }
            for (int n = 0; n < 4; n++) __0.ReadFloat(ref f);
            var ship = HelmShip(__instance);
            if (!__0.OKCBBENHIBC && ship != null) ship.ConsoleDeserialize(__0, __1);
            Told(tos ? "the original Enterprise's helm" : "a helm of another type", __instance);
            __result = false;
            return false;
        }

        // ---- Tactical ---------------------------------------------------------------------------------

        private static bool Tactical_Prefix(ConsoleTactical __instance, Reader __0, float __1, ref bool __result)
        {
            if (!On || __instance == null || !senderKnown) return true;
            bool senderTos = senderPick == Enterprise;
            if (senderTos == TosMode(__instance)) return true;
            byte flags = 0, b = 0;
            __0.ReadByte(ref flags);
            if (__0.OKCBBENHIBC) { __result = false; return false; }
            if ((flags & 1) == 0) { __result = true; return false; }
            for (int n = 0; n < 8; n++) __0.ReadByte(ref b);
            if (senderTos) { float f = 0f; bool t = false; __0.ReadFloat(ref f); __0.ReadBool(ref t); }
            var ship = TacticalShip(__instance);
            if (!__0.OKCBBENHIBC && ship != null) ship.ConsoleDeserialize(__0, __1);
            Told(senderTos ? "the original Enterprise's tactical" : "a tactical of another type", __instance);
            __result = false;
            return false;
        }

        // ---- Engineering ------------------------------------------------------------------------------

        private static bool Engineering_Prefix(ConsoleEngineering __instance, Reader __0, float __1, ref bool __result)
        {
            if (!On || __instance == null) return true;
            uint start = __0.LLPAKGEPJJP; // Pos
            byte flags = 0;
            __0.ReadByte(ref flags);
            __0.Seek(start);
            bool transporter = (flags & 2) != 0, repair = (flags & 4) != 0, ops = (flags & 0x20) != 0;
            bool senderTos = senderKnown ? senderPick == Enterprise : TosMode(__instance);
            bool missing = (transporter && EngTransporter(__instance) == null) || (repair && EngRepair(__instance) == null) || (ops && EngOps(__instance) == null);
            if (!missing && senderTos == TosMode(__instance)) return true;
            // The sender's layout: flags, warp state, repair targets, [ops mode], [its original Enterprise flag], then the parts.
            byte b = 0; bool modified = false, t = false; int count = 0, item = 0;
            __0.ReadByte(ref flags);
            __0.ReadByte(ref b);
            __0.ReadBool(ref modified);
            if (modified) { __0.ReadInt(ref count); for (int n = 0; n < count && !__0.OKCBBENHIBC; n++) __0.ReadInt(ref item); }
            if (ops) __0.ReadByte(ref b);
            if (senderTos) __0.ReadBool(ref t);
            __result = false;
            if (__0.OKCBBENHIBC) return false;
            var ship = EngineeringShip(__instance);
            if ((flags & 1) != 0)
            {
                if (ship == null || !ship.ConsoleDeserialize(__0, __1)) return false; // power, warp charge, repair crews
            }
            Told(missing ? "panels this console hasn't" : "the original Enterprise's engineering", __instance);
            // The panels, as far as this console has them (the rest is skipped by its length).
            if (transporter) { var p = EngTransporter(__instance); if (p == null || !p.TransporterDeserialize(__0, __1)) return false; }
            if (repair) { var p = EngRepair(__instance); if (p == null || !p.RepairGridDeserialize(__0, __1)) return false; }
            if (ops) { var p = EngOps(__instance); if (p == null || !p.OpsPanelDeserialize(__0, __1)) return false; }
            return false;
        }
    }
}
