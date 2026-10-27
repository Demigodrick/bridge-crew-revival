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
                new Hook(AccessTools.Method(typeof(UnmannedStationSync), "NetworkSerialize"), nameof(UnmannedSend_Prefix), true),
                new Hook(AccessTools.Method(typeof(Bridge), "SendConsoleMessage"), nameof(Message_Prefix), true),
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
        }

        private sealed class Hook
        {
            public readonly MethodBase Method;
            public readonly string Patch;
            public readonly bool Prefix;
            public Hook(MethodBase method, string patch, bool prefix) { Method = method; Patch = patch; Prefix = prefix; }
        }

        private static bool Active => PvpPrototype.Active;
        private static bool OnB => PvpShips.Mine == "B";

        // The bridge (the briefing room has stations too: only the bridge gets a second set).
        private static bool OnBridge => GamePhaseManager.IEFBKOMBFGO != null && GamePhaseManager.IEFBKOMBFGO.GetCurrentGamePhase() == typeof(GamePhase_Bridge);

        /// <summary>Is this station (or console) part of the second set, ship B's?</summary>
        internal static bool IsCopy(NovaStation station) => station != null && copies.Contains(station);

        internal static bool IsCopy(ConsoleBase console) => copies.Any(s => s != null && s.GHJLPNMIOAJ == console);

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
            if (dead > 0 || copies.Count == 0) handedBack = false;
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

        private static void SetVisible(IEnumerable<GameObject> roots, bool visible)
        {
            foreach (var root in roots)
            {
                if (root == null) continue;
                foreach (var r in root.GetComponentsInChildren<Renderer>(true)) r.enabled = visible;
                foreach (var c in root.GetComponentsInChildren<Collider>(true)) c.enabled = visible;
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
