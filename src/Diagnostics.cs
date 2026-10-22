using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StbcRevival
{
    /// <summary>
    /// Debug logging of the game's startup and connection flow: intro screens, game phases, level loads,
    /// room operations, popups, plus a periodic heartbeat. Enabled by [Debug] Diagnostics.
    /// Applied after the first frame, like the Photon patches (patching JITs the targets).
    /// </summary>
    internal static class Diagnostics
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static float nextHeartbeat;
        private static string lastHeartbeat;

        internal static void Apply(Harmony harmony, Type photonNetwork)
        {
            HookAll(harmony, "IntroElement", "OnEnable", nameof(IntroEnter));
            HookAll(harmony, "IntroElement", "Continue", nameof(IntroContinue));
            HookAll(harmony, "GamePhaseManager", "SwitchToPhase", nameof(Call));
            HookAll(harmony, "GamePhaseManager", "SwitchToPhaseRPC", nameof(Call));
            HookAll(harmony, "NetworkLoadLevel", "LoadMainLevelRPC", nameof(Call));
            HookAll(harmony, "NetworkLoadLevel", "LoadAdditiveLevelRPC", nameof(Call));
            HookAll(harmony, "NetworkLoadLevel", "StartMultiLoadingRPC", nameof(Call));
            HookAll(harmony, "UI.PopupMgr", "ShowPopup", nameof(Call));
            HookAll(harmony, "UI.PopupMgr", "ShowErrorPopup", nameof(Call));
            if (photonNetwork != null)
                foreach (var name in new[] { "CreateRoom", "JoinRoom", "JoinOrCreateRoom", "JoinRandomRoom", "ReJoinRoom",
                                             "JoinLobby", "LeaveLobby", "LeaveRoom", "Disconnect" })
                    foreach (var m in photonNetwork.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == name))
                        Patch(harmony, m, nameof(Call));

            SceneManager.sceneLoaded += (scene, mode) => Plugin.Log.LogInfo($"[diag] scene loaded: {scene.name} ({mode})");

            foreach (var typeName in Plugin.TraceTypes.Value.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0))
                TraceType(harmony, typeName);
        }

        private static readonly string[] NoisyMethods = { "Update", "LateUpdate", "FixedUpdate", "BatchUpdate", "BatchLateUpdate", "OnGUI", "OnRenderObject" };
        private static readonly System.Collections.Generic.Dictionary<MethodBase, int> traceCounts = new System.Collections.Generic.Dictionary<MethodBase, int>();

        /// <summary>Log the first few calls of every method declared on a type (except per-frame ones and accessors).</summary>
        private static void TraceType(Harmony harmony, string typeName)
        {
            var type = AccessTools.TypeByName(typeName);
            if (type == null)
            {
                Plugin.Log.LogWarning($"[diag] trace: type {typeName} not found");
                return;
            }
            int n = 0;
            foreach (var m in type.GetMethods(All | BindingFlags.DeclaredOnly))
            {
                if (m.IsAbstract || m.IsGenericMethodDefinition || m.GetMethodBody() == null) continue;
                if (NoisyMethods.Contains(m.Name) || m.Name.StartsWith("get_") || m.Name.StartsWith("set_")) continue;
                if (m.DeclaringType != type) continue;
                Patch(harmony, m, nameof(Trace));
                n++;
            }
            Plugin.Log.LogInfo($"[diag] tracing {n} methods of {type.FullName}");
        }

        private static void Trace(MethodBase __originalMethod, object[] __args)
        {
            traceCounts.TryGetValue(__originalMethod, out var count);
            if (count >= 10) return;
            traceCounts[__originalMethod] = count + 1;
            Plugin.Log.LogInfo($"[trace] {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}({string.Join(", ", __args.Select(Describe).ToArray())})" +
                               (count == 9 ? " (further calls not logged)" : ""));
        }

        private static void HookAll(Harmony harmony, string typeName, string method, string prefix)
        {
            var type = AccessTools.TypeByName(typeName);
            if (type == null)
            {
                Plugin.Log.LogWarning($"[diag] type {typeName} not found");
                return;
            }
            foreach (var m in type.GetMethods(All).Where(m => m.Name == method && m.DeclaringType == type && !m.IsGenericMethodDefinition))
                Patch(harmony, m, prefix);
        }

        private static void Patch(Harmony harmony, MethodBase m, string prefix)
        {
            try { harmony.Patch(m, prefix: new HarmonyMethod(typeof(Diagnostics), prefix)); }
            catch (Exception e) { Plugin.Log.LogWarning($"[diag] could not hook {m.DeclaringType?.Name}.{m.Name}: {e.Message}"); }
        }

        private static void IntroEnter(object __instance) =>
            Plugin.Log.LogInfo($"[diag] intro screen: {__instance.GetType().Name}");

        private static void IntroContinue(object __instance) =>
            Plugin.Log.LogInfo($"[diag] intro continue: {__instance.GetType().Name}");

        private static void Call(MethodBase __originalMethod, object[] __args) =>
            Plugin.Log.LogInfo($"[diag] {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}({string.Join(", ", __args.Select(Describe).ToArray())})");

        private static string Describe(object a)
        {
            if (a == null) return "null";
            if (a is string s) return "\"" + s + "\"";
            if (a is Array arr) return a.GetType().GetElementType()?.Name + "[" + arr.Length + "]";
            return a.ToString();
        }

        /// <summary>Called every frame from the plugin; logs a state summary when it changes (at most every 5 s).</summary>
        internal static void Heartbeat(Type photonNetwork)
        {
            if (Time.realtimeSinceStartup < nextHeartbeat) return;
            nextHeartbeat = Time.realtimeSinceStartup + 5f;
            try
            {
                string Get(Type t, string name) => t?.GetMethod(name, All, null, Type.EmptyTypes, null)?.Invoke(null, null)?.ToString() ?? "?";

                var gpm = AccessTools.TypeByName("GamePhaseManager");
                var gpmInstance = gpm?.GetMethod("get_Instance", All)?.Invoke(null, null);
                var phase = gpmInstance == null ? "none" : (gpm.GetMethod("GetCurrentGamePhase", All)?.Invoke(gpmInstance, null) as Type)?.Name ?? "null";

                var netCore = AccessTools.TypeByName("SVR.Network.NetworkCore");
                var netCoreInstance = netCore?.GetFields(All).FirstOrDefault(f => f.IsStatic && f.FieldType == netCore)?.GetValue(null);
                var connecting = netCoreInstance == null ? "?" : netCore.GetMethod("IsConnecting", All)?.Invoke(netCoreInstance, null)?.ToString();

                var status = AccessTools.TypeByName("SVR.Network.NetworkStatus");
                var scenes = string.Join(",", Enumerable.Range(0, SceneManager.sceneCount).Select(i => SceneManager.GetSceneAt(i).name).ToArray());

                var line = $"phase={phase} scenes=[{scenes}] loading={Get(AccessTools.TypeByName("NetworkLoadLevel"), "IsCurrentlyLoading")} " +
                           $"pun={Get(photonNetwork, "get_connectionStateDetailed")} inRoom={Get(photonNetwork, "get_inRoom")} " +
                           $"lobby={Get(photonNetwork, "get_insideLobby")} connecting={connecting} " +
                           $"initialDone={Get(status, "HasFinishedInitialConnection")} reason={Get(status, "GetNotConnectedReason")} " +
                           $"hadError={Get(status, "get_HadError")}";
                if (line != lastHeartbeat)
                {
                    lastHeartbeat = line;
                    Plugin.Log.LogInfo("[diag] " + line);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[diag] heartbeat failed: " + e.Message);
            }
        }
    }
}
