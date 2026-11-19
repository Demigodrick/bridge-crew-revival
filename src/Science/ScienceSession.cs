using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using StbcRevival.Missions;
using UnityEngine;

namespace StbcRevival.Science
{
    /// <summary>
    /// The science console's shared state. The host owns it: it keeps the Ledger and the ScanBoard and publishes
    /// them as room properties (SCU = ledger wire, SCP = board wire, SCC = the actor holding the console, 0 if
    /// free; the board's own wire has no claimant). Crew read them once a second (and at once on joining a room),
    /// so a late joiner gets everything. A crewmate's claim, release, heartbeat, report and complete go to the
    /// host as event 170 (byte[] from ScienceWire); the host judges them (ScanBoard drops anyone but the
    /// claimant) and, on a Complete, grants what the target gives, which only the host looks up.
    /// The host's own use of the console is applied directly, through the same checks.
    /// </summary>
    internal static class ScienceSession
    {
        internal const byte Code = 170;
        private const string KeyLedger = "SCU", KeyBoard = "SCP", KeyClaimant = "SCC";
        private const float PublishEvery = 0.25f, ReadEvery = 1f;
        private const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        /// <summary>Set by the targets (ScienceTargets): what a target grants. Null until then, so nothing is granted.</summary>
        internal static Func<string, TargetInfo> ResolveTarget = null;

        /// <summary>A line to show the whole crew (raised on the host): ScienceTargets puts it on the objectives panel
        /// for a while; it also goes to every console as a message.</summary>
        internal static event Action<string> Notice;

        internal static Ledger Ledger { get; private set; } = new Ledger();
        internal static ScanBoard Board { get; private set; } = new ScanBoard();

        /// <summary>We're in a Patrol with the console on.</summary>
        internal static bool Active { get; private set; }

        /// <summary>The actor at the console, 0 if free.</summary>
        internal static int Claimant => IsHost ? Board.Claimant(Time.unscaledTime) : clientClaimant;

        internal static bool IsHost => !InRoom || (IsMasterClient?.Invoke(null, null) is bool b && b);

        private static MethodInfo isMasterClient;
        private static MethodInfo IsMasterClient => isMasterClient ?? (isMasterClient = Photon?.GetMethod("get_isMasterClient", Static));

        /// <summary>This machine's actor number (1 when not in a room).</summary>
        internal static int LocalActor
        {
            get
            {
                var me = Photon?.GetMethod("get_player", Static)?.Invoke(null, null);
                return ActorOf(me) is int id && id > 0 ? id : 1;
            }
        }

        private static int clientClaimant;
        private static string seenRoom;
        private static bool hooked, adopted, patrolSeen; // patrolSeen: this room has had a Patrol (or its state), so it gets published
        private static float nextPublish, nextRead, nextActive;
        private static string sentLedger, sentBoard;
        private static int sentClaimant = -1;
        private static string readLedger, readBoard;
        private static readonly HashSet<string> warned = new HashSet<string>();

        // ------------------------------------------------------------------------------------------- host: mission hooks
        /// <summary>A Patrol began (host): a fresh ledger and board, the console on if this bridge has one.</summary>
        internal static void PatrolStarted()
        {
            if (!IsHost) return;
            patrolSeen = true;
            Ledger = TestLedger() ?? new Ledger { On = SciencePlacement.HasForCurrentBridge || (Plugin.ScienceOn != null && Plugin.ScienceOn.Value) };
            Board = new ScanBoard();
            adopted = true; // ours now: not to be replaced by what the room still holds
            sentLedger = sentBoard = null;
            sentClaimant = -1;
            nextPublish = 0f;
            Active = Ledger.On;
            Plugin.Log.LogInfo("Science console: " + (Ledger.On ? "on" : "off") + " for this Patrol");
        }

        // [Debug] ScienceLedger: a ledger to start the Patrol with (switched on), or null when unset or not a ledger.
        private static Ledger TestLedger()
        {
            string wire = Plugin.ScienceLedger?.Value?.Trim();
            if (string.IsNullOrEmpty(wire)) return null;
            Ledger l = Ledger.FromWire(wire);
            if (!l.On && l.ToWire() == new Ledger().ToWire()) // what FromWire gives for anything malformed
            {
                Plugin.Log.LogWarning("Science console: [Debug] ScienceLedger isn't a ledger (" + wire + "); ignored");
                return null;
            }
            l.On = true;
            Plugin.Log.LogInfo("Science console: testing with the ledger " + l.ToWire() + " from [Debug] ScienceLedger");
            return l;
        }

        /// <summary>The Patrol ended (every machine): the console is off again and the totals are forgotten; the host publishes that.</summary>
        internal static void PatrolEnded()
        {
            Ledger = new Ledger();
            Board = new ScanBoard();
            readLedger = readBoard = null; // a crewmate reads the room's state again, even if its text is the same as before
            clientClaimant = 0;
            Active = false;
            nextPublish = 0f;
            ScienceEffects.Forget();
            ScienceTargets.Forget();
        }

