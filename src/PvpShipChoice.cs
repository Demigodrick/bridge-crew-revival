using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Sector.Spacecraft;
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
            var preview = AccessTools.Method(typeof(ConsoleBriefingRoom), "HHILGDAGFIM"); // ShowBridgePreview
            if (preview != null) harmony.Patch(preview, prefix: new HarmonyMethod(typeof(PvpShipChoice), nameof(Preview_Prefix)));
            // The SHIP toggles: pressable for either ship's captain (see Selection_Postfix).
            var selection = AccessTools.Method(typeof(ConsoleBriefingRoom), "KJCBOFBCMAN"); // UpdateSelectionInteractable
            if (selection != null) harmony.Patch(selection, postfix: new HarmonyMethod(typeof(PvpShipChoice), nameof(Selection_Postfix)));
            else Plugin.Log.LogWarning("PvP: ConsoleBriefingRoom.UpdateSelectionInteractable not found; ship B's captain may not be able to change ship");
            // The host sending each player their bridge to load (GamePhase_Bridge.OnPhaseSwitchedOnClient, once per
            // player: LoadMultipleLevels(player, Loader.CurrentBridgeScene)).
            var send = AccessTools.Method(typeof(GamePhase_Bridge), "OnPhaseSwitchedOnClient");
            if (send == null) Plugin.Log.LogWarning("PvP: GamePhase_Bridge.OnPhaseSwitchedOnClient not found; every crew loads the host's ship's bridge");
            else harmony.Patch(send, prefix: new HarmonyMethod(typeof(PvpShipChoice), nameof(SendBridge_Prefix)),
                finalizer: new HarmonyMethod(typeof(PvpShipChoice), nameof(SendBridge_Finalizer)));
            // A ship's own updates by its own ship's rules (see Pin).
            var shield = AccessTools.Method(AccessTools.TypeByName("Sector.Spacecraft.Subsystems.BubbleShieldServer"), "BatchUpdate");
            if (shield != null) harmony.Patch(shield, prefix: new HarmonyMethod(typeof(PvpShipChoice), nameof(Shield_Prefix)), finalizer: new HarmonyMethod(typeof(PvpShipChoice), nameof(Back_Finalizer)));
            else Plugin.Log.LogWarning("PvP: BubbleShieldServer.BatchUpdate not found; both ships' shields work by the host's ship's rules");
            foreach (var name in new[] { "DONFBIHMDFE", "KMHNOKFDFAB" }) // BaseSpacecraft.UpdateDamageOverTime, SetStatusEffect
            {
                var dot = AccessTools.Method(typeof(BaseSpacecraft), name);
                if (dot != null) harmony.Patch(dot, prefix: new HarmonyMethod(typeof(PvpShipChoice), nameof(Ship_Prefix)), finalizer: new HarmonyMethod(typeof(PvpShipChoice), nameof(Back_Finalizer)));
                else Plugin.Log.LogWarning($"PvP: BaseSpacecraft.{name} not found; damage over time by the host's ship's rules");
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

        // The console makes its SHIP toggles pressable only for the room's Captain (slot 0, ship A's), and only those
        // not on: ship B's captain could press one (while the console hadn't refreshed yet) and then none, stuck on
        // it. In a PvP room, either ship's captain: each toggle but the one on (the toggles show this game's own
        // ship's pick, SyncBridgeToggles from the flags).
        private static readonly MethodInfo SetInteractable = AccessTools.Method(AccessTools.TypeByName("KHDFGIINMKJ"), "SetInteractable", new[] { typeof(UnityEngine.UI.Selectable), typeof(bool) });

        private static void Selection_Postfix(ConsoleBriefingRoom __instance)
        {
            if (!PvpLobby.InPvpRoom || SetInteractable == null || !CanPick()) return;
            foreach (var name in new[] { "_aegisToggle", "_enterpriseToggle", "_tngToggle" })
            {
                if (!(AccessTools.Field(typeof(ConsoleBriefingRoom), name)?.GetValue(__instance) is UnityEngine.UI.Toggle toggle)) continue;
                if (name == "_tngToggle" && !Loader.FEJCJINLJKP) continue; // IsTNGContentAvailableAndEnabled
                bool want = !toggle.isOn;
                if (toggle.interactable != want) SetInteractable.Invoke(null, new object[] { toggle, want });
            }
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
            try { Holograms(); }
            catch (Exception e) { if (!holoFailed) { holoFailed = true; Plugin.Log.LogWarning("PvP: the tables' ship holograms: " + e.Message); } }
            if (RetroFlag == null || Loader.EJLNDBGLIFL == null || spawning || pinned != null) return;
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

        // ---- The tables' ship holograms ---------------------------------------------------------------------

        // Each table's hologram (Mdl_AegisBriefingRoom, and table B's copy) has its three ship models, which
        // ConsoleBriefingRoomShipHolograms hands to the briefing consoles only when there are exactly four of them
        // (a PvP room has eight), for the console to show the room's one ship (ShowBridgePreview). So in a PvP room
        // the tables showed whichever model was left on. Here each table shows its own ship's pick: table A ship
        // A's, table B (PvpLobby's copy, "(table B)") ship B's; the consoles' own switching is kept out.
        private static float nextHolo;
        private static bool holoFailed;
        private static readonly System.Collections.Generic.HashSet<string> toldHolo = new System.Collections.Generic.HashSet<string>();

        private static void Holograms()
        {
            if (!PvpLobby.InPvpRoom || Time.unscaledTime < nextHolo) return;
            nextHolo = Time.unscaledTime + 0.25f;
            var phases = GamePhaseManager.IEFBKOMBFGO;
            if (phases == null || phases.GetCurrentGamePhase() != typeof(GamePhase_BriefingRoom)) return;
            var holos = UnityEngine.Object.FindObjectsOfType<ConsoleBriefingRoomShipHolograms>();
            bool tableBHas = false;
            foreach (var holo in holos) if (holo != null && OnTableB(holo.transform)) tableBHas = true;
            foreach (var holo in holos)
            {
                if (holo == null) continue;
                string ship = OnTableB(holo.transform) ? "B" : "A";
                int pick = Of(ship);
                var t = Traverse.Create(holo);
                t.Field("MDPLEKPCLKN").SetValue(true); // never hand these to the consoles
                var aegis = t.Field("_aegisModel").GetValue() as GameObject;
                var enterprise = t.Field("_enterpriseModel").GetValue() as GameObject;
                var tng = t.Field("_TNGEnterpriseModel").GetValue() as GameObject;
                var show = pick == EnterpriseD && tng != null ? tng : pick == Enterprise && enterprise != null ? enterprise : aegis;
                foreach (var model in new[] { aegis, enterprise, tng })
                    if (model != null && model.activeSelf != (model == show)) model.SetActive(model == show);
                if (toldHolo.Add(ship + pick)) Plugin.Log.LogInfo($"PvP: table {ship}'s hologram shows {Name(pick)}");
                // Table B's copy without a chooser of its own: its models by the same names, under its copy.
                if (ship == "A" && !tableBHas)
                {
                    var copy = GameObject.Find("Mdl_AegisBriefingRoom (table B)");
                    if (copy == null) continue;
                    int pickB = Of("B");
                    var showB = pickB == EnterpriseD && tng != null ? tng : pickB == Enterprise && enterprise != null ? enterprise : aegis;
                    foreach (var model in new[] { aegis, enterprise, tng })
                    {
                        if (model == null) continue;
                        var twin = FindByName(copy.transform, model.name);
                        if (twin != null && twin.gameObject.activeSelf != (model == showB)) twin.gameObject.SetActive(model == showB);
                    }
                    if (toldHolo.Add("B" + pickB)) Plugin.Log.LogInfo($"PvP: table B's hologram shows {Name(pickB)}");
                }
            }
        }

        private static Transform FindByName(Transform root, string name)
        {
            if (root.name == name) return root;
            foreach (Transform child in root)
            {
                var found = FindByName(child, name);
                if (found != null) return found;
            }
            return null;
        }

        private static bool OnTableB(Transform t)
        {
            for (; t != null; t = t.parent)
                if (t.name.EndsWith(" (table B)", StringComparison.Ordinal)) return true;
            return false;
        }

        // A console's own switch of the table hologram (to the room's one ship): not in a PvP room.
        private static bool Preview_Prefix() => !PvpLobby.InPvpRoom;

        // ---- Code for the other ship: its own ship's rules ------------------------------------------------

        // The flags decide how a ship works, not just how it looks: the AI crew's behaviour trees and helm
        // (USSAegisCrew bakes IsTOSMode in), the shields (BubbleShieldServer: TOS shields stay up at no power and
        // recharge by themselves), damage over time (BaseSpacecraft: TNG's list). The host runs all of that for
        // both ships, so ship B's ran by ship A's rules. While code runs for the other ship (PvpScope's view of it,
        // a ship's shield or damage update), the flags are at that ship's pick.
        private static string pinned;

        /// <summary>The flags at that ship's pick until Unpin (PvpScope's switch to the other ship).</summary>
        internal static void Pin(string ship)
        {
            if (RetroFlag == null || Loader.EJLNDBGLIFL == null || !PvpLobby.InPvpRoom || ship == null) return;
            pinned = ship;
            Set(Of(ship));
        }

        internal static void Unpin()
        {
            if (pinned == null) return;
            pinned = null;
            if (RetroFlag != null && Loader.EJLNDBGLIFL != null && PvpLobby.InPvpRoom) Set(Of(PvpShips.Mine ?? "A"));
        }

        // A ship's own update (shields, damage over time): the flags at its pick around it, if that differs.
        private static int FlagsPick()
        {
            var loader = Loader.EJLNDBGLIFL;
            return Equals(RetroFlag.GetValue(loader), true) ? Enterprise : Equals(TngFlag.GetValue(loader), true) ? EnterpriseD : Aegis;
        }

        private static int Around(object ship)
        {
            if (RetroFlag == null || Loader.EJLNDBGLIFL == null || !PvpPrototype.Active || !(ship is PlayerSpacecraft craft)) return -1;
            int want = Of(PvpPrototype.Letter(craft)), now = FlagsPick();
            if (want == now) return -1;
            Set(want);
            return now;
        }

        /// <summary>The flags at that ship's pick (A, B) if they aren't; the value to give Restore after.</summary>
        internal static int AroundShip(string ship)
        {
            if (RetroFlag == null || Loader.EJLNDBGLIFL == null || !PvpPrototype.Active || ship == null) return -1;
            int want = Of(ship), now = FlagsPick();
            if (want == now) return -1;
            Set(want);
            return now;
        }

        internal static void Restore(int state) => Back(state);

        private static void Back(int state)
        {
            if (state >= 0) Set(state);
        }

        private static void Shield_Prefix(object __instance, out int __state)
        {
            object facade = null;
            try { facade = Traverse.Create(__instance).Field("ICIOFECOJKP").GetValue() ?? Traverse.Create(__instance).Property("ICIOFECOJKP").GetValue(); }
            catch { }
            object craft = null;
            if (facade != null)
                try { craft = Traverse.Create(facade).Property("FMDNNFLLMMN").GetValue() ?? Traverse.Create(facade).Field("<BAPNEMLKIOK>k__BackingField").GetValue(); }
                catch { }
            __state = Around(craft);
        }

        private static void Ship_Prefix(object __instance, out int __state) => __state = Around(__instance);

        private static Exception Back_Finalizer(int __state, Exception __exception)
        {
            Back(__state);
            return __exception;
        }

        // ---- The host sending each crew its own ship's bridge -------------------------------------------

        // The host sent every player its own bridge setting, ship A's (the host's) bridge, so ship B's crew loaded
        // ship A's bridge while their game's flags (this class) said ship B's ship. With both ships the Aegis that
        // was the same; with ship B any other ship, ship B's crew never finished loading (never asked the host for
        // the system's bodies, never seated) and every launch stayed on the loading screen. Now each player is sent
        // the bridge of the ship they crew: the host's flags at that ship's pick while the game reads
        // Loader.CurrentBridgeScene for them, then back.
        private static void SendBridge_Prefix(object __0, out int __state)
        {
            __state = -1;
            if (RetroFlag == null || !PvpLobby.InPvpRoom || Loader.EJLNDBGLIFL == null || __0 == null) return;
            string ship = PvpShips.Of(__0) ?? "A";
            __state = Of(PvpShips.Mine ?? "A");
            spawning = true; // Update leaves the flags alone meanwhile
            Set(Of(ship));
            Plugin.Log.LogInfo($"PvP: player #{PvpShips.Actor(__0)} (ship {ship}) sent {Name(Of(ship))}'s bridge");
        }

        private static Exception SendBridge_Finalizer(int __state, Exception __exception)
        {
            if (__state >= 0)
            {
                Set(__state);
                spawning = false;
            }
            return __exception;
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
