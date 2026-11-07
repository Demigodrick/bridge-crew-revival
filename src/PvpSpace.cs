using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sector.Spacecraft;
using UnityEngine;
using ImpulseManager = Sector.Spacecraft.POLCIGDAIKK;
using ImpulsePhase = Sector.Spacecraft.POLCIGDAIKK.DPANMALGKFB;
using PooledReader = SVR.Network.PLPKPDFOLDL;

namespace StbcRevival
{
    /// <summary>
    /// PvP test: each crew's own space, and impulse for both ships. The game keeps one player ship's surroundings
    /// precise by centring every game's space on it (SimulationSpace's floating origin), and only the host
    /// decides where that centre is: every game follows it, every ship's position is sent as an offset from it,
    /// tagged with its shift number, and a game drops what was sent from another centre. Impulse travel is the
    /// same: the host flies its own player ship along the course, sends where it is, and every game at impulse
    /// copies that onto its own player ship; the end-of-impulse handshake waits for every player, then
    /// re-centres everyone on the host's ship. With two player ships, the crew of the ship the host isn't on
    /// was put on the host's ship's course, and could never travel apart from it. Here:
    ///   - Separate space: the games of the ship the host isn't on keep their own centre, on their own ship
    ///     (they shift it themselves and ignore the host's), and their shift numbers carry their player number.
    ///     Every game that keeps a centre publishes it ("ORG" player property, by shift number), and a position
    ///     sent from another centre (a ship's state, NetworkSectorBody; a body's place, SystemTransform) is
    ///     moved by the difference between the centres on arrival instead of being dropped.
    ///   - Impulse: the host also flies the other ship along its course (its engines' UpdateImpulse), and sends
    ///     that ship's crew where it is (event 155), which their games follow as they'd follow the host's ship.
    ///     The end-of-impulse handshake is per ship (the other ship's sync points are named for it, and the host
    ///     counts only that ship's players for them, its own only its own); that crew re-centres on its own ship,
    ///     and the host ends the other ship's impulse when its crew is done (at once if it has none).
    /// Only while the PvP test runs with both ships bound; [Debug] PvpSeparateSpace turns it off.
    /// </summary>
    internal static class PvpSpace
    {
        private const int ActorShift = 20, RawMask = (1 << ActorShift) - 1;
        private const string OriginKey = "ORG";
        private const byte TrackEvent = 155;
        private const float TrackEvery = 0.05f, PublishEvery = 0.25f;
        private static readonly string[] ImpulsePoints = { "Impulse Cooldown", "Impulse Resync" };

        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        // SimulationSpace's own (private) parts.
        private static FieldInfo spaceInstance, playerShip, playerShipTransform, masterStates, masterImpulsePosition;
        private static MethodInfo floatingOrigin, impulseOverride, setCooldownSynced, getCooldownSynced, checkShift, resync, recordState;
        private static double unitToMeter = 1, unitToSkybox = 1;

        // SyncPointManager's.
        private static MethodInfo hash;
        private static FieldInfo syncPoints;
        private static readonly Dictionary<uint, string> pointShip = new Dictionary<uint, string>(); // sync point hash → ship ("" = the host's)
        private static uint reachedHash; // the sync point a player just reached (for AllPlayersSynced)
        private static bool ownResync;  // the re-centre we call ourselves (not the host's for its ship)
        private static bool resyncedThisImpulse;

