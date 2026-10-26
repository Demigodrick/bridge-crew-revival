using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Sector;
using Sector.Spacecraft;
using StbcRevival.Missions;
using UnityEngine;
using Team = Sector.DGGJOECIFAF;
using Category = Sector.HHHDDNLEDBN;

namespace StbcRevival
{
    /// <summary>
    /// Player vs player, phase 1 (the PvP test mission): two player ships in one sector, each player's game on
    /// its own. The game assumes one player ship; these patches keep a second one from taking over:
    ///   - Binding: every machine announces a newly spawned player ship (SectorSpawnManager.CJDNEDMKCJJ sends
    ///     "player ship spawned", INPOODEDFLG) and some fifteen systems (bridge, consoles, camera, nav map...)
    ///     take the last one announced as theirs. The second ship ("PvP B") is announced as an ordinary ship
    ///     instead, unless you're on it ([Debug] PvpTestShip = B), when it's announced after the first.
    ///   - The other ship's own crew logic (USSAegisCrew, which acts through the bridge's consoles, so on your
    ///     ship) is switched off, as are its replies to ship-wide events (subsystem messages, mission warps,
    ///     probe launches) and its throttle's hold on the helm's slider.
    ///   - Teams: hostility is "not Federation", so the second ship is Klingon; players on it see the teams
    ///     swapped (their own ship Federation, the first one Klingon). The host's simulation sees the real
    ///     teams (the host is on the first ship), and the teams sent to other players are always the real ones.
    ///   - The targeting panel's category: another player ship is an enemy, yours is yours.
    /// Only while the PvP test mission runs.
    /// </summary>
    internal static class PvpPrototype
    {
        internal const string ShipBName = "PvP B";

        /// <summary>The player ship this game is on, or null outside the PvP test.</summary>
        internal static PlayerSpacecraft Bound { get; private set; }

        private static TargetInfoComponent boundInfo;
        private static bool serializing;
        private static int activeFrame = -1;
        private static bool active;
        private static readonly Dictionary<TargetableComponent, PlayerSpacecraft> shipOf = new Dictionary<TargetableComponent, PlayerSpacecraft>();
        private static readonly AccessTools.FieldRef<TargetInfoComponent, Team> TeamField =
            AccessTools.FieldRefAccess<TargetInfoComponent, Team>("<BEJOMHEMMLD>k__BackingField"); // behind get_TeamID

        // The ship's replies to ship-wide events: subsystem damaged / destroyed / repaired / at minimum or
        // maximum power (bridge messages), probe launch, warp-effect end, mission warp.
        private static readonly string[] ShipEventHandlers =
        {
            "HCCIGJCMAKL", "BOAPPLHKHFP", "OPMBGEHIDBD", "NEBNOAFFOJP", "NDBKDBOMAJB", "NEPBADGDJBJ", "AEIHFBMCKMD", "GCLCHPJHNNK",
        };

        /// <summary>The PvP test mission is running (or about to): checked once a frame.</summary>
        internal static bool Active
        {
            get
            {
                if (activeFrame != Time.frameCount)
                {
                    activeFrame = Time.frameCount;
                    try { active = RevivalMissions.Selected == typeof(PvpTestSequence); }
                    catch { active = false; }
                }
                return active;
            }
        }

        private static bool OnShipB => string.Equals(Plugin.PvpTestShip.Value?.Trim(), "B", StringComparison.OrdinalIgnoreCase);

        internal static Team RawTeam(TargetInfoComponent info) => TeamField(info);

        internal static void Patch(Harmony harmony)
        {
            var announce = AccessTools.Method(typeof(SectorSpawnManager), "CJDNEDMKCJJ", new[] { typeof(SectorBody) });
            if (announce == null)
            {
                Plugin.Log.LogWarning("PvP: SectorSpawnManager's ship announcement wasn't found; PvP test off");
                return;
            }
            harmony.Patch(announce, prefix: new HarmonyMethod(typeof(PvpPrototype), nameof(Announce_Prefix)));
            foreach (var name in ShipEventHandlers)
            {
                var handler = AccessTools.Method(typeof(PlayerSpacecraft), name);
                if (handler == null) Plugin.Log.LogWarning($"PvP: PlayerSpacecraft.{name} not found");
                else harmony.Patch(handler, prefix: new HarmonyMethod(typeof(PvpPrototype), nameof(OwnShipOnly_Prefix)));
            }
            var throttle = AccessTools.Method(typeof(PlayerSpacecraft), "Multicast_SetThrottleAndPower");
            if (throttle != null)
                harmony.Patch(throttle, prefix: new HarmonyMethod(typeof(PvpPrototype), nameof(Throttle_Prefix)),
                    postfix: new HarmonyMethod(typeof(PvpPrototype), nameof(Throttle_Postfix)));
            var team = AccessTools.PropertyGetter(typeof(TargetInfoComponent), "DGGJOECIFAF");
            var serialize = AccessTools.Method(typeof(TargetInfoComponent), "OnPhotonSerializeView");
            if (team != null && serialize != null)
            {
                harmony.Patch(team, postfix: new HarmonyMethod(typeof(PvpPrototype), nameof(Team_Postfix)));
                harmony.Patch(serialize, prefix: new HarmonyMethod(typeof(PvpPrototype), nameof(Serialize_Prefix)),
                    finalizer: new HarmonyMethod(typeof(PvpPrototype), nameof(Serialize_Finalizer)));
            }
            var category = AccessTools.Method(typeof(TargetableComponent), "GetCategory");
            if (category != null) harmony.Patch(category, postfix: new HarmonyMethod(typeof(PvpPrototype), nameof(Category_Postfix)));
        }