        /// <summary>An assignment was completed (host): the enemy grows a step stronger.</summary>
        internal static void AssignmentCompleted()
        {
            if (!IsHost || !Ledger.On) return;
            Ledger.AddEnemyStep();
            int i = (Ledger.EnemySteps - 1) % 3; // the area this step went to: damage, shields, speed in turn
            Say("Enemy " + (i == 0 ? "damage" : i == 1 ? "shields" : "speed") + " +" + Pct(ScienceTuning.EnemyStep) + "%");
        }

        /// <summary>The targets of this Patrol (host).</summary>
        internal static void HostSetTargets(IEnumerable<string> ids)
        {
            if (IsHost) Board.SetTargets(ids);
        }

        // ------------------------------------------------------------------------------------------- crew: messages
        internal static void SendClaim() => Send(new ScienceMsg { Kind = ScienceMsgKind.Claim }, ScienceWire.Claim());
        internal static void SendRelease() => Send(new ScienceMsg { Kind = ScienceMsgKind.Release }, ScienceWire.Release());
        internal static void SendHeartbeat() => Send(new ScienceMsg { Kind = ScienceMsgKind.Heartbeat }, ScienceWire.Heartbeat());
        internal static void SendReport(string id, float p) =>
            Send(new ScienceMsg { Kind = ScienceMsgKind.Report, Id = id, Progress = p }, ScienceWire.Report(id, p));
        internal static void SendComplete(string id) =>
            Send(new ScienceMsg { Kind = ScienceMsgKind.Complete, Id = id }, ScienceWire.Complete(id));

        private static void Send(ScienceMsg local, byte[] wire)
        {
            if (wire == null) return;
            if (IsHost) { Apply(LocalActor, local); return; }
            try
            {
                int master = MasterActor;
                if (master > 0) NDIOGIGMPBH.RaiseEvent(Code, wire, true, new CGJCFMCOBMJ { TargetActors = new[] { master } });
            }
            catch (Exception e) { WarnOnce("send", "Science console: sending: " + e.Message); }
        }

        // ------------------------------------------------------------------------------------------- host: judging
        private static void OnEvent(byte code, object content, int sender)
        {
            try
            {
                if (code != Code || !IsHost) return;
                ScienceMsg m;
                if (!(content is byte[] b) || !ScienceWire.TryRead(b, out m)) return;
                Apply(sender, m);
            }
            catch (Exception e) { WarnOnce("receive", "Science console: receiving: " + e.Message); }
        }

        private static void Apply(int sender, ScienceMsg m)
        {
            if (!Active) return;
            float now = Time.unscaledTime;
            switch (m.Kind)
            {
                case ScienceMsgKind.Claim: Board.TryClaim(sender, now); break;
                case ScienceMsgKind.Release: Board.Release(sender); break;
                case ScienceMsgKind.Heartbeat: Board.Heartbeat(sender, now); break;
                case ScienceMsgKind.Report: Board.Report(sender, m.Id, m.Progress, now); break;
                case ScienceMsgKind.Complete:
                    if (Board.Progress(m.Id) < 100f) break; // reports got it there first, or it doesn't count
                    if (Board.Complete(sender, m.Id, now)) Grant(m.Id);
                    break;
            }
        }

        private static void Grant(string id)
        {
            TargetInfo info = null;
            try { info = ResolveTarget?.Invoke(id); }
            catch (Exception e) { WarnOnce("resolve", "Science console: looking up a target: " + e.Message); }
            if (info == null) { WarnOnce("unresolved", "Science console: a scan finished on a target nothing knows (" + id + "); nothing granted"); return; }
            // Named by what it is, not by the target's own name ("Graviton stream analysed: shields +5%").
            if (info.Area.HasValue)
            {
                UpgradeArea a = info.Area.Value;
                Ledger.AddScan(a);
                Say((ScienceKinds.ReadingNameFor(a) ?? "Reading") + " analysed: " + AreaText(a) + " +" + Pct(Ledger.Crew(a)) + "%");
            }
            else if (Ledger.IsHostileTeam(info.Team))
            {
                Ledger.AddFactionScan(info.Team);
                Say(ScienceKinds.DebrisName(info.Team) + " analysed: weak spots +" + Pct(Ledger.CrewVsFaction(info.Team)) + "%");
            }
            else WarnOnce("nogrant", "Science console: " + id + " gives nothing (no area, no hostile team)");
        }

        private static string AreaText(UpgradeArea a) => a.ToString().ToLowerInvariant();
        private static int Pct(float share) => (int)Math.Round(share * 100f);

        private static void Say(string text)
        {
            try { Notice?.Invoke(text); } catch (Exception e) { WarnOnce("notice", "Science console: notice: " + e.Message); }
            try { Bridge.Instance.SendConsoleMessage(UI.MIFGHKACDIH.All, text, 6f); }
            catch (Exception e) { WarnOnce("console", "Science console: console message: " + e.Message); }
        }

