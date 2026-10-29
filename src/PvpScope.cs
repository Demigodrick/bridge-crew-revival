using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AI;
using HarmonyLib;
using Sector.Spacecraft;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// PvP test: the other ship's AI crew. The game's crew (USSAegisCrew, on each player ship) and everything it
    /// calls read "the" ship from a handful of one-per-game handles: the bridge's craft, its four console
    /// statics, and the crew order board (CommandManager.Instance, whose CurrentOrder the crew reads ~140
    /// times). While the other ship's crew runs (its BatchUpdate and event handlers, PvpRouting), those handles
    /// are that ship's (only the crew's: the other captain console drives the bridge's one viewscreen):
    ///   - the craft: the other player ship;
    ///   - the consoles: the twins (PvpConsoles) of yours, so the other set;
    ///   - the order board: the ship's own CommandManager if its prefab has one, else a copy of yours made
    ///     once (same command list, its own orders);
    ///   - Bridge.Crew: the other ship's crew.
    /// Leaving puts yours back. The bridge's own parts (the captain's crew menu, the crew bots' avatars) stay
    /// yours. The other set's consoles get the crew bots of their twins (ConsoleBase.AI), so the crew counts
    /// them as AI stations. Only while the PvP test runs.
    /// </summary>
    internal static class PvpScope
    {
        private sealed class View
        {
            public CMFLDCJNMJJ Craft;
            public ConsoleCaptain Captain;
            public ConsoleEngineering Engineering;
            public ConsoleHelm Helm;
            public ConsoleTactical Tactical;
            public USSAegisCrew Crew;
            public CommandManager Orders;
        }

        private static readonly FieldInfo CraftField = AccessTools.Field(typeof(Bridge), "<BAPNEMLKIOK>k__BackingField");
        private static readonly FieldInfo CaptainField = AccessTools.Field(typeof(Bridge), "<BCEEBGAPPFD>k__BackingField");
        private static readonly FieldInfo EngineeringField = AccessTools.Field(typeof(Bridge), "<FIGNOOIOMKP>k__BackingField");
        private static readonly FieldInfo HelmField = AccessTools.Field(typeof(Bridge), "<CADPODOELDO>k__BackingField");
        private static readonly FieldInfo TacticalField = AccessTools.Field(typeof(Bridge), "<FEENAOHJOEO>k__BackingField");
        private static readonly FieldInfo CrewField = AccessTools.Field(typeof(Bridge), "<FIKINJFBFEL>k__BackingField");
        private static readonly FieldInfo OrdersField = AccessTools.Field(typeof(CommandManager), "<MFBACMFBGOL>k__BackingField");

        private static readonly Dictionary<string, CommandManager> copiedOrders = new Dictionary<string, CommandManager>();
        private static View yours;      // the handles as they were when the other ship's view went on
        private static string applied;  // the ship whose view is on, or null for yours

        /// <summary>The ship whose view is on, or null for yours.</summary>
        internal static string Applied => applied;

        internal static bool Ready => CraftField != null && CaptainField != null && EngineeringField != null && HelmField != null
                                      && TacticalField != null && CrewField != null && OrdersField != null;

        /// <summary>Code for this ship ("A", "B", or null for yours) is about to run: its view goes on.</summary>
        internal static void Switch(string ship)
        {
            if (ship == PvpShips.Mine) ship = null;
            if (ship == applied || !Ready || Bridge.Instance == null) return;
            // Only once your own ship is bound (until then the handles are the first ship's, whoever you are).
            if (ship != null && (PvpPrototype.Bound == null || PvpPrototype.Letter(PvpPrototype.Bound) != PvpShips.Mine)) return;
            if (applied == null) yours = Read();
            View view = ship == null ? yours : Build(ship);
            if (view == null) return; // the other ship isn't there (yet): stay as you are
            Write(view);
            applied = ship;
        }

        /// <summary>Runs something with that ship's view on, then puts back what was on.</summary>
        internal static void Run(string ship, Action action)
        {
            var before = applied;
            Switch(ship);
            try { action(); }
            finally { Switch(before); }
        }

        /// <summary>Between missions: nothing of the last one kept.</summary>
        internal static void Reset()
        {
            if (applied != null && yours != null) Write(yours);
            applied = null;
            yours = null;
            foreach (var orders in copiedOrders.Values)
                if (orders != null) UnityEngine.Object.Destroy(orders.gameObject);
            copiedOrders.Clear();
        }

        private static View Read() => new View
        {
            Craft = CraftField.GetValue(Bridge.Instance) as CMFLDCJNMJJ,
            Captain = CaptainField.GetValue(null) as ConsoleCaptain,
            Engineering = EngineeringField.GetValue(null) as ConsoleEngineering,
            Helm = HelmField.GetValue(null) as ConsoleHelm,
            Tactical = TacticalField.GetValue(null) as ConsoleTactical,
            Crew = CrewField.GetValue(null) as USSAegisCrew,
            Orders = OrdersField.GetValue(null) as CommandManager,
        };

        private static void Write(View view)
        {
            CraftField.SetValue(Bridge.Instance, view.Craft);
            CaptainField.SetValue(null, view.Captain);
            EngineeringField.SetValue(null, view.Engineering);
            HelmField.SetValue(null, view.Helm);
            TacticalField.SetValue(null, view.Tactical);
            CrewField.SetValue(null, view.Crew);
            OrdersField.SetValue(null, view.Orders);
        }

        private static View Build(string ship)
        {
            var craft = PvpPrototype.ShipOf(ship);
            if (craft == null || yours == null) return null;
            return new View
            {
                Craft = craft,
                Captain = PvpConsoles.Twin(yours.Captain),
                Engineering = PvpConsoles.Twin(yours.Engineering),
                Helm = PvpConsoles.Twin(yours.Helm),
                Tactical = PvpConsoles.Twin(yours.Tactical),
                Crew = craft.GetComponent<USSAegisCrew>(),
                Orders = OrdersFor(ship, craft, yours.Orders),
            };
        }

        // The ship's own order board, or a copy of yours: inactive while its fields are copied, so its Awake
        // (which makes it the game's one board) runs on the copied command list; then yours is put back.
        private static CommandManager OrdersFor(string ship, PlayerSpacecraft craft, CommandManager mine)
        {
            var own = craft.GetComponentInChildren<CommandManager>(true);
            if (own != null && own != mine) return own;
            if (copiedOrders.TryGetValue(ship, out var copy) && copy != null) return copy;
            if (mine == null) return null;
            var holder = new GameObject($"Crew orders ({PvpPrototype.ShipBName} test, ship {ship})");
            holder.SetActive(false);
            holder.transform.SetParent(craft.transform, false); // the ship's, for event routing
            copy = holder.AddComponent<CommandManager>();
            foreach (var field in typeof(CommandManager).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (field.IsDefined(typeof(SerializeField), false) || field.IsPublic)
                    field.SetValue(copy, field.GetValue(mine));
            holder.SetActive(true);
            OrdersField.SetValue(null, mine);
            copiedOrders[ship] = copy;
            Plugin.Log.LogInfo($"PvP: ship {ship}'s crew orders: a copy of yours");
            return copy;
        }
    }
}
