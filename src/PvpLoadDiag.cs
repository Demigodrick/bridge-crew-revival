using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sector.Spacecraft;
using StbcRevival.Missions;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// PvP test: why a launch stays on the loading screen. The game lifts it (GamePhase_Bridge.BatchUpdate →
    /// FadeToBridge) only once (1) you're seated, (2) your bridge has its ship and that ship's impulse state is in
    /// step (craft.engines.ImpulseManager.InSync; the host's at once, a crew member's once the host answers), and
    /// (3) every player has reached the "FadeToBridge" sync point, which they reach only once their own (1) and
    /// (2) hold. So one player stuck at (1) or (2) keeps everyone on the loading screen. While you're on the
    /// bridge and not yet faded in, this logs those conditions, and every player's loading state as their games
    /// report it, whenever they change (and every 10 s regardless), in a PvP room only.
    /// </summary>
    internal static class PvpLoadDiag
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        private static readonly FieldInfo CraftField = AccessTools.Field(typeof(Bridge), "<BAPNEMLKIOK>k__BackingField");
        private static readonly FieldInfo SeatedField = AccessTools.Field(typeof(GamePhase_Bridge), "GGDEEFAGPGJ");
        private static readonly FieldInfo SyncPointsField = AccessTools.Field(typeof(SyncPointManager), "KHOCLLMDFFF");  // _syncPoints
        private static readonly FieldInfo ReachedField = AccessTools.Field(typeof(SyncPointManager), "KPANLLHOCID");     // _reachedLocally
        private static readonly MethodInfo HashMethod = AccessTools.Method(typeof(SyncPointManager), "PNPLACHMLIP", new[] { typeof(string) });

        private static float nextLook, nextForced;
        private static string last;
        private static bool watching;

        internal static void Update()
        {
            if (Time.unscaledTime < nextLook) return;
            nextLook = Time.unscaledTime + 1f;
            string state;
            try { state = Describe(); }
            catch (Exception e) { state = "couldn't read: " + e.GetType().Name + " " + e.Message; }
            if (state == null)
            {
                if (watching) Plugin.Log.LogInfo("PvP: [load] the loading screen is lifted (or the bridge left)");
                watching = false;
                last = null;
                return;
            }
            watching = true;
            if (state == last && Time.unscaledTime < nextForced) return;
            last = state;
            nextForced = Time.unscaledTime + 10f;
            Plugin.Log.LogInfo("PvP: [load] " + state);
        }

        // Null when there's nothing to watch: not in a PvP room, not on the bridge, or already faded in.
        private static string Describe()
        {
            if (!PvpLobby.InPvpRoom) return null;
            var manager = GamePhaseManager.IEFBKOMBFGO;
            if (manager == null || manager.GetCurrentGamePhase() != typeof(GamePhase_Bridge)) return null;
            var phase = UnityEngine.Object.FindObjectOfType<GamePhase_Bridge>();
            var local = NovaPlayerManager.IEFBKOMBFGO?.AJPNCBOKCLC as NovaPlayerInfo;
            string localLoading = LoadingStateOf(local);
            if (localLoading == "Loaded") return null;

            var parts = new List<string>();
            parts.Add($"you: {localLoading ?? "no player info"}");
            parts.Add($"seated {(phase == null ? "no phase" : SeatedField?.GetValue(phase) is bool s ? (s ? "yes" : "no") : "?")}");

            // Your bridge's ship, and its impulse state.
            var bridge = Bridge.Instance;
            var craft = bridge == null || CraftField == null ? null : CraftField.GetValue(bridge);
            if (bridge == null) parts.Add("bridge none");
            else if (craft == null) parts.Add("bridge ship none");
            else
            {
                var engines = Traverse.Create(craft).Property("GCOAODPDBEI").GetValue();
                var impulse = engines == null ? null : Traverse.Create(engines).Property("POLCIGDAIKK").GetValue();
                var inSync = impulse == null ? null : Traverse.Create(impulse).Property("ONMDOGBOENM").GetValue();
                parts.Add($"bridge ship '{NameOf(craft)}' (ship {(craft is PlayerSpacecraft ps ? PvpPrototype.Letter(ps) : null) ?? "?"})");
                parts.Add($"impulse in step {(inSync is bool b ? (b ? "yes" : "no") : engines == null ? "no engines" : impulse == null ? "no impulse manager" : "?")}");
            }

            // The fade-in sync point: reached here, and (on the host) who's reported it.
            var sync = SyncPointManager.IEFBKOMBFGO;
            if (sync != null && HashMethod != null)
            {
                uint hash = (uint)HashMethod.Invoke(sync, new object[] { "FadeToBridge" });
                bool reached = ReachedField?.GetValue(sync) is IList r && r.Contains(hash);
                var point = SyncPointsField?.GetValue(sync) is IDictionary points && points.Contains(hash) ? points[hash] : null;
                string who = "";
                if (point != null)
                {
                    var synced = Traverse.Create(point).Field("PlayersSynced").GetValue<IList>();
                    bool done = Traverse.Create(point).Field("Synced").GetValue<bool>();
                    who = $", {(done ? "everyone through" : "waiting")}; reported: {(synced == null ? "?" : string.Join(" ", synced.Cast<object>().Select(p => "#" + PvpShips.Actor(p)).ToArray()))}";
                }
                parts.Add($"FadeToBridge reached here {(reached ? "yes" : "no")}{who}");
            }

            // The PvP test as this game sees it.
            var ships = UnityEngine.Object.FindObjectsOfType<PlayerSpacecraft>();
            parts.Add($"PvP test {(PvpPrototype.Active ? "on" : "off")} (room mission '{RevivalBriefing.RoomSelection() ?? "none"}'), you crew {PvpShips.Mine}, " +
                      $"your ship {(PvpPrototype.Bound == null ? "not bound" : "'" + NameOf(PvpPrototype.Bound) + "'")}, " +
                      $"player ships here {ships.Length}{(ships.Length > 0 ? ": " + string.Join(", ", ships.Select(NameOf).ToArray()) : "")}");

            // Every player's loading state, as their own games report it (the "LoadingState" player property).
            var players = PvpShips.AllPlayers ?? new object[0];
            parts.Add("players: " + string.Join(", ", players.Select(p =>
            {
                var props = PvpShips.Props(p);
                var loading = props != null && props.Contains("LoadingState") ? props["LoadingState"] : null;
                return $"#{PvpShips.Actor(p)} ship {PvpShips.Of(p)} {(loading == null ? "no state" : loading.ToString())}";
            }).ToArray()));
            return string.Join("; ", parts.ToArray());
        }

        private static string LoadingStateOf(NovaPlayerInfo info)
        {
            if (info == null) return null;
            var value = Traverse.Create(info).Property("PAKMGDMFGFM").GetValue(); // LoadingState
            return value?.ToString();
        }

        private static string NameOf(object o) => o is UnityEngine.Object u && u != null ? u.name : o?.ToString() ?? "null";
    }
}
