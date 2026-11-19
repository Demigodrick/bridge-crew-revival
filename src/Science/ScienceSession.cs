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

        /// <summary>Set by the targets (Task 5): what a target grants. Null until then, so nothing is granted.</summary>
        internal static Func<string, TargetInfo> ResolveTarget = null;
        /// <summary>Set by the targets (Task 5): a target's display name for notices (the id if unset).</summary>
        internal static Func<string, string> TargetName = null;

        /// <summary>A line to show the whole crew (raised on the host; the game carries it to every console).</summary>
        internal static event Action<string> Notice;

        internal static Ledger Ledger { get; private set; } = new Ledger();
        internal static ScanBoard Board { get; private set; } = new ScanBoard();

        /// <summary>We're in a Patrol with the console on.</summary>
        internal static bool Active { get; private set; }

        /// <summary>The actor at the console, 0 if free.</summary>
        internal static int Claimant => IsHost ? Board.Claimant(Time.unscaledTime) : clientClaimant;

        internal static bool IsHost => !InRoom || (Photon?.GetMethod("get_isMasterClient", Static)?.Invoke(null, null) is bool b && b);

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
        private static bool hooked, adopted;
        private static float nextPublish, nextRead, nextActive;
        private static string sentLedger, sentBoard;
        private static int sentClaimant = -1;
        private static string readLedger, readBoard;
        private static readonly HashSet<string> warned = new HashSet<string>();

        // ------------------------------------------------------------------------------------------- host: mission hooks
        /// <summary>A Patrol began (host): a fresh ledger and board, the console on if this bridge has one.</summary>
        internal static void PatrolStarted()
        {
            Ledger = new Ledger { On = SciencePlacement.HasForCurrentBridge || (Plugin.ScienceOn != null && Plugin.ScienceOn.Value) };
            Board = new ScanBoard();
            adopted = true; // ours now: not to be replaced by what the room still holds
            sentLedger = sentBoard = null;
            sentClaimant = -1;
            nextPublish = 0f;
            Active = Ledger.On;
            Plugin.Log.LogInfo("Science console: " + (Ledger.On ? "on" : "off") + " for this Patrol");
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
            if (code != Code || !IsHost) return;
            try
            {
                ScienceMsg m;
                if (!(content is byte[] b) || !ScienceWire.TryRead(b, out m)) return;
                Apply(sender, m);
            }
            catch (Exception e) { WarnOnce("receive", "Science console: receiving: " + e.Message); }
        }

        private static void Apply(int sender, ScienceMsg m)
        {
            if (!Ledger.On) return;
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
            string name = id;
            try { name = TargetName?.Invoke(id) ?? id; } catch (Exception) { }
            if (info.Area.HasValue)
            {
                UpgradeArea a = info.Area.Value;
                Ledger.AddScan(a);
                Say(name + " analysed: " + AreaText(a) + " +" + Pct(Ledger.Crew(a)) + "%");
            }
            else if (info.Team != 0)
            {
                Ledger.AddFactionScan(info.Team);
                Say(name + " analysed: weak spots +" + Pct(Ledger.CrewVsFaction(info.Team)) + "%");
            }
            else WarnOnce("nogrant", "Science console: " + id + " gives nothing (no area, no team)");
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
                Active = Ledger.On && RevivalMissions.Selected == typeof(PatrolSequence);
            }
            var room = Room;
            if (room == null) { seenRoom = null; return; }
            string name = room.GetType().GetMethod("get_name", All)?.Invoke(room, null) as string;
            if (name != seenRoom)
            {
                seenRoom = name;
                adopted = false;
                sentLedger = sentBoard = readLedger = readBoard = null;
                sentClaimant = -1;
                nextRead = nextPublish = 0f;
            }
            if (host)
            {
                if (!adopted) { adopted = true; Adopt(room); }
                if (now >= nextPublish) { nextPublish = now + PublishEvery; Publish(room, now); }
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
            Ledger = Ledger.FromWire(u);
            Board = ScanBoard.FromWire(p);
            sentLedger = u; sentBoard = p;
            Plugin.Log.LogInfo("Science console: took over the room's state");
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
        private static object Room => Photon?.GetMethod("get_room", Static)?.Invoke(null, null);
        private static bool InRoom => Photon?.GetMethod("get_inRoom", Static)?.Invoke(null, null) is bool b && b;
        private static int? ActorOf(object player) =>
            player?.GetType().GetMethod("get_ID", BindingFlags.Public | BindingFlags.Instance)?.Invoke(player, null) as int?;

        private static int MasterActor => ActorOf(Photon?.GetMethod("get_masterClient", Static)?.Invoke(null, null)) ?? -1;

        private static object Prop(object room, string key) =>
            room.GetType().GetMethod("get_customProperties", All)?.Invoke(room, null) is IDictionary props && props.Contains(key) ? props[key] : null;

        private static void WarnOnce(string kind, string text)
        {
            if (warned.Add(kind)) Plugin.Log.LogWarning(text);
        }
    }
}