        // ------------------------------------------------------------------------------------------- every frame
        internal static void Update()
        {
            if (!hooked) { hooked = true; NDIOGIGMPBH.OnEventCall += OnEvent; }
            float now = Time.unscaledTime;
            bool host = IsHost;
            if (now >= nextActive)
            {
                nextActive = now + ReadEvery;
                Active = Ledger.On && RevivalMissions.Selected == typeof(PatrolSequence) && StbcRevival.Walking.Bridge.OnBridge();
                ScienceTargets.ShrinkDebris();
            }
            var room = Room;
            if (room == null)
            {
                if (seenRoom != null) { seenRoom = null; ResetRoomState(); }
                return;
            }
            string name = RoomName(room);
            if (name != seenRoom)
            {
                if (seenRoom != null) ResetRoomState(); // another room: nothing of the last one carries over
                seenRoom = name;
                adopted = false;
                sentLedger = sentBoard = readLedger = readBoard = null;
                sentClaimant = -1;
                nextRead = nextPublish = 0f;
            }
            if (host)
            {
                if (!adopted) { adopted = true; Adopt(room); }
                if (patrolSeen && now >= nextPublish) { nextPublish = now + PublishEvery; Publish(room, now); }
            }
            else
            {
                adopted = false; // a later host change adopts the room's state afresh
                sentLedger = sentBoard = null;
                sentClaimant = -1;
                if (now >= nextRead) { nextRead = now + ReadEvery; Read(room); }
            }
        }

        // A new host with the old one's state in the room takes it over (the claimant is lost: the console is free).
        private static void Adopt(object room)
        {
            string u = Prop(room, KeyLedger) as string, p = Prop(room, KeyBoard) as string;
            if (string.IsNullOrEmpty(u)) return;
            patrolSeen = true;
            Ledger = Ledger.FromWire(u);
            Board = ScanBoard.FromWire(p);
            sentLedger = u; sentBoard = p;
            Plugin.Log.LogInfo("Science console: took over the room's state");
        }

        private static void ResetRoomState()
        {
            Ledger = new Ledger();
            Board = new ScanBoard();
            clientClaimant = 0;
            Active = false;
            patrolSeen = false;
            ScienceEffects.Forget();
            ScienceTargets.Forget();
            sentLedger = sentBoard = readLedger = readBoard = null;
            sentClaimant = -1;
        }

        private static void Publish(object room, float now)
        {
            string u = Ledger.ToWire(), p = Board.ToWire();
            int c = Board.Claimant(now);
            if (u != sentLedger && PhotonProps.Set(room, KeyLedger, u)) sentLedger = u;
            if (p != sentBoard && PhotonProps.Set(room, KeyBoard, p)) sentBoard = p;
            if (c != sentClaimant && PhotonProps.Set(room, KeyClaimant, c)) sentClaimant = c;
        }

        private static void Read(object room)
        {
            string u = Prop(room, KeyLedger) as string, p = Prop(room, KeyBoard) as string;
            if (u != readLedger) { readLedger = u; Ledger = Ledger.FromWire(u); }
            if (p != readBoard) { readBoard = p; Board = ScanBoard.FromWire(p); }
            clientClaimant = Prop(room, KeyClaimant) is int c && c > 0 ? c : 0;
        }

        // ------------------------------------------------------------------------------------------- names
        /// <summary>The display name of a player in the room, by actor number.</summary>
        internal static string PlayerName(int actor)
        {
            try
            {
                if (Photon?.GetMethod("get_playerList", Static)?.Invoke(null, null) is Array list)
                    foreach (var p in list)
                        if (p != null && ActorOf(p) == actor && p.GetType().GetMethod("get_name")?.Invoke(p, null) is string n && n.Length > 0) return n;
            }
            catch (Exception) { }
            return "player " + actor;
        }

        // ------------------------------------------------------------------------------------------- Photon
        private static Type Photon => Patches.PhotonNetworkType;
        private static MethodInfo getRoom, getRoomName, getProps;
        private static Type roomType;
        private static object Room => (getRoom ?? (getRoom = Photon?.GetMethod("get_room", Static)))?.Invoke(null, null);

        // The room's name and custom properties (their getters looked up once for the room's type).
        private static void RoomMethods(object room)
        {
            if (room.GetType() == roomType) return;
            roomType = room.GetType();
            getRoomName = roomType.GetMethod("get_name", All);
            getProps = roomType.GetMethod("get_customProperties", All);
        }

        private static string RoomName(object room) { RoomMethods(room); return getRoomName?.Invoke(room, null) as string; }
        private static bool InRoom => GetInRoom?.Invoke(null, null) is bool b && b;
        private static MethodInfo getInRoom;
        private static MethodInfo GetInRoom => getInRoom ?? (getInRoom = Photon?.GetMethod("get_inRoom", Static));
        private static int? ActorOf(object player) =>
            player?.GetType().GetMethod("get_ID", BindingFlags.Public | BindingFlags.Instance)?.Invoke(player, null) as int?;

        private static int MasterActor => ActorOf(Photon?.GetMethod("get_masterClient", Static)?.Invoke(null, null)) ?? -1;

        private static object Prop(object room, string key)
        {
            RoomMethods(room);
            return getProps?.Invoke(room, null) is IDictionary props && props.Contains(key) ? props[key] : null;
        }

        private static void WarnOnce(string kind, string text)
        {
            if (warned.Add(kind)) Plugin.Log.LogWarning(text);
        }
    }
}