        internal static void Patch(Harmony harmony)
        {
            var space = typeof(SimulationSpace);
            spaceInstance = AccessTools.Field(space, "JKOEGLOEGKD");
            playerShip = AccessTools.Field(space, "PFHOOFGPFMM");
            playerShipTransform = AccessTools.Field(space, "GOMEHHDMBGH");
            masterStates = AccessTools.Field(space, "ENKPDCIALPO");
            masterImpulsePosition = AccessTools.Field(space, "GCBHEHCMONM");
            floatingOrigin = AccessTools.Method(space, "get_FloatingOrigin");
            shiftCounter = AccessTools.Field(space, "MKHNNJBFKPH");   // _shiftId
            pendingShiftId = AccessTools.Field(space, "CPEGBLPAMIF"); // _pendingShiftId
            impulseOverride = AccessTools.Method(space, "get_ImpulseOverride");
            setCooldownSynced = AccessTools.Method(space, "KPKBCKPCLDG"); // set_ImpulseCooldownSynced
            getCooldownSynced = AccessTools.Method(space, "get_ImpulseCooldownSynced");
            checkShift = AccessTools.Method(space, "EEAIEGFANPN"); // CheckOriginShift
            resync = AccessTools.Method(space, "Multicast_ResyncFloatingOrigin");
            recordState = masterStates == null ? null : AccessTools.Method(masterStates.FieldType, "RecordState");
            unitToMeter = AccessTools.Field(space, "UnitToMeterScale")?.GetValue(null) is double m ? m : 1;
            unitToSkybox = AccessTools.Field(space, "UnitToSkyboxScale")?.GetValue(null) is double s ? s : 1;
            hash = AccessTools.Method(typeof(SyncPointManager), "PNPLACHMLIP", new[] { typeof(string) });
            syncPoints = AccessTools.Field(typeof(SyncPointManager), "KHOCLLMDFFF");

            var hooks = new[]
            {
                new Hook(AccessTools.Method(space, "NetworkDeserialize"), nameof(SpaceReceive_Prefix), null),
                new Hook(AccessTools.Method(space, "FixedUpdate"), null, nameof(SpaceFixedUpdate_Postfix)),
                new Hook(resync, nameof(Resync_Prefix), null),
                new Hook(AccessTools.Method(typeof(SystemTransform), "Deserialize"), nameof(PlaceReceive_Prefix), nameof(PlaceReceive_Postfix)),
                new Hook(AccessTools.Method(typeof(SyncPointManager), "SyncWithOtherPlayers"), nameof(Sync_Prefix), nameof(Sync_Postfix)),
                new Hook(AccessTools.Method(typeof(SyncPointManager), "ClearSyncPoint"), nameof(ClearSync_Prefix), null),
                new Hook(AccessTools.Method(typeof(SyncPointManager), "PlayerReachedSyncPointRPC"), nameof(Reached_Prefix), nameof(Reached_Postfix)),
                new Hook(AccessTools.Method(typeof(SyncPointManager), "COHPFJGKNCF"), nameof(AllSynced_Prefix), null), // AllPlayersSynced
            };
            if (hooks.Any(h => h.Method == null) || new object[] { shiftCounter, pendingShiftId, spaceInstance, playerShip, playerShipTransform, masterStates, masterImpulsePosition,
                    floatingOrigin, impulseOverride, setCooldownSynced, getCooldownSynced, checkShift, recordState, hash, syncPoints }.Any(x => x == null))
            {
                Plugin.Log.LogWarning("PvP: the game's space and impulse parts weren't all found; both crews share the host's space, and only the host's ship can use impulse");
                return;
            }
            foreach (var hook in hooks)
                harmony.Patch(hook.Method, prefix: hook.Prefix == null ? null : new HarmonyMethod(typeof(PvpSpace), hook.Prefix),
                    postfix: hook.Postfix == null ? null : new HarmonyMethod(typeof(PvpSpace), hook.Postfix));
            // A ship's state: NetworkSectorBody.ApplyState (PMLPAFENGBE), and the kinds of body that have their own.
            int states = 0;
            foreach (var type in typeof(NetworkSectorBody).Assembly.GetTypes().Where(t => typeof(NetworkSectorBody).IsAssignableFrom(t)))
            {
                var apply = type.GetMethod("PMLPAFENGBE", All | BindingFlags.DeclaredOnly);
                if (apply == null || apply.IsAbstract) continue;
                harmony.Patch(apply, prefix: new HarmonyMethod(typeof(PvpSpace), nameof(State_Prefix)));
                states++;
            }
            var impulseUpdate = AccessTools.Method(typeof(ImpulseManager), "Update");
            if (impulseUpdate != null)
                harmony.Patch(impulseUpdate, prefix: new HarmonyMethod(typeof(PvpSpace), nameof(Impulse_Prefix)),
                    finalizer: new HarmonyMethod(typeof(PvpSpace), nameof(Impulse_Finalizer)));
            foreach (var name in ImpulsePoints)
                foreach (var ship in new[] { "", "A", "B" })
                    pointShip[Hash(ship == "" ? name : name + " " + ship)] = ship;
            NDIOGIGMPBH.OnEventCall += OnEvent;
            Plugin.Log.LogInfo($"PvP: separate space for each crew ready ({states} kinds of ship state)");
        }

