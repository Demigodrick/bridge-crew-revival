using System.Linq;
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
            var stationButton = AccessTools.Method(typeof(GazeTargetCrewBotUI), "CFOMPOLCEPO"); // OnStationButton
            if (stationButton != null) harmony.Patch(stationButton, prefix: new HarmonyMethod(typeof(PvpCrewMenu), nameof(StationButton_Prefix)),
                                                     transpiler: new HarmonyMethod(typeof(PvpCrewMenu), nameof(StationButton_Transpiler)));
            else Plugin.Log.LogWarning("PvP: the crew menu's station switch wasn't found; with 4 or more players in a PvP room, nobody can switch stations from it");
            var hover = AccessTools.Method(typeof(GazeTargetCrewBotUI), "GAODHGDMMOB");   // OnCrewHover
            if (hover != null) harmony.Patch(hover, prefix: new HarmonyMethod(typeof(PvpCrewMenu), nameof(Hover_Prefix)),
                                             postfix: new HarmonyMethod(typeof(PvpCrewMenu), nameof(Hover_Postfix)));
            var managerUpdate = AccessTools.Method(typeof(NonVRCrewBotManager), "BatchUpdate");
            if (managerUpdate != null) harmony.Patch(managerUpdate, postfix: new HarmonyMethod(typeof(PvpCrewMenu), nameof(Manager_Postfix)));
            var buttons = AccessTools.Method(typeof(GazeTargetCrewBotUI), "NPIMFNINENI"); // UpdateStationButtons
            if (buttons != null) harmony.Patch(buttons, postfix: new HarmonyMethod(typeof(PvpCrewMenu), nameof(Buttons_Postfix)));
            else Plugin.Log.LogWarning("PvP: the crew menu's station buttons weren't found; a station the other ship's player holds can't be picked from it");
            if (stations == null || seated == null) Plugin.Log.LogWarning("PvP: the crew menu's stations or the crew's seat count weren't found; crew orders may not work");
            else Plugin.Log.LogInfo("PvP: crew menus and crews count only their own ship's players");
        }

        private static bool On => PvpLobby.InPvpRoom && PvpPrototype.Active;

        // The crew menu's station buttons (Flight Control, Operations, Tactical: moving to that station) are off for
        // any station a player holds (GazeTargetCrewBotUI.UpdateStationButtons), whatever their ship: a captain
        // couldn't move to helm while the other ship's player sat at theirs. Only your own ship's players count.
        private static readonly string[] ButtonFields = { "_helmStationButton", "_engineerStationButton", "_tacticalStationButton" };
        private static readonly string[] ProxyFields = { "LCOOLCBNLDN", "PPEEAIPDBNE", "HEOKEBEBIJK" }; // _consoleInteractionListenerProxy2, 3, (1)
        private static readonly FLCHMJJDKDL[] ButtonRoles = { FLCHMJJDKDL.Helm, FLCHMJJDKDL.Engineering, FLCHMJJDKDL.Tactical };

        // Moving to another station from the crew menu (OnStationButton) is refused once the room has 4 players ("A
        // player has joined the room": a fifth would have no station). A PvP room has two ships' stations: only your
        // own ship's players count.
        private static System.Collections.Generic.IEnumerable<CodeInstruction> StationButton_Transpiler(System.Collections.Generic.IEnumerable<CodeInstruction> code)
        {
            var count = AccessTools.Method(typeof(PvpCrewMenu), nameof(PlayersForStations));
            int replaced = 0;
            foreach (var instruction in code)
            {
                if ((instruction.opcode == System.Reflection.Emit.OpCodes.Callvirt || instruction.opcode == System.Reflection.Emit.OpCodes.Call)
                    && instruction.operand is System.Reflection.MethodInfo m && m.Name == "get_playerCount")
                {
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Pop).MoveLabelsFrom(instruction); // the room
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, count);
                    replaced++;
                    continue;
                }
                yield return instruction;
            }
            if (replaced == 0) Plugin.Log.LogWarning("PvP: the crew menu's player count wasn't found; with 4 or more players in a PvP room, station switches stay refused");
        }

        // ---- Diagnostics: pointing at a crew member with the mouse (desktop) -----------------------------------
        // The mouse picks a station through NonVRCrewBotManager (Hovered: a station box with AIStation on), which
        // tells every crew menu (OnCrewHover); the menu whose HoverStation matches opens. In PvP each role has two
        // bots, two menus: logged as it happens (changes only), to see which step fails for a station.
        private static readonly AccessTools.FieldRef<NonVRCrewBotManager, FLCHMJJDKDL> Hovered = AccessTools.FieldRefAccess<NonVRCrewBotManager, FLCHMJJDKDL>("GEGIJPGHBAA");
        private static readonly AccessTools.FieldRef<NonVRCrewBotManager, UnityEngine.BoxCollider[]> StationBoxes = AccessTools.FieldRefAccess<NonVRCrewBotManager, UnityEngine.BoxCollider[]>("HNNNDKIEONM");
        private static FLCHMJJDKDL lastHovered = FLCHMJJDKDL.COUNT;
        private static string lastStations;

        private static void Manager_Postfix(NonVRCrewBotManager __instance)
        {
            if (!On || __instance == null) return;
            try
            {
                var ai = AIStation(__instance);
                var boxes = StationBoxes(__instance);
                if (ai != null && boxes != null)
                {
                    var stations = string.Join(", ", Enumerable.Range(0, Math.Min(ai.Length, boxes.Length)).Select(i =>
                        $"{(FLCHMJJDKDL)i} {(ai[i] ? "pickable" : "not pickable")}{(boxes[i] == null ? ", no box" : $", box '{boxes[i].name}' at {boxes[i].transform.position}{(boxes[i].enabled && boxes[i].gameObject.activeInHierarchy ? "" : " (off)")}")}").ToArray());
                    if (stations != lastStations) { lastStations = stations; Plugin.Log.LogInfo("PvP: [menu] desktop crew stations: " + stations); }
                }
                var hovered = Hovered(__instance);
                if (hovered != lastHovered)
                {
                    lastHovered = hovered;
                    if (hovered != FLCHMJJDKDL.COUNT) Plugin.Log.LogInfo($"PvP: [menu] mouse on the {hovered} station");
                }
            }
            catch (Exception e) { if (told.Add("menudiag")) Plugin.Log.LogWarning("PvP: [menu] crew station check: " + e.Message); }
        }

        // Picking a station with the mouse opens the crew menu whose HoverStation is that station. Each bot has its
        // own menu, and in PvP each role has two bots: the other ship's (switched off and unseen on your bridge when
        // its ship's player sits there) answered too, and your ship's bot's menu took its station from its console
        // as it started, before that bot had one on a crew member's game: picking tactical opened nothing you could
        // see. A bot's menu now takes its station from where its bot sits, and only your own ship's bots' menus answer.
        private static readonly AccessTools.FieldRef<GazeTargetCrewBotUI, UnityEngine.GameObject> BotPawn = AccessTools.FieldRefAccess<GazeTargetCrewBotUI, UnityEngine.GameObject>("IGDLADOLMAL");
        private static readonly AccessTools.FieldRef<GazeTargetCrewBotUI, FLCHMJJDKDL> MenuStation = AccessTools.FieldRefAccess<GazeTargetCrewBotUI, FLCHMJJDKDL>("OHMLDNCIJBJ");
        private static readonly AccessTools.FieldRef<GazeTargetCrewBotUI, FLCHMJJDKDL> HoverStation = AccessTools.FieldRefAccess<GazeTargetCrewBotUI, FLCHMJJDKDL>("BGBCLIFDOEO");
        private static readonly AccessTools.FieldRef<GazeTargetCrewBotUI, bool> Gazed = AccessTools.FieldRefAccess<GazeTargetCrewBotUI, bool>("FBHMKAIJHGE");

        private static NovaPlayerInfo BotOf(GazeTargetCrewBotUI menu)
        {
            var pawn = BotPawn(menu);
            if (pawn == null) return null;
            var info = Tracked.All<NovaPlayerInfo>().FirstOrDefault(p => p != null && p.PawnObject == pawn);
            return info != null && info.AOMGCCGDJJC ? info : null; // a crew bot's menu (the captain's own isn't one)
        }

        private static bool OtherShipsBot(NovaPlayerInfo bot) =>
            PvpCrewBots.ShipBHasBots() && (PvpCrewBots.IsShipBBot(bot) ? "B" : "A") != (PvpShips.Mine ?? "A");

        private static void Hover_Prefix(GazeTargetCrewBotUI __instance)
        {
            if (!On || __instance == null) return;
            try
            {
                var bot = BotOf(__instance);
                if (bot == null || OtherShipsBot(bot) || !(bot.Station is NovaStation seat)) return;
                var role = seat.OGBAPMIPPID;
                if (role == FLCHMJJDKDL.Captain || role == FLCHMJJDKDL.COUNT) return;
                if (MenuStation(__instance) == role && HoverStation(__instance) == role) return;
                Plugin.Log.LogInfo($"PvP: [menu] {bot.FAJABLKBGHN}'s crew menu was for {MenuStation(__instance)} / {HoverStation(__instance)}; now {role}, where it sits");
                MenuStation(__instance) = role;
                HoverStation(__instance) = role;
            }
            catch (Exception e) { if (told.Add("hoverfix")) Plugin.Log.LogWarning("PvP: [menu] setting a crew menu's station: " + e.Message); }
        }

        private static readonly System.Collections.Generic.Dictionary<GazeTargetCrewBotUI, string> lastMenu = new System.Collections.Generic.Dictionary<GazeTargetCrewBotUI, string>();

        /// <summary>Leaving the bridge: the crew menus noted for the log are let go (a destroyed menu still held here
        /// kept everything it pointed at: its buttons, its bot, the bridge).</summary>
        internal static void Forget()
        {
            lastMenu.Clear();
            lastStations = lastButtons = null;
            lastHovered = FLCHMJJDKDL.COUNT;
        }

        private static void Hover_Postfix(GazeTargetCrewBotUI __instance)
        {
            if (!On || __instance == null) return;
            try
            {
                var own = BotOf(__instance);
                if (own != null && OtherShipsBot(own) && Gazed(__instance)) Gazed(__instance) = false; // not your ship's bot
            }
            catch { }
            if (lastHovered == FLCHMJJDKDL.COUNT) return;
            try
            {
                var t = Traverse.Create(__instance);
                var pawn = t.Field("IGDLADOLMAL").GetValue() as UnityEngine.GameObject;   // BotPawn
                var info = pawn == null ? null : Tracked.All<NovaPlayerInfo>().FirstOrDefault(p => p != null && p.PawnObject == pawn);
                string bot = info == null ? "no bot" : $"{info.FAJABLKBGHN} (ship {(PvpCrewBots.IsShipBBot(info) ? "B" : "A")}'s bot{(info.Station is NovaStation st ? $" at {st.name}" : "")})";
                string line = $"menu {t.Field("OHMLDNCIJBJ").GetValue()} / hover {t.Field("BGBCLIFDOEO").GetValue()} of {bot}: "
                            + $"{(__instance.gameObject.activeInHierarchy ? "active" : "inactive")}, gazed {t.Field("FBHMKAIJHGE").GetValue()}, open {t.Field("ABLOCMKECPH").GetValue()}";
                if (lastMenu.TryGetValue(__instance, out var was) && was == line) return;
                lastMenu[__instance] = line;
                Plugin.Log.LogInfo($"PvP: [menu] mouse on {lastHovered}: crew menu {line}");
            }
            catch (Exception e) { if (told.Add("hoverdiag")) Plugin.Log.LogWarning("PvP: [menu] crew menu check: " + e.Message); }
        }

        private static void StationButton_Prefix(FLCHMJJDKDL __0)
        {
            if (On) Plugin.Log.LogInfo($"PvP: [stations] crew menu: you asked to move to {__0}");
        }

        private static string lastButtons;

        public static int PlayersForStations()
        {
            var room = Patches.PhotonNetworkType?.GetMethod("get_room", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.Invoke(null, null);
            int all = room?.GetType().GetMethod("get_playerCount")?.Invoke(room, null) is int n ? n : 0;
            if (!On) return all;
            string mine = PvpShips.Mine ?? "A";
            int own = PvpShips.AllPlayers.Count(p => PvpShips.Of(p) == mine);
            if (all >= 4 && own < 4 && told.Add("count")) Plugin.Log.LogInfo($"PvP: crew menu station switch counts your ship's {own} players, not the room's {all}");
            return own;
        }

        private static void Buttons_Postfix(GazeTargetCrewBotUI __instance)
        {
            if (!On || __instance == null) return;
            try
            {
                string myShip = PvpShips.Mine ?? "A";
                var taken = new System.Collections.Generic.HashSet<FLCHMJJDKDL>();   // by a player on your ship (you included)
                var theirs = new System.Collections.Generic.HashSet<FLCHMJJDKDL>();  // by the other ship's only
                foreach (var player in Tracked.All<NovaPlayerInfo>())
                {
                    if (player == null || (player.AOMGCCGDJJC && !player.AOLEJMABHPE) || !(player.Station is NovaStation station)) continue; // bots
                    bool own = player.AOLEJMABHPE || PvpShips.Of(PvpShips.Player(player.NJNHJKEPFAK)) == myShip;
                    (own ? taken : theirs).Add(station.OGBAPMIPPID);
                }
                for (int i = 0; i < ButtonRoles.Length; i++)
                {
                    var role = ButtonRoles[i];
                    if (!theirs.Contains(role) || taken.Contains(role)) continue;
                    if (AccessTools.Field(typeof(GazeTargetCrewBotUI), ButtonFields[i])?.GetValue(__instance) is UnityEngine.Behaviour button && button != null)
                        button.enabled = true;
                    var proxy = AccessTools.Field(typeof(GazeTargetCrewBotUI), ProxyFields[i])?.GetValue(__instance);
                    var state = proxy == null ? null : AccessTools.Property(proxy.GetType(), "COHKAHJKNII"); // MyState
                    if (state != null) state.SetValue(proxy, Enum.Parse(state.PropertyType, "Valid"), null);
                    if (told.Add("button" + role)) Plugin.Log.LogInfo($"PvP: crew menu's {role} station button on (held only by the other ship's player)");
                }
                var shown = string.Join(", ", ButtonRoles.Select((r, i) =>
                {
                    var b = AccessTools.Field(typeof(GazeTargetCrewBotUI), ButtonFields[i])?.GetValue(__instance) as UnityEngine.Behaviour;
                    string who = taken.Contains(r) ? "your ship's player" : theirs.Contains(r) ? "the other ship's player" : "nobody";
                    return $"{r} {(b == null ? "no button" : b.enabled ? "on" : "off")} (held by {who})";
                }).ToArray());
                if (shown != lastButtons) { lastButtons = shown; Plugin.Log.LogInfo("PvP: [stations] crew menu's station buttons: " + shown); }
            }
            catch (Exception e)
            {
                if (told.Add("buttonerror")) Plugin.Log.LogWarning("PvP: the crew menu's station buttons: " + (e.InnerException ?? e).Message);
            }
        }

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