        // A player ship spawned: the first ship always binds (as the game does); the second binds only for
        // players on it, and for everyone else is announced as an ordinary ship (next frame's end, as the game's).
        private static bool Announce_Prefix(SectorSpawnManager __instance, SectorBody __0)
        {
            if (!(__0 is PlayerSpacecraft ship) || !Active) return true;
            string name = ship.ALPAOOKFIEE?.BaseName ?? "";
            bool shipB = name.StartsWith(ShipBName, StringComparison.Ordinal);
            if (!shipB || OnShipB)
            {
                Bind(ship);
                Plugin.Log.LogInfo($"PvP: on {(shipB ? ShipBName : "the first player ship")} ('{name}')");
                return true;
            }
            Plugin.Log.LogInfo($"PvP: '{name}' is the other player's ship");
            Detach(ship);
            __instance.StartCoroutine(AnnounceAsBody(ship));
            return false;
        }

        private static IEnumerator AnnounceAsBody(SectorBody body)
        {
            yield return new WaitForEndOfFrame();
            if (body != null) CIFDOFLFGEA.Send(new JJKIHDENJCK(body));
        }

        private static void Bind(PlayerSpacecraft ship)
        {
            if (Bound != null && Bound != ship) Detach(Bound);
            Bound = ship;
            boundInfo = ship.GetTargetInfo() as TargetInfoComponent;
            shipOf.Clear();
            var crew = ship.GetComponent<AI.USSAegisCrew>();
            if (crew != null) crew.enabled = true;
        }

        // Its crew logic off (OnDisable drops its event listeners and its updates).
        private static void Detach(PlayerSpacecraft ship)
        {
            var crew = ship.GetComponent<AI.USSAegisCrew>();
            if (crew != null) crew.enabled = false;
        }

        private static bool IsOther(PlayerSpacecraft ship) => Active && Bound != null && ship != Bound;

        private static bool OwnShipOnly_Prefix(PlayerSpacecraft __instance) => !IsOther(__instance);

        // Multicast_SetThrottleAndPower also moves the helm's throttle slider: not for the other ship.
        private static void Throttle_Prefix(PlayerSpacecraft __instance, out float __state)
        {
            __state = -1f;
            if (IsOther(__instance) && Bridge.AGAFPEFMEGB != null) __state = Bridge.AGAFPEFMEGB.KJLEFDENMNI.OJKPBPCBPMA;
        }

        private static void Throttle_Postfix(float __state)
        {
            if (__state >= 0f && Bridge.AGAFPEFMEGB != null) Bridge.AGAFPEFMEGB.KJLEFDENMNI.OJKPBPCBPMA = __state;
        }

        // On the second ship, its team and the Federation swap places (never while being sent).
        private static void Team_Postfix(ref Team __result)
        {
            if (serializing || boundInfo == null || !Active) return;
            var own = TeamField(boundInfo);
            if (own == Team.Federation) return;
            if (__result == own) __result = Team.Federation;
            else if (__result == Team.Federation) __result = own;
        }

        private static void Serialize_Prefix() => serializing = true;

        private static Exception Serialize_Finalizer(Exception __exception)
        {
            serializing = false;
            return __exception;
        }

        private static void Category_Postfix(TargetableComponent __instance, ref Category __result)
        {
            if (!Active || Bound == null) return;
            if (!shipOf.TryGetValue(__instance, out var ship))
                shipOf[__instance] = ship = __instance.GetComponentInParent<PlayerSpacecraft>();
            if (ship != null) __result = ship == Bound ? Category.Self : Category.Enemy;
        }
    }
}
