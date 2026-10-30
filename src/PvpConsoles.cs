using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sector.Spacecraft;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StbcRevival
{
    /// <summary>
    /// PvP test: a second set of stations and consoles for the second ship. The game sends a player's input as
    /// the state of their console (NovaPlayerInfo.NetworkSerialize, to everyone), and every game applies it to
    /// its own console at that player's station, which drives its own ship. With one set, ship B's input moved
    /// ship A. Here every bridge gets four more stations (indexes 4-7, the same roles, in the same places) with
    /// their own consoles, bound to ship B on every game; ship B's players are seated there:
    ///   - NovaStation.Awake: each station is copied once (the copy loads its own console scene, through the
    ///     level loader's queue) and registered after the four originals (GGPMPCDFHAK.RegisterStation), so
    ///     lookups by role still find the originals and station 4-7 are the copies.
    ///   - ConsoleBase.ILKCJBGLJPI (a player ship spawned): original consoles bind only to ship A, copies only
    ///     to ship B (on games on ship A, ship B is announced as an ordinary ship, so copies are bound here).
    ///   - Bridge.MBCIEEJDGBB (a console loaded): the bridge's helm / tactical / engineering / captain
    ///     consoles, read all over the game, are your own set's. Any other one-per-game object in the other
    ///     set's console scenes is handed back to its twin in yours.
    ///   - NovaSpawnManager.NJHADEBHMNN (seating a player): ship B's players get the copy of their station.
    ///   - The other set is hidden on your bridge (drawn, touched and canvases), as it sits where yours does.
    /// Only while the PvP test runs. Ship B has no AI crew yet: the AI works through the host's own bridge.
    /// </summary>
    internal static class PvpConsoles
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags Statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        private static readonly HashSet<NovaStation> copies = new HashSet<NovaStation>();
        private static readonly Dictionary<NovaStation, NovaStation> twinOf = new Dictionary<NovaStation, NovaStation>(); // both ways
        private static readonly Dictionary<NovaStation, Scene> sceneOf = new Dictionary<NovaStation, Scene>();
        private static bool copying;
        private static bool handedBack;
        private static float nextHide;

        private static FieldInfo consoleData, consoleParent, loaderQueue, stationList, unloadedList;
        private static MethodInfo getStationByRole;
        private static readonly FieldInfo NextStation = AccessTools.Field(typeof(NovaStation), "_next");
        private static readonly FieldInfo PreviousStation = AccessTools.Field(typeof(NovaStation), "_previous");
        private static bool linked;
        private static MethodInfo bindConsole;

        internal static void Patch(Harmony harmony)
        {
            var registry = AccessTools.TypeByName("GGPMPCDFHAK");
            consoleData = AccessTools.Field(typeof(NovaStation), "_consoleData");
            consoleParent = consoleData == null ? null : AccessTools.Field(consoleData.FieldType, "ConsoleParent");
            loaderQueue = AccessTools.Field(typeof(ConsoleLoader), "BGJAFEBKCPD");
            stationList = registry == null ? null : AccessTools.Field(registry, "NAIGHOOPLNL");
            unloadedList = registry == null ? null : AccessTools.Field(registry, "EKHOODMFJNG");
            bindConsole = AccessTools.Method(typeof(ConsoleBase), "ILKCJBGLJPI");
            var targets = new[]
            {
                new Hook(AccessTools.Method(typeof(NovaStation), "Awake"), nameof(StationAwake_Postfix), false),
                new Hook(registry == null ? null : AccessTools.Method(registry, "RegisterStation"), nameof(Register_Prefix), true),
                new Hook(AccessTools.Method(typeof(ConsoleLoader), "Awake"), nameof(LoaderAwake_Prefix), true),
                new Hook(bindConsole, nameof(Bind_Prefix), true),
                new Hook(AccessTools.Method(typeof(Bridge), "MBCIEEJDGBB"), nameof(BridgeConsole_Prefix), true),
                new Hook(AccessTools.Method(AccessTools.TypeByName("NovaSpawnManager"), "NJHADEBHMNN"), nameof(Seat_Prefix), true),
                new Hook(AccessTools.Method(AccessTools.TypeByName("NovaSpawnManager"), "CFLMIFGGHEB"), nameof(Seat_Prefix), true), // players with a custom avatar
                new Hook(AccessTools.Method(typeof(UnmannedStationSync), "NetworkSerialize"), nameof(UnmannedSend_Prefix), true),
                new Hook(AccessTools.Method(typeof(Bridge), "SendConsoleMessage"), nameof(Message_Prefix), true),
                new Hook(AccessTools.Method(typeof(NovaPlayerInfo), "NetworkDeserialize"), nameof(PlayerState_Prefix), true),
                new Hook(AccessTools.Method(typeof(UnmannedStationSync), "NetworkDeserialize"), nameof(UnmannedReceive_Prefix), true),
            };
            if (targets.Any(t => t.Method == null) || consoleData == null || consoleParent == null || loaderQueue == null || stationList == null || unloadedList == null)
            {
                Plugin.Log.LogWarning("PvP: the second console set's hooks weren't all found; both ships share one set of consoles");
                return;
            }
            foreach (var target in targets)
            {
                var hook = new HarmonyMethod(typeof(PvpConsoles), target.Patch);
                if (target.Prefix) harmony.Patch(target.Method, prefix: hook);
                else harmony.Patch(target.Method, postfix: hook);
            }
            harmony.Patch(AccessTools.Method(registry, "RegisterStation"), postfix: new HarmonyMethod(typeof(PvpConsoles), nameof(Order_Postfix)));
            harmony.Patch(AccessTools.Method(typeof(NovaPlayerInfo), "NetworkDeserialize"), finalizer: new HarmonyMethod(typeof(PvpConsoles), nameof(PlayerState_Finalizer)));
            PvpRouting.Patch(harmony);
            // The swap to a player's role station (after the mission's fade, NKILJLNGDHM): ship B's copy for them.
            getStationByRole = AccessTools.Method(registry, "GetStation", new[] { typeof(FLCHMJJDKDL) });
            var swap = AccessTools.Method(AccessTools.TypeByName("NovaSpawnManager"), "NKILJLNGDHM");
            if (swap != null && getStationByRole != null)
                harmony.Patch(swap, transpiler: new HarmonyMethod(typeof(PvpConsoles), nameof(Swap_Transpiler)));
            else Plugin.Log.LogWarning("PvP: the role station swap wasn't found; ship B's players may be moved to ship A's consoles");
        }

        private sealed class Hook
        {
            public readonly MethodBase Method;
            public readonly string Patch;
            public readonly bool Prefix;
            public Hook(MethodBase method, string patch, bool prefix) { Method = method; Patch = patch; Prefix = prefix; }
        }

        // On the bridge only: with the PvP test merely picked in the briefing room, the game's own console sync is
        // left alone (the master's sync of every station there flooded the crew; the second set exists on the
        // bridge anyway). Once a frame: the sync hooks ask on every network tick.
        private static int activeFrame = -1;
        private static bool active;

        private static bool Active
        {
            get
            {
                if (activeFrame != Time.frameCount)
                {
                    activeFrame = Time.frameCount;
                    active = PvpPrototype.Active && GameState.Phase() == "GamePhase_Bridge";
                }
                return active;
            }
        }
        private static bool OnB => PvpShips.Mine == "B";

        // The bridge (the briefing room has stations too: only the bridge gets a second set).
        private static bool OnBridge => GamePhaseManager.IEFBKOMBFGO != null && GamePhaseManager.IEFBKOMBFGO.GetCurrentGamePhase() == typeof(GamePhase_Bridge);

        /// <summary>Is this station (or console) part of the second set, ship B's?</summary>
        internal static bool IsCopy(NovaStation station) => station != null && copies.Contains(station);

        internal static bool IsCopy(ConsoleBase console) => copies.Any(s => s != null && s.GHJLPNMIOAJ == console);

        /// <summary>The same console in the other set (ship A's for ship B's, and back), or null.</summary>
        internal static T Twin<T>(T console) where T : ConsoleBase
        {
            if (console == null) return null;
            foreach (var pair in twinOf)
                if (pair.Key != null && pair.Key.GHJLPNMIOAJ == console) return pair.Value == null ? null : pair.Value.GHJLPNMIOAJ as T;
            return null;
        }

        private static readonly MethodInfo SetAI = AccessTools.Method(typeof(ConsoleBase), "set_AI");

        // The game seats its crew bots at the original stations only: the copies get their twins' (the crew
        // counts a console with a bot and no player as an AI station).
        private static void ShareBots()
        {
            if (SetAI == null) return;
            foreach (var copy in copies)
            {
                var console = copy == null ? null : copy.GHJLPNMIOAJ;
                if (console == null || console.LPMMGGIAOCL != null || !twinOf.TryGetValue(copy, out var original) || original == null) continue;
                var bot = original.GHJLPNMIOAJ == null ? null : original.GHJLPNMIOAJ.LPMMGGIAOCL;
                if (bot == null) continue;
                SetAI.Invoke(console, new object[] { bot });
                Plugin.Log.LogInfo($"PvP: ship B's {copy.OGBAPMIPPID} console crewed by {bot.FAJABLKBGHN}");
            }
        }

        private static readonly Dictionary<Transform, string> setRoots = new Dictionary<Transform, string>();
        private static float nextRoots;

        /// <summary>The ship whose console set this object is in ("A", "B"), or null if it's in neither.</summary>
        internal static string SetOf(Component part)
        {
            if (copies.Count == 0 || part == null) return null;
            if (Time.unscaledTime > nextRoots)
            {
                nextRoots = Time.unscaledTime + 1f;
                setRoots.Clear();
                foreach (var station in twinOf.Keys.Where(s => s != null))
                    foreach (var root in Roots(station))
                        if (root != null) setRoots[root.transform] = copies.Contains(station) ? "B" : "A";
            }
            for (var t = part.transform; t != null; t = t.parent)
                if (setRoots.TryGetValue(t, out var set)) return set;
            return null;
        }

        // ---- Stations ---------------------------------------------------------------------------

        private static void StationAwake_Postfix(NovaStation __instance)
        {
            if (copying || !Active || !OnBridge || copies.Contains(__instance) || twinOf.ContainsKey(__instance)) return;
            ForgetOldBridge();
            NovaStation copy;
            copying = true;
            try
            {
                var go = (GameObject)UnityEngine.Object.Instantiate(__instance.gameObject, __instance.transform.parent, false);
                go.transform.localPosition = __instance.transform.localPosition;
                go.transform.localRotation = __instance.transform.localRotation;
                go.transform.localScale = __instance.transform.localScale;
                go.name = __instance.gameObject.name + " (PvP B)";
                copy = go.GetComponent<NovaStation>();
            }
            finally
            {
                copying = false;
            }
            if (copy == null) return;
            twinOf[__instance] = copy;
            twinOf[copy] = __instance;
            // Its console needs a parent of its own (the copy's Awake has already queued the console scene; the
            // parent is only read when that scene arrives, in ConsoleLoader.Awake).
            var data = consoleData.GetValue(copy);
            var originalParent = consoleParent.GetValue(consoleData.GetValue(__instance)) as GameObject;
            if (data != null && originalParent != null && ReferenceEquals(consoleParent.GetValue(data), originalParent))
            {
                var parent = new GameObject(originalParent.name + " (PvP B)");
                parent.transform.SetParent(originalParent.transform.parent, false);
                parent.transform.localPosition = originalParent.transform.localPosition;
                parent.transform.localRotation = originalParent.transform.localRotation;
                parent.transform.localScale = originalParent.transform.localScale;
                consoleParent.SetValue(data, parent);
            }
            Plugin.Log.LogInfo($"PvP: station {__instance.name} ({__instance.OGBAPMIPPID}) copied for ship B");
        }

        // The copies go after the originals (in role order among themselves), so station 4-7 are ship B's and
        // every lookup by role still finds ship A's.
        private static bool Register_Prefix(NovaStation __0)
        {
            if (!copying) return true;
            copies.Add(__0);
            var list = (IList)stationList.GetValue(null);
            var unloaded = (IList)unloadedList.GetValue(null);
            if (list.Contains(__0)) return false;
            int index = list.Count;
            for (int i = 0; i < list.Count; i++)
                if (list[i] is NovaStation other && copies.Contains(other) && __0.OGBAPMIPPID < other.OGBAPMIPPID)
                {
                    index = i;
                    break;
                }
            list.Insert(index, __0);
            unloaded.Add(__0);
            return false;
        }

        // Each copy is made as its original wakes, so an original waking later was sorted in among the copies
        // (by role): station numbers then depended on load order, a ship B Captain sat at 3. After every
        // registration: originals in role order, then copies in role order, the same on every game.
        private static void Order_Postfix()
        {
            if (copies.Count == 0 || !(stationList.GetValue(null) is IList list)) return;
            var stations = list.Cast<object>().ToList();
            var sorted = stations.OrderBy(s => s is NovaStation st && copies.Contains(st) ? 1 : 0)
                .ThenBy(s => s is NovaStation st ? (int)st.OGBAPMIPPID : int.MaxValue).ToList(); // stable: ties keep their order
            if (sorted.SequenceEqual(stations)) return;
            list.Clear();
            foreach (var station in sorted) list.Add(station);
        }

        // Which loaded copy of a console scene belongs to which station (both copies have the same name).
        private static void LoaderAwake_Prefix(ConsoleLoader __instance)
        {
            if (!Active || !(loaderQueue.GetValue(null) is IList queue) || queue.Count == 0) return;
            var station = AccessTools.Property(queue[0].GetType(), "Station")?.GetValue(queue[0], null) as NovaStation;
            if (station != null) sceneOf[station] = __instance.gameObject.scene;
        }

        // A new bridge: the old one's stations are gone (destroyed with its scene).
        private static void ForgetOldBridge()
        {
            int dead = copies.RemoveWhere(st => st == null);
            foreach (var gone in twinOf.Keys.Where(st => st == null).ToList()) twinOf.Remove(gone);
            foreach (var gone in sceneOf.Keys.Where(st => st == null).ToList()) sceneOf.Remove(gone);
            if (dead > 0 || copies.Count == 0) handedBack = linked = false;
        }

        // ---- Role stations -------------------------------------------------------------------------

        // NKILJLNGDHM(role, player, ...) looks up "the station for this role" (GGPMPCDFHAK.GetStation(role):
        // always ship A's): for a ship B player, the call becomes StationForRole(role, player).
        private static IEnumerable<HarmonyLib.CodeInstruction> Swap_Transpiler(IEnumerable<HarmonyLib.CodeInstruction> code)
        {
            var mine = AccessTools.Method(typeof(PvpConsoles), nameof(StationForRole));
            foreach (var instruction in code)
            {
                if ((instruction.opcode == System.Reflection.Emit.OpCodes.Call || instruction.opcode == System.Reflection.Emit.OpCodes.Callvirt)
                    && instruction.operand as MethodInfo == getStationByRole)
                {
                    yield return new HarmonyLib.CodeInstruction(System.Reflection.Emit.OpCodes.Ldarg_2); // the player
                    yield return new HarmonyLib.CodeInstruction(System.Reflection.Emit.OpCodes.Call, mine);
                    continue;
                }
                yield return instruction;
            }
        }

        public static NovaStation StationForRole(FLCHMJJDKDL role, NovaPlayerInfo player)
        {
            var station = getStationByRole.Invoke(null, new object[] { role }) as NovaStation;
            if (!Active || station == null || player == null || player.AOMGCCGDJJC) return station;
            if (PvpShips.Of(PvpShips.Player(player.NJNHJKEPFAK)) != "B") return station;
            return twinOf.TryGetValue(station, out var copy) && copy != null ? copy : station;
        }

        // The copies' next/previous stations (station cycling) pointed at the originals: link them to each other.
        private static void LinkCopies()
        {
            if (linked || NextStation == null || PreviousStation == null) return;
            foreach (var copy in copies)
            {
                if (copy == null || !twinOf.TryGetValue(copy, out var original) || original == null) return;
                if (NextStation.GetValue(original) is NovaStation next && twinOf.TryGetValue(next, out var nextCopy)) NextStation.SetValue(copy, nextCopy);
                if (PreviousStation.GetValue(original) is NovaStation previous && twinOf.TryGetValue(previous, out var previousCopy)) PreviousStation.SetValue(copy, previousCopy);
            }
            linked = true;
        }

        // ---- Binding to ships -------------------------------------------------------------------

        // Original consoles drive ship A, copies ship B.
        private static bool Bind_Prefix(ConsoleBase __instance, INPOODEDFLG __0)
        {
            if (!Active || __0?.playerShip == null) return true;
            bool copy = IsCopy(__instance);
            return copy == (PvpPrototype.Letter(__0.playerShip) == "B");
        }

        /// <summary>Ship B spawned (announced as an ordinary ship on games on ship A): bind the copies to it.</summary>
        internal static void BindCopies(PlayerSpacecraft shipB)
        {
            if (shipB == null || bindConsole == null) return;
            int bound = 0;
            foreach (var station in copies)
            {
                var console = station == null ? null : station.GHJLPNMIOAJ;
                if (console == null) continue;
                bindConsole.Invoke(console, new object[] { new INPOODEDFLG(shipB) });
                bound++;
            }
            Plugin.Log.LogInfo($"PvP: {bound} ship B consoles bound to {shipB.name}");
        }

        // The bridge's own consoles (Bridge.HelmConsole and the rest) are your ship's set.
        private static bool BridgeConsole_Prefix(AIAJMCJDLKJ __0)
        {
            if (!Active || __0?.Station == null) return true;
            return IsCopy(__0.Station) == OnB;
        }

        // Ship B's players sit at the copy of their station.
        private static void Seat_Prefix(NovaPlayerInfo __0, int __1, ref int __4)
        {
            if (!Active || __0 == null || __0.AOMGCCGDJJC) return;
            if (PvpShips.Of(PvpShips.Player(__1)) != "B") return;
            var list = (IList)stationList.GetValue(null);
            if (__4 < 0 || __4 >= list.Count || !(list[__4] is NovaStation station) || copies.Contains(station)) return;
            if (!twinOf.TryGetValue(station, out var copy) || copy == null) return;
            int index = list.IndexOf(copy);
            if (index < 0) return;
            Plugin.Log.LogInfo($"PvP: {__0.FAJABLKBGHN} (ship B) seated at station {index} instead of {__4}");
            __4 = index;
        }

        // ---- Safety net -----------------------------------------------------------------------------

        private static readonly AccessTools.FieldRef<NovaPlayerInfo, ConsoleBase> PlayerConsole =
            AccessTools.FieldRefAccess<NovaPlayerInfo, ConsoleBase>("KHDENOMKIJE");
        private static readonly HashSet<string> warnedMismatch = new HashSet<string>();

        // A player's console state applied to a console of the other ship would drive that ship with their
        // input (and its engine with their ship's position). With the console left out for that one packet, the
        // game skips the state, as for a player with no console yet.
        private static void PlayerState_Prefix(NovaPlayerInfo __instance, out ConsoleBase __state)
        {
            __state = null;
            if (!Active || copies.Count == 0 || __instance == null || __instance.AOMGCCGDJJC) return;
            var console = PlayerConsole(__instance);
            if (console == null) return;
            bool playerOnB = PvpShips.Of(PvpShips.Player(__instance.NJNHJKEPFAK)) == "B";
            if (IsCopy(console) == playerOnB) return;
            __state = console;
            PlayerConsole(__instance) = null;
            if (warnedMismatch.Add(__instance.FAJABLKBGHN ?? "?"))
                Plugin.Log.LogWarning($"PvP: {__instance.FAJABLKBGHN} is on ship {(playerOnB ? "B" : "A")} but sits at the other ship's console; their console state is ignored");
        }

        private static Exception PlayerState_Finalizer(NovaPlayerInfo __instance, ConsoleBase __state, Exception __exception)
        {
            if (__state != null && __instance != null) PlayerConsole(__instance) = __state;
            return __exception;
        }

        // ---- Unmanned stations --------------------------------------------------------------------

        // UnmannedStationSync, as the game's but over every station by number (the game's covers stations by
        // role: the first four). The host sends the state of consoles nobody is ready to own, and marks a
        // seated player's console ready once it has a ship; with two sets, ship B's stations need the same.
        private static bool UnmannedSend_Prefix(SVR.Network.PLPKPDFOLDL __0, ref bool __result)
        {
            if (!Active || copies.Count == 0) return true;
            var stream = __0;
            uint start = stream.LLPAKGEPJJP;
            stream.Reserve(1u);
            byte count = 0;
            var list = (IList)stationList.GetValue(null);
            for (int i = 0; i < list.Count && i < 255; i++)
            {
                var console = (list[i] as NovaStation)?.GHJLPNMIOAJ;
                if (console == null || (console.FKKJLMLCKJI != null && console.FKKJLMLCKJI.ReadyToOwnConsole)) continue;
                stream.WriteByte((byte)i);
                if (!console.ConsoleSerialize(stream))
                {
                    __result = false;
                    return false;
                }
                if (console.FKKJLMLCKJI != null && console.DADHBNBICAJ != null) console.FKKJLMLCKJI.ReadyToOwnConsole = true;
                count++;
            }
            uint end = stream.LLPAKGEPJJP;
            stream.Seek(start);
            stream.WriteByte(count);
            stream.Seek(end);
            __result = !stream.OKCBBENHIBC;
            return false;
        }

        private static bool UnmannedReceive_Prefix(SVR.Network.PLPKPDFOLDL __0, float __1, ref bool __result)
        {
            if (!Active || copies.Count == 0) return true;
            var stream = __0;
            __result = false;
            byte count = 0;
            if (!stream.ReadByte(ref count)) return false;
            var list = (IList)stationList.GetValue(null);
            for (int n = 0; n < count; n++)
            {
                byte index = 0;
                if (!stream.ReadByte(ref index)) return false;
                var console = index < list.Count ? (list[index] as NovaStation)?.GHJLPNMIOAJ : null;
                if (console != null && (console.FKKJLMLCKJI == null || !console.FKKJLMLCKJI.ReadyToOwnConsole))
                {
                    if (!console.ConsoleDeserialize(stream, __1)) return false;
                    if (console.FKKJLMLCKJI != null && console.DADHBNBICAJ != null) console.FKKJLMLCKJI.ReadyToOwnConsole = true;
                    continue;
                }
                uint at = stream.LLPAKGEPJJP, length = 0;
                stream.ReadUInt(ref length);
                if (stream.OKCBBENHIBC) return false;
                stream.Seek(at + length);
            }
            __result = !stream.OKCBBENHIBC;
            return false;
        }

        // ---- Messages -----------------------------------------------------------------------------

        private static MethodInfo showMessage;

        // Bridge.SendConsoleMessage tells every bridge, with no ship in it. Each game's messages are about its
        // own ship (the host's are ship A's), so they stay on that game for now. (With crews of several, a
        // ship's other players will need them sent to them alone.)
        private static bool Message_Prefix(Bridge __instance, object __0, string __1, float __2, bool __3, bool __4, string[] __5)
        {
            if (!Active) return true;
            if (PvpRouting.KeepOff(__1)) return false;
            if (showMessage == null) showMessage = AccessTools.Method(typeof(Bridge), "NewConsoleMessageRPC");
            if (showMessage == null) return true;
            showMessage.Invoke(__instance, new object[] { Convert.ToInt32(__0), __1, __2, __3, __4, __5 });
            return false;
        }

        // ---- Your set only ----------------------------------------------------------------------

        /// <summary>Each frame while the PvP test runs: hide the other set, hand its singletons back once.</summary>
        internal static void Update()
        {
            if (!Active || copies.Count == 0 || Time.unscaledTime < nextHide) return;
            nextHide = Time.unscaledTime + 1f;
            LinkCopies();
            ShareBots();
            bool onB = OnB;
            var others = twinOf.Keys.Where(s => s != null && copies.Contains(s) != onB).ToList();
            if (others.Count == 0) return;
            foreach (var station in others) SetVisible(Roots(station), false);
            if (!handedBack && others.All(s => s.GHJLPNMIOAJ != null))
            {
                handedBack = true;
                HandBack(others);
            }
        }

        // A station's own objects, its console (and its parent) and the rest of its console scene.
        private static IEnumerable<GameObject> Roots(NovaStation station)
        {
            yield return station.gameObject;
            var console = station.GHJLPNMIOAJ;
            if (console != null) yield return console.gameObject;
            if (copies.Contains(station) && consoleParent.GetValue(consoleData.GetValue(station)) is GameObject parent) yield return parent;
            if (sceneOf.TryGetValue(station, out var scene) && scene.IsValid() && scene.isLoaded)
                foreach (var root in scene.GetRootGameObjects()) yield return root;
        }

        // Not the crew bots' mouse targets (NonVRCrewBotManager parents one to each bot's station, the original set's):
        // the bots are shared, and those stations sit where yours do, so they're how you pick a bot without a headset.
        private static void SetVisible(IEnumerable<GameObject> roots, bool visible)
        {
            foreach (var root in roots)
            {
                if (root == null) continue;
                foreach (var r in root.GetComponentsInChildren<Renderer>(true)) r.enabled = visible;
                foreach (var c in root.GetComponentsInChildren<Collider>(true))
                    if (visible || !c.name.EndsWith("_NonVRCrewBotCollider", StringComparison.Ordinal)) c.enabled = visible;
                foreach (var c in root.GetComponentsInChildren<Canvas>(true)) c.enabled = visible;
            }
        }

        // The other set's one-per-game objects (a static of their own type, set when they woke) go back to
        // their twins in yours.
        private static void HandBack(List<NovaStation> others)
        {
            var handed = new List<string>();
            foreach (var other in others)
            {
                if (!twinOf.TryGetValue(other, out var mine) || mine == null) continue;
                var mineRoots = Roots(mine).Where(r => r != null).ToList();
                foreach (var root in Roots(other))
                {
                    if (root == null) continue;
                    foreach (var part in root.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (part == null) continue;
                        for (var type = part.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
                        {
                            foreach (var field in type.GetFields(Statics))
                            {
                                if (field.IsLiteral || field.IsInitOnly || !field.FieldType.IsInstanceOfType(part)) continue;
                                if (!ReferenceEquals(field.GetValue(null), part)) continue;
                                var twin = mineRoots.Select(r => r.GetComponentInChildren(part.GetType(), true)).FirstOrDefault(c => c != null);
                                if (twin == null) continue;
                                field.SetValue(null, twin);
                                handed.Add(field.DeclaringType.Name);
                            }
                        }
                    }
                }
            }
            Plugin.Log.LogInfo($"PvP: the other ship's consoles are hidden; handed back to yours: {(handed.Count > 0 ? string.Join(", ", handed.Distinct().ToArray()) : "nothing")}");
        }
    }
}