        private sealed class Hook
        {
            public readonly MethodBase Method;
            public readonly string Prefix, Postfix;
            public Hook(MethodBase method, string prefix, string postfix) { Method = method; Prefix = prefix; Postfix = postfix; }
        }

        // ---- Who's where --------------------------------------------------------------------------

        private static bool Enabled => Plugin.PvpSeparateSpace.Value && PvpPrototype.Active && PvpPrototype.Bound != null;

        /// <summary>The ship the host is on ("A", "B"), or null.</summary>
        private static string HostShip
        {
            get
            {
                var master = typeof(NDIOGIGMPBH).GetMethod("get_masterClient", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
                return master == null ? null : PvpShips.Of(master);
            }
        }

        private static bool IsMaster => typeof(NDIOGIGMPBH).GetMethod("get_isMasterClient", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) is true;

        private static int separateFrame = -1;
        private static bool separate;

        /// <summary>This game keeps its own space: it's on the ship the host isn't on (once a frame).</summary>
        private static bool Separate
        {
            get
            {
                if (separateFrame != Time.frameCount)
                {
                    separateFrame = Time.frameCount;
                    try { separate = Enabled && !IsMaster && HostShip is string host && PvpShips.Mine != host; }
                    catch { separate = false; }
                }
                return separate;
            }
        }

        private static int MyActor => PvpShips.Actor(PvpShips.Me);

        private static object Space => spaceInstance.GetValue(null);
        private static Vector3d Origin => (Vector3d)floatingOrigin.Invoke(null, null);
        private static bool ImpulseOverride => impulseOverride.Invoke(null, null) is true;

        // ---- Shift numbers and centres ---------------------------------------------------------------

        // A game with its own space numbers its shifts with its player number above the game's own count: its
        // shift counter (SimulationSpace._shiftId) is set so once it's separate, and the game counts on from it.
        private static FieldInfo shiftCounter, pendingShiftId;

        private static void NumberOwnShifts(SimulationSpace space)
        {
            int own = (int)shiftCounter.GetValue(space);
            if (own >> ActorShift != MyActor) shiftCounter.SetValue(space, (MyActor << ActorShift) | (own & RawMask));
        }

        // Centres published by every game that keeps one, by shift number: the host's (plain numbers) and those
        // of games with their own space (their player number in them).
        private static readonly Dictionary<int, Vector3d> origins = new Dictionary<int, Vector3d>();
        private static float nextPublish, nextRead;
        private static string published;

        private static bool TryOrigin(int shiftId, out Vector3d origin) => origins.TryGetValue(shiftId, out origin);

        // How far a position sent from that centre must move to be in ours, in our space's units; false if that
        // centre isn't known (yet).
        private static bool TryMove(int shiftId, double scale, out Vector3 move)
        {
            move = Vector3.zero;
            if (!TryOrigin(shiftId, out var theirs)) return false;
            var delta = theirs - Origin;
            move = new Vector3((float)(delta.x / scale), (float)(delta.y / scale), (float)(delta.z / scale));
            return true;
        }

        internal static void Update()
        {
            if (!Enabled)
            {
                if (origins.Count > 0) origins.Clear();
                return;
            }
            float now = Time.realtimeSinceStartup;
            if ((Separate || IsMaster) && now >= nextPublish)
            {
                nextPublish = now + PublishEvery;
                Publish();
            }
            if (now >= nextRead)
            {
                nextRead = now + PublishEvery;
                ReadOrigins();
            }
            if (IsMaster && now >= nextTrack)
            {
                nextTrack = now + TrackEvery;
                SendTracks();
            }
        }

        private static readonly List<string> recent = new List<string>();

        private static void Publish()
        {
            int id = SimulationSpace.NNOJBJMFJPJ;
            var origin = Origin;
            string entry = string.Format(CultureInfo.InvariantCulture, "{0},{1:R},{2:R},{3:R}", id, origin.x, origin.y, origin.z);
            if (recent.Count > 0 && recent[recent.Count - 1] == entry) return;
            recent.Add(entry);
            if (recent.Count > 4) recent.RemoveAt(0); // a few back: packets sent just before a shift still arrive after it
            var value = string.Join(";", recent.ToArray());
            if (value == published) return;
            published = value;
            PhotonProps.Set(PvpShips.Me, OriginKey, value);
            origins[id] = origin;
        }

        private static void ReadOrigins()
        {
            foreach (var player in PvpShips.AllPlayers)
            {
                if (!(PvpShips.Props(player)?[OriginKey] is string value)) continue;
                foreach (var entry in value.Split(';'))
                {
                    var parts = entry.Split(',');
                    if (parts.Length == 4 && int.TryParse(parts[0], out int id)
                        && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                        && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
                        && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
                        origins[id] = new Vector3d(x, y, z);
                }
            }
        }

        // ---- Positions from another centre -------------------------------------------------------------

        // A ship's state (an offset from the sender's centre, in our space's units unless the sender had the ship
        // in its skybox, which isn't moved): into our centre's terms.
        // (NetworkSectorBody's State is private: its fields through the boxed argument.)
        private static readonly Type StateType = AccessTools.Inner(typeof(NetworkSectorBody), "JLDAPIFNCII");
        private static readonly FieldInfo StateShift = StateType == null ? null : AccessTools.Field(StateType, "ShiftId");
        private static readonly FieldInfo StatePosition = StateType == null ? null : AccessTools.Field(StateType, "Position");
        private static readonly FieldInfo StateFlags = StateType == null ? null : AccessTools.Field(StateType, "Flags");

        private static void State_Prefix(object[] __args)
        {
            if (!Enabled || StateShift == null || __args.Length == 0 || __args[0] == null) return;
            var state = __args[0];
            int shift = (int)StateShift.GetValue(state);
            int ours = SimulationSpace.NNOJBJMFJPJ;
            if (shift == ours || ((byte)StateFlags.GetValue(state) & 8) != 0) return;
            if (!TryMove(shift, unitToMeter, out var move)) return;
            StatePosition.SetValue(state, (Vector3)StatePosition.GetValue(state) + move);
            StateShift.SetValue(state, ours);
            __args[0] = state;
        }

        private static readonly AccessTools.FieldRef<SystemTransform, int> PlaceShift = AccessTools.FieldRefAccess<SystemTransform, int>("_shiftId");
        private static readonly AccessTools.FieldRef<SystemTransform, Vector3> Tracking = AccessTools.FieldRefAccess<SystemTransform, Vector3>("_trackingStartPosition");
        private static readonly AccessTools.FieldRef<SystemTransform, Vector3> NewTracking = AccessTools.FieldRefAccess<SystemTransform, Vector3>("_newTrackingStartPosition");
        private static readonly AccessTools.FieldRef<SystemTransform, bool> TrackingInSkybox = AccessTools.FieldRefAccess<SystemTransform, bool>("_trackingInSkybox");
        private static readonly AccessTools.FieldRef<SystemTransform, bool> ChangePending = AccessTools.FieldRefAccess<SystemTransform, bool>("_changeToApply");
        private static readonly AccessTools.FieldRef<SystemTransform, float> LastSync = AccessTools.FieldRefAccess<SystemTransform, float>("_lastSyncTime");

        private struct PlaceState
        {
            public bool Moved;
            public int OwnShift;
            public int TheirShift;
        }

        // A body's place (SystemTransform.Deserialize): its position is in the system's own terms, which needs
        // nothing, but the game only takes it with the same shift number, and its tracking start is an offset
        // from the sender's centre. Peeked at, taken as if it were ours, and the tracking start moved after.
        private static void PlaceReceive_Prefix(SystemTransform __instance, PooledReader __0, out PlaceState __state)
        {
            __state = default(PlaceState);
            if (!Enabled || __0 == null) return;
            uint at = __0.LLPAKGEPJJP; // Pos
            byte flags = 0;
            int shift = 0;
            bool read = __0.ReadByte(ref flags) && (flags & 1) == 0 && __0.ReadInt(ref shift);
            __0.Seek(at);
            int own = PlaceShift(__instance);
            if (!read || shift == own || !TryOrigin(shift, out _)) return;
            __state = new PlaceState { Moved = true, OwnShift = own, TheirShift = shift };
            PlaceShift(__instance) = shift;
        }

        private static void PlaceReceive_Postfix(SystemTransform __instance, float __1, PlaceState __state)
        {
            if (!__state.Moved) return;
            PlaceShift(__instance) = __state.OwnShift;
            if (LastSync(__instance) != __1) return; // not taken (old, or not placed yet)
            if (!TryMove(__state.TheirShift, TrackingInSkybox(__instance) ? unitToSkybox : unitToMeter, out var move)) return;
            NewTracking(__instance) += move;
            if (!ChangePending(__instance)) Tracking(__instance) = NewTracking(__instance);
        }

        // ---- Our own centre ----------------------------------------------------------------------------

        // The host's centre and its ship's course: not ours. Read through (the object's data goes on after it).
        private static bool SpaceReceive_Prefix(PooledReader __0, ref bool __result)
        {
            if (!Separate) return true;
            bool any = false;
            __0.ReadBool(ref any);
            if (any)
            {
                int shift = 0;
                double d = 0;
                float f = 0;
                __0.ReadInt(ref shift);
                for (int i = 0; i < 6; i++) __0.ReadDouble(ref d);
                for (int i = 0; i < 4; i++) __0.ReadFloat(ref f);
            }
            __result = !__0.OKCBBENHIBC; // HasError
            return false;
        }

        // Shifting our centre as the host shifts its own: when our ship is far enough from it (applied the next
        // fixed update, as the host's are).
        private static void SpaceFixedUpdate_Postfix(SimulationSpace __instance)
        {
            if (Separate)
            {
                NumberOwnShifts(__instance);
                if (!ImpulseOverride) checkShift.Invoke(__instance, null);
            }
            if (Enabled && IsMaster) FlyOtherShips(__instance);
        }

        // The host re-centres everyone on its ship after its impulse: not us.
        private static bool Resync_Prefix() => !Separate || ownResync;

        // ---- Impulse: the end-of-impulse handshake, per ship ------------------------------------------

        private static uint Hash(string name)
        {
            var manager = SyncPointManager.IEFBKOMBFGO;
            if (manager != null) return (uint)hash.Invoke(manager, new object[] { name });
            // Before there's a manager: the same hash, worked out on a throwaway one.
            var temp = (SyncPointManager)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(SyncPointManager));
            return (uint)hash.Invoke(temp, new object[] { name });
        }

        // Our ship's impulse sync points are named for it.
        private static void Sync_Prefix(ref string __0)
        {
            if (Separate && ImpulsePoints.Contains(__0)) __0 = __0 + " " + PvpShips.Mine;
        }

        private static void ClearSync_Prefix(ref string __0)
        {
            if (!Separate || !ImpulsePoints.Contains(__0)) return;
            __0 = __0 + " " + PvpShips.Mine;
            resyncedThisImpulse = false;
        }

        // Our crew is through the impulse: re-centre on our own ship, as the host does everyone on its own.
        private static void Sync_Postfix(string __0, bool __result)
        {
            if (!Separate || !__result || resyncedThisImpulse || __0 != ImpulsePoints[0] + " " + PvpShips.Mine) return;
            resyncedThisImpulse = true;
            var space = Space;
            var here = space == null ? null : playerShipTransform.GetValue(space) as SystemTransform;
            if (here == null) return;
            ownResync = true;
            try
            {
                resync.Invoke(space, new object[] { here.Position });
                pendingShiftId.SetValue(space, MyActor << ActorShift); // the game's re-centre starts the count again at 0: ours
            }
            finally { ownResync = false; }
            Plugin.Log.LogInfo("PvP: impulse over, this crew's space re-centred on its own ship");
        }

        private static void Reached_Prefix(int __1) => reachedHash = (uint)__1;

        private static void Reached_Postfix() => reachedHash = 0;

        // Who an impulse sync point waits for: the host's ship's players for its own (plain names), that
        // ship's for a ship's named one.
        private static bool AllSynced_Prefix(object __0, ref bool __result)
        {
            if (!Enabled || !pointShip.TryGetValue(reachedHash, out var ship)) return true;
            ship = ship == "" ? HostShip : ship;
            var synced = Traverse.Create(__0).Field("PlayersSynced").GetValue<IList>();
            __result = PvpShips.AllPlayers.Where(p => PvpShips.Of(p) == ship).All(p => synced != null && synced.Contains(p));
            return false;
        }

        // The host ends the other ship's impulse when its crew is through (as it waits for its own crew for its own).
        private static readonly AccessTools.FieldRef<ImpulseManager, BaseSpacecraft> ImpulseShip =
            AccessTools.FieldRefAccess<ImpulseManager, BaseSpacecraft>("JCIEFGHGMMP");

        private static void Impulse_Prefix(ImpulseManager __instance, out bool? __state)
        {
            __state = null;
            if (!Enabled || !IsMaster) return;
            var ship = ImpulseShip(__instance) as PlayerSpacecraft;
            var space = Space;
            if (ship == null || space == null || ReferenceEquals(ship, playerShip.GetValue(space))) return;
            string letter = PvpPrototype.ShipOf("A") == ship ? "A" : PvpPrototype.ShipOf("B") == ship ? "B" : null;
            if (letter == null) return;
            __state = getCooldownSynced.Invoke(null, null) is true;
            setCooldownSynced.Invoke(null, new object[] { CrewThrough(letter) });
        }

        private static Exception Impulse_Finalizer(bool? __state, Exception __exception)
        {
            if (__state.HasValue) setCooldownSynced.Invoke(null, new object[] { __state.Value });
            return __exception;
        }

        private static bool CrewThrough(string letter)
        {
            if (!PvpShips.AllPlayers.Any(p => PvpShips.Of(p) == letter)) return true; // no players aboard: nothing to wait for
            var manager = SyncPointManager.IEFBKOMBFGO;
            if (manager == null || !(syncPoints.GetValue(manager) is IDictionary points)) return false;
            var point = points[Hash(ImpulsePoints[1] + " " + letter)];
            return point != null && Traverse.Create(point).Field("Synced").GetValue<bool>();
        }

        // ---- Impulse: the host flies the other ship, and tells its crew where it is ---------------------

        private static float nextTrack;

        // The other ship's engines and impulse manager (each player ship's), or nulls.
        private sealed class OtherShip
        {
            public string Letter;
            public PlayerSpacecraft Ship;
            public object Engines;
            public ImpulseManager Impulse;
        }

        private static IEnumerable<OtherShip> OtherShips(object space)
        {
            foreach (var letter in new[] { "A", "B" })
            {
                var ship = PvpPrototype.ShipOf(letter);
                if (ship == null || ReferenceEquals(ship, playerShip.GetValue(space))) continue;
                var engines = Traverse.Create(ship).Property("GCOAODPDBEI").GetValue();
                var impulse = engines == null ? null : Traverse.Create(engines).Property("POLCIGDAIKK").GetValue<ImpulseManager>();
                if (impulse != null) yield return new OtherShip { Letter = letter, Ship = ship, Engines = engines, Impulse = impulse };
            }
        }

        // Each physics step: the other ship along its course, as SimulationSpace.FixedUpdate does the host's.
        private static void FlyOtherShips(SimulationSpace space)
        {
            foreach (var other in OtherShips(space))
            {
                var phase = other.Impulse.KANLFOJDHPL; // CurrentPhase
                if (phase != ImpulsePhase.Deactivated && phase != ImpulsePhase.Cooldown) Traverse.Create(other.Engines).Method("UpdateImpulse").GetValue();
            }
        }

        // Where it is, to its crew, while it's at impulse.
        private static void SendTracks()
        {
            var space = Space;
            if (space == null) return;
            foreach (var other in OtherShips(space))
            {
                string letter = other.Letter;
                var ship = other.Ship;
                if (other.Impulse.KANLFOJDHPL == ImpulsePhase.Deactivated) continue;
                var crew = PvpShips.AllPlayers.Where(p => PvpShips.Of(p) == letter).Select(PvpShips.Actor).Where(a => a != MyActor).ToArray();
                if (crew.Length == 0) continue;
                var at = ship.ALPAOOKFIEE; // systemTransform
                var rot = at.Rotation;
                NDIOGIGMPBH.RaiseEvent(TrackEvent, new object[] { letter, at.Position.x, at.Position.y, at.Position.z, rot.x, rot.y, rot.z, rot.w },
                    false, new CGJCFMCOBMJ { TargetActors = crew });
            }
        }

        private static readonly Type ShipStateType = AccessTools.Inner(typeof(SimulationSpace), "CBAACGKKEDM");
        private static readonly MethodInfo TimeInSeconds = AccessTools.Method(AccessTools.TypeByName("SVR.Network.GFFJGHDGIFB"), "TimeInSeconds"); // NetworkTime

        private static double NetworkTime() => TimeInSeconds?.Invoke(null, null) is double t ? t : Time.realtimeSinceStartup;

        // Our ship's course, from the host: followed as the game follows the host's ship's.
        private static void OnEvent(byte code, object content, int sender)
        {
            if (code != TrackEvent || !Separate || !(content is object[] track) || track.Length != 8 || !(track[0] is string letter) || letter != PvpShips.Mine) return;
            var space = Space;
            if (space == null) return;
            try
            {
                var position = new Vector3d((double)track[1], (double)track[2], (double)track[3]);
                var rotation = new Quaternion((float)track[4], (float)track[5], (float)track[6], (float)track[7]);
                var state = Activator.CreateInstance(ShipStateType, NetworkTime() + 0.01); // ShipState, as SimulationSpace records the host's
                AccessTools.Field(ShipStateType, "Pos").SetValue(state, position);
                AccessTools.Field(ShipStateType, "Rot").SetValue(state, rotation);
                recordState.Invoke(masterStates.GetValue(space), new object[] { state });
                var ship = playerShip.GetValue(space) as PlayerSpacecraft;
                var engines = ship == null ? null : Traverse.Create(ship).Property("GCOAODPDBEI").GetValue();
                var impulse = engines == null ? null : Traverse.Create(engines).Property("POLCIGDAIKK").GetValue<ImpulseManager>();
                if (impulse != null && impulse.HHBBMNCINCA) masterImpulsePosition.SetValue(space, position); // IsOperating
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("PvP: our ship's impulse course: " + e.Message);
            }
        }
    }
}
