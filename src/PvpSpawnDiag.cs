using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Sector;
using Sector.Spacecraft;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// PvP room: each step of how a crew member's game gets the system's bodies and its seat, logged, as ship B's
    /// crew got neither (no player ship ever arrived; never seated) and every launch stayed on the loading screen.
    /// The game's chain: the crew member's starting system loaded (SectorSpawnManager.OnStartingSystemLoaded) →
    /// RequestToSpawnAllSectorBodies to the host → the host's TellPlayerToSpawnAllSectorBodies sends each body
    /// (Clients_SpawnResource) then SpawnAllSectorBodiesCompleted → the crew member's ClientReadyForSpawning to all →
    /// the host spawns their pawn (parked in _pendingPlayerIds while its own screen is black or it's still loading,
    /// until ProcessPendingSpawns' four flags are set) → SpawnPlayerRPC → seated. Each step is logged, with any
    /// exception it throws (an RPC's exception otherwise goes nowhere a player sees).
    /// </summary>
    internal static class PvpSpawnDiag
    {
        private static int bodiesReceived, bodiesFailed;
        private static string lastFlags;

        internal static void Patch(Harmony harmony)
        {
            var sector = typeof(SectorSpawnManager);
            var spawn = typeof(NovaSpawnManager);
            int patched = 0;
            void Hook(Type type, string name, string prefix)
            {
                var method = AccessTools.Method(type, name);
                if (method == null) { Plugin.Log.LogWarning($"PvP: [spawn] {type.Name}.{name} not found; not logged"); return; }
                harmony.Patch(method, prefix: prefix == null ? null : new HarmonyMethod(typeof(PvpSpawnDiag), prefix),
                    finalizer: new HarmonyMethod(typeof(PvpSpawnDiag), nameof(Finalizer)));
                patched++;
            }
            Hook(sector, "EFEKDHAKNJL", nameof(SystemLoaded_Prefix));                // OnStartingSystemLoaded
            Hook(sector, "RequestToSpawnAllSectorBodies", nameof(AskedForBodies_Prefix));
            Hook(sector, "Clients_SpawnResource", nameof(Body_Prefix));
            Hook(sector, "SpawnAllSectorBodiesCompleted", nameof(BodiesDone_Prefix));
            Hook(spawn, "ClientReadyForSpawning", nameof(Ready_Prefix));
            Hook(spawn, "POKPAEJMHEJ", nameof(Pending_Prefix));                      // ProcessPendingSpawns
            Hook(spawn, "SpawnPlayerRPC", nameof(SpawnPlayer_Prefix));
            // The crew member's own mission setup before it asks for the bodies (GamePhase_Bridge.LoadBridgeRoutine):
            // the host's go-ahead, then each step's start and end; a step that starts and never ends is where it stops.
            void Step(Type type, string name, string label)
            {
                var method = AccessTools.Method(type, name);
                if (method == null) { Plugin.Log.LogWarning($"PvP: [spawn] {type.Name}.{name} not found; not logged"); return; }
                stepNames[method.Name + "@" + type.Name] = label;
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(PvpSpawnDiag), nameof(Step_Prefix)),
                    postfix: new HarmonyMethod(typeof(PvpSpawnDiag), nameof(Step_Postfix)),
                    finalizer: new HarmonyMethod(typeof(PvpSpawnDiag), nameof(Finalizer)));
                patched++;
            }
            Step(typeof(GamePhase_Bridge), "LoadScenarioRPC", "the host's go-ahead to load the mission");
            Step(sector, "LoadResourceMap", "reading the resource map (once a session)");
            Step(typeof(SectorMap), "InitAllSystems", "setting up the sector's systems");
            Step(typeof(SectorMap), "LoadStartingSystem", "loading the starting system");
            Step(sector, "Request_PreInitializePoolData", "asking the host for the spawn pools");
            Plugin.Log.LogInfo($"PvP: [spawn] the load chain is logged in PvP rooms ({patched} of 12 steps)");
        }

        private static bool On => PvpLobby.InPvpRoom;

        private static readonly System.Collections.Generic.Dictionary<string, string> stepNames = new System.Collections.Generic.Dictionary<string, string>();

        private static string StepName(MethodBase m) =>
            m != null && stepNames.TryGetValue(m.Name + "@" + m.DeclaringType?.Name, out var label) ? label : m?.Name ?? "?";

        private static void Step_Prefix(MethodBase __originalMethod)
        {
            if (On) Log("start: " + StepName(__originalMethod));
        }

        // An IEnumerator step (the resource map) only starts here: its work runs as the game steps through it.
        private static void Step_Postfix(MethodBase __originalMethod)
        {
            if (On) Log("done: " + StepName(__originalMethod) + (__originalMethod is MethodInfo mi && mi.ReturnType == typeof(IEnumerator) ? " (handed to a coroutine)" : ""));
        }

        private static void Log(string text) => Plugin.Log.LogInfo("PvP: [spawn] " + text);

        private static void SystemLoaded_Prefix()
        {
            if (!On) return;
            bodiesReceived = bodiesFailed = 0;
            Log("starting system loaded here: asking the host for its bodies");
        }

        private static void AskedForBodies_Prefix(object __0)
        {
            if (On) Log($"player #{PvpShips.Actor(__0)} asked for the system's bodies (sending them)");
        }

        private static void Body_Prefix(string __0)
        {
            if (!On) return;
            bodiesReceived++;
            if (bodiesReceived <= 3) Log($"body {bodiesReceived} arrived: {Short(__0)}");
        }

        private static void BodiesDone_Prefix()
        {
            if (!On) return;
            Log($"the host says that's all the bodies: {bodiesReceived} arrived{(bodiesFailed > 0 ? $", {bodiesFailed} failed" : "")}; "
                + $"player ships here {UnityEngine.Object.FindObjectsOfType<PlayerSpacecraft>().Length}");
        }

        private static void Ready_Prefix(NovaSpawnManager __instance, object __0)
        {
            if (!On) return;
            var local = NovaPlayerManager.IEFBKOMBFGO?.AJPNCBOKCLC as NovaPlayerInfo;
            string loading = local == null ? "no local player" : Traverse.Create(local).Property("PAKMGDMFGFM").GetValue()?.ToString(); // LoadingState
            var fade = CameraFade.IEFBKOMBFGO; // Instance
            bool black = fade != null && Traverse.Create(fade).Property("DOCEPIEFBLG").GetValue() is true; // IsFullyOpaque
            Log($"player #{PvpShips.Actor(__0)} is ready for spawning (here: {loading}, screen {(black ? "black: parked until it's clear" : "clear")})");
        }

        private static void Pending_Prefix(NovaSpawnManager __instance)
        {
            if (!On) return;
            var t = Traverse.Create(__instance);
            var pending = t.Field("NNPJAOAIKPN") /* _pendingPlayerIds */.GetValue() as IList;
            if (pending == null || pending.Count == 0) { lastFlags = null; return; }
            string flags = $"level loaded {Flag(t, "NNIINKJEPBE")}, consoles loaded {Flag(t, "NCNLNIELGAH")}, phase routine done {Flag(t, "EDHNBFPKFHF")}, fourth {Flag(t, "BMPBKPLGEKB")}";
            if (flags == lastFlags) return;
            lastFlags = flags;
            var ids = new System.Collections.Generic.List<string>();
            foreach (var id in pending) ids.Add("#" + id);
            Log($"waiting to spawn {string.Join(" ", ids.ToArray())}: {flags}");
        }

        private static void SpawnPlayer_Prefix()
        {
            if (On) Log("the host spawned a player here (SpawnPlayerRPC)");
        }

        private static string Flag(Traverse t, string name)
        {
            var field = t.Field(name);
            object value = field.FieldExists() ? field.GetValue() : t.Property(name).PropertyExists() ? t.Property(name).GetValue() : null;
            return value is bool b ? (b ? "yes" : "no") : "?";
        }

        private static Exception Finalizer(Exception __exception, MethodBase __originalMethod)
        {
            if (__exception != null && On)
            {
                if (__originalMethod?.Name == "Clients_SpawnResource") bodiesFailed++;
                Plugin.Log.LogWarning($"PvP: [spawn] {__originalMethod?.DeclaringType?.Name}.{__originalMethod?.Name} threw: {__exception}");
            }
            return __exception;
        }

        private static string Short(string json) => json == null ? "null" : json.Length <= 240 ? json : json.Substring(0, 240) + "…";
    }
}
