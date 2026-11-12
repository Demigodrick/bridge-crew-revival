using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// PvP: each ship's sounds on its own bridge. The game's networked sounds (WwiseSoundUtil.PostEvent_Networked:
    /// played here, then PostEvent_Networked_RPC to everyone else) carry no ship, and the host makes many for
    /// either ship. A sound whose ship is known goes to that ship's crew only (played here if it's yours):
    ///   - a console's sound (the overload with a GameObject): its console set's ship (PvpConsoles.SetOf), e.g.
    ///     the Enterprise-D tactical "engage weapons" (ConsoleTactical.OnFirePhasers / FireTorpedoes, which also
    ///     run by their own ship's rules now: the sound's gate is that ship's Enterprise-D flag);
    ///   - impulse's pre-whine (ImpulseManager.BeginImpulse, on the host): the ship going to impulse, by its own
    ///     ship's rules (none on the original Enterprise);
    ///   - the launch music (ConsoleBriefingRoom.LaunchGame, the host): each ship's own theme to its own crew
    ///     (the game played ship A's to everyone, and only when the host was ship A's captain).
    /// Sounds with no ship (the mission's, explosions) are left as they are.
    /// </summary>
    internal static class PvpSounds
    {
        [ThreadStatic] private static string soundShip;
        [ThreadStatic] private static bool launching;
        private static float lastLaunchMusic = -100f;
        private static MethodInfo sendTo, playHere, playHereAt;
        private static readonly HashSet<string> told = new HashSet<string>();
        private static readonly AccessTools.FieldRef<Sector.Spacecraft.POLCIGDAIKK, Sector.Spacecraft.BaseSpacecraft> ImpulseShip =
            AccessTools.FieldRefAccess<Sector.Spacecraft.POLCIGDAIKK, Sector.Spacecraft.BaseSpacecraft>("JCIEFGHGMMP");

        internal static void Patch(Harmony harmony)
        {
            var wwise = typeof(WwiseSoundUtil);
            var player = AccessTools.TypeByName("CBBCCGMALMP"); // PhotonPlayer
            sendTo = AccessTools.Method(AccessTools.TypeByName("MCGGBALEIDD"), "SSRPC",                     // Extensions.SSRPC(view, target, method, player, args)
                new[] { AccessTools.TypeByName("PhotonView"), typeof(object), typeof(string), player, typeof(object[]) });
            playHere = AccessTools.Method(wwise, "PostEvent", new[] { typeof(string) });
            playHereAt = AccessTools.Method(wwise, "PostEvent", new[] { typeof(string), typeof(GameObject) });
            var plain = AccessTools.Method(wwise, "PostEvent_Networked", new[] { typeof(string) });
            var placed = AccessTools.Method(wwise, "PostEvent_Networked", new[] { typeof(string), typeof(GameObject) });
            if (sendTo == null || playHere == null || playHereAt == null || plain == null || placed == null)
            {
                Plugin.Log.LogWarning("PvP: the networked sound parts weren't all found; both ships hear each other's bridge sounds");
                return;
            }
            harmony.Patch(plain, prefix: new HarmonyMethod(typeof(PvpSounds), nameof(Plain_Prefix)));
            harmony.Patch(placed, prefix: new HarmonyMethod(typeof(PvpSounds), nameof(Placed_Prefix)));
            var emitterPlay = AccessTools.Method(wwise, "PlayEmitterNetworked");
            var emitterStop = AccessTools.Method(wwise, "StopEmitterNetworked");
            var emitterRtpc = AccessTools.Method(wwise, "SetEmitterRTPCNetworked");
            if (emitterPlay != null) harmony.Patch(emitterPlay, prefix: new HarmonyMethod(typeof(PvpSounds), nameof(EmitterPlay_Prefix)));
            if (emitterStop != null) harmony.Patch(emitterStop, prefix: new HarmonyMethod(typeof(PvpSounds), nameof(EmitterStop_Prefix)));
            if (emitterRtpc != null) harmony.Patch(emitterRtpc, prefix: new HarmonyMethod(typeof(PvpSounds), nameof(EmitterRtpc_Prefix)));
            if (emitterPlay == null || emitterStop == null || emitterRtpc == null || NetSound == null)
                Plugin.Log.LogWarning("PvP: the sound emitters' networked parts weren't all found; console button sounds reach both ships");
            var launch = AccessTools.Method(typeof(ConsoleBriefingRoom), "GLFEOIFJEHE"); // LaunchGame
            if (launch != null) harmony.Patch(launch, prefix: new HarmonyMethod(typeof(PvpSounds), nameof(Launch_Prefix)),
                postfix: new HarmonyMethod(typeof(PvpSounds), nameof(Launch_Postfix)), finalizer: new HarmonyMethod(typeof(PvpSounds), nameof(Launch_Finalizer)));
            var impulse = AccessTools.Method(typeof(Sector.Spacecraft.POLCIGDAIKK), "BeginImpulse");
            if (impulse != null) harmony.Patch(impulse, prefix: new HarmonyMethod(typeof(PvpSounds), nameof(Impulse_Prefix)),
                finalizer: new HarmonyMethod(typeof(PvpSounds), nameof(Ship_Finalizer)));
            foreach (var name in new[] { "OnFirePhasers", "FireTorpedoes" })
            {
                var fire = AccessTools.Method(typeof(ConsoleTactical), name);
                if (fire != null) harmony.Patch(fire, prefix: new HarmonyMethod(typeof(PvpSounds), nameof(Fire_Prefix)),
                    finalizer: new HarmonyMethod(typeof(PvpSounds), nameof(Fire_Finalizer)));
            }
            Plugin.Log.LogInfo("PvP: each ship's bridge sounds to its own crew");
        }

        private static bool OnBridge => PvpLobby.InPvpRoom && PvpPrototype.Active && PvpPrototype.Bound != null;

        // The sound's ship: one set here (impulse, weapons, the launch), else the ship whose code is running (the
        // host running the other ship: PvpScope; a console updating or its state arriving, a ship's part, an AI crew's
        // event: PvpRouting's context), else the console set the sound's object is in. None: everyone's (the mission's).
        private static string ShipOf(GameObject at) =>
            soundShip ?? PvpScope.Applied ?? PvpRouting.Context ?? (at != null ? PvpConsoles.SetOf(at.transform) : null);

        private static bool Plain_Prefix(string __0)
        {
            if (launching && PvpLobby.InPvpRoom) return false; // the launch music: each ship's own, after
            if (!OnBridge || __0 == null) return true;
            string ship = ShipOf(null);
            if (ship == null) return true;
            Send(ship, __0, null);
            return false;
        }

        private static bool Placed_Prefix(string __0, GameObject __1)
        {
            if (!OnBridge || __0 == null) return true;
            string ship = ShipOf(__1);
            if (ship == null) return true;
            Send(ship, __0, __1);
            return false;
        }

        // ---- Sound emitters (console buttons, throttle, transporter: PostEvent_RPC / SetRTPC_RPC to everyone) ----

        private static readonly AccessTools.FieldRef<SoundEmitter, int> StartEvent = AccessTools.FieldRefAccess<SoundEmitter, int>("StartEventID");
        private static readonly AccessTools.FieldRef<SoundEmitter, int> StopEvent = AccessTools.FieldRefAccess<SoundEmitter, int>("StopEventID");
        private static readonly AccessTools.FieldRef<SoundEmitter, GameObject> EmitterOverride = AccessTools.FieldRefAccess<SoundEmitter, GameObject>("GameObjectOverride");
        private static readonly MethodInfo NetSound = AccessTools.PropertyGetter(typeof(SoundEmitter), "KIFAEIGDJDI") ?? AccessTools.Method(typeof(SoundEmitter), "get_KIFAEIGDJDI") ?? AccessTools.Method(typeof(SoundEmitter), "get_NetSoundID"); // NetSoundID

        private static bool EmitterPlay_Prefix(SoundEmitter __0) => Emitter(__0, true);
        private static bool EmitterStop_Prefix(SoundEmitter __0) => Emitter(__0, false);

        private static bool Emitter(SoundEmitter emitter, bool play)
        {
            if (!OnBridge || emitter == null || NetSound == null) return true;
            string ship = ShipOf(emitter.gameObject);
            if (ship == null) return true;
            if (ship == (PvpShips.Mine ?? "A")) { if (play) emitter.Play(); else emitter.Stop(); }
            var at = EmitterOverride(emitter) != null ? EmitterOverride(emitter).transform.position : emitter.transform.position;
            ToShip(ship, "PostEvent_RPC", new object[] { play ? StartEvent(emitter) : StopEvent(emitter), at, NetSound.Invoke(emitter, null) });
            return false;
        }

        private static bool EmitterRtpc_Prefix(string __0, float __1, SoundEmitter __2)
        {
            if (!OnBridge || string.IsNullOrEmpty(__0) || __2 == null || NetSound == null) return true;
            string ship = ShipOf(__2.gameObject);
            if (ship == null) return true;
            if (ship == (PvpShips.Mine ?? "A")) __2.SetRTPC(__0, __1);
            ToShip(ship, "SetRTPC_RPC", new object[] { __0, __1, NetSound.Invoke(__2, null) });
            return false;
        }

        // An RPC on the sound utility's view to that ship's other players.
        private static void ToShip(string ship, string rpc, object[] args)
        {
            try
            {
                var util = WwiseSoundUtil.IEFBKOMBFGO; // Instance
                var view = util == null ? null : util.GetComponent("PhotonView");
                if (view == null) return;
                int me = PvpShips.Actor(PvpShips.Me);
                foreach (var player in PvpShips.AllPlayers)
                    if (PvpShips.Actor(player) != me && PvpShips.Of(player) == ship)
                        sendTo.Invoke(null, new object[] { view, util, rpc, player, args });
            }
            catch (Exception e)
            {
                if (told.Add("error" + rpc)) Plugin.Log.LogWarning($"PvP: {rpc} for ship {ship}: " + (e.InnerException ?? e).Message);
            }
        }

        // To that ship's crew: played here if it's yours (at its object, if it has one), sent to the rest of them.
        private static void Send(string ship, string evt, GameObject at)
        {
            try
            {
                if (ship == (PvpShips.Mine ?? "A"))
                {
                    if (at != null) playHereAt.Invoke(null, new object[] { evt, at });
                    else playHere.Invoke(null, new object[] { evt });
                }
                var util = WwiseSoundUtil.IEFBKOMBFGO; // Instance
                var view = util == null ? null : util.GetComponent("PhotonView");
                if (view == null) return;
                int me = PvpShips.Actor(PvpShips.Me);
                foreach (var player in PvpShips.AllPlayers)
                    if (PvpShips.Actor(player) != me && PvpShips.Of(player) == ship)
                        sendTo.Invoke(null, new object[] { view, util, "PostEvent_Networked_RPC", player, new object[] { evt, at != null, at != null ? at.transform.position : Vector3.zero } });
                if (told.Add(ship + evt)) Plugin.Log.LogInfo($"PvP: sound '{evt}' to ship {ship}'s crew only");
            }
            catch (Exception e)
            {
                if (told.Add("error" + evt)) Plugin.Log.LogWarning($"PvP: sound '{evt}' for ship {ship}: " + (e.InnerException ?? e).Message);
            }
        }

        // ---- The launch music ------------------------------------------------------------------------

        private static void Launch_Prefix() => launching = PvpLobby.InPvpRoom;

        private static void Launch_Postfix(ConsoleBriefingRoom __instance)
        {
            if (!launching || Time.realtimeSinceStartup - lastLaunchMusic < 30f) return;
            lastLaunchMusic = Time.realtimeSinceStartup;
            launching = false;
            var t = Traverse.Create(__instance);
            foreach (var ship in new[] { "A", "B" })
            {
                int pick = PvpShipChoice.Of(ship);
                string field = pick == 2 ? "_beginGameMusicTNG" : pick == 1 ? "_beginGameMusicTOS" : "_beginGameMusic";
                var evt = t.Field(field).GetValue() as string;
                if (string.IsNullOrEmpty(evt)) continue;
                if (ship == (PvpShips.Mine ?? "A")) playHere.Invoke(null, new object[] { evt });
                var util = WwiseSoundUtil.IEFBKOMBFGO;
                var view = util == null ? null : util.GetComponent("PhotonView");
                int me = PvpShips.Actor(PvpShips.Me);
                if (view != null)
                    foreach (var player in PvpShips.AllPlayers)
                        if (PvpShips.Actor(player) != me && PvpShips.Of(player) == ship)
                            sendTo.Invoke(null, new object[] { view, util, "PostEvent_Networked_RPC", player, new object[] { evt, false, Vector3.zero } });
                Plugin.Log.LogInfo($"PvP: ship {ship}'s crew hear {PvpShipChoice.Name(pick)}'s launch music");
            }
        }

        private static Exception Launch_Finalizer(Exception __exception)
        {
            launching = false;
            return __exception;
        }

        // ---- Impulse and weapons: their own ship's sound, by its own ship's rules --------------------------

        private struct ShipState { public string Sound; public int Flags; public Transform Panel; public bool PanelSet; }

        private static void Impulse_Prefix(Sector.Spacecraft.POLCIGDAIKK __instance, out ShipState __state)
        {
            __state = new ShipState { Sound = soundShip, Flags = -1 };
            if (!OnBridge) return;
            var ship = ImpulseShip(__instance) as Sector.Spacecraft.PlayerSpacecraft;
            if (ship == null) return;
            string letter = PvpPrototype.Letter(ship);
            soundShip = letter;
            __state.Flags = PvpShipChoice.AroundShip(letter);
        }

        private static Exception Ship_Finalizer(ShipState __state, Exception __exception)
        {
            soundShip = __state.Sound;
            PvpShipChoice.Restore(__state.Flags);
            return __exception;
        }

        private static readonly AccessTools.FieldRef<ConsoleTactical, Transform> TngPanel = AccessTools.FieldRefAccess<ConsoleTactical, Transform>("_tngPanelSoundTransform");

        private static void Fire_Prefix(ConsoleTactical __instance, out ShipState __state)
        {
            __state = new ShipState { Sound = soundShip, Flags = -1 };
            if (!OnBridge || __instance == null) return;
            string letter = PvpConsoles.IsCopy(__instance) ? "B" : "A";
            __state.Flags = PvpShipChoice.AroundShip(letter);
            // An Aegis or original Enterprise console has no Enterprise-D sound panel: its own transform stands in.
            if (TngPanel(__instance) == null) { TngPanel(__instance) = __instance.transform; __state.PanelSet = true; }
        }

        private static Exception Fire_Finalizer(ConsoleTactical __instance, ShipState __state, Exception __exception)
        {
            PvpShipChoice.Restore(__state.Flags);
            if (__state.PanelSet && __instance != null) TngPanel(__instance) = null;
            soundShip = __state.Sound;
            return __exception;
        }
    }
}
