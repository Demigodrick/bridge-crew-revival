using System;
using System.Collections.Generic;
using System.Linq;
using BodyRef = Mission.Scripting.KOOLIJPNDOH;          // a reference to a sector body (ship), filled by spawn tasks
using MissionSequence = Mission.Scripting.MNFBGALLEAJ;  // base class of mission sequences
using MissionObjective = Mission.Scripting.MJHDDKANLEC; // base class of a sequence's objectives
using Spawner = Mission.Scripting.FFPNMKADDPK;          // a spawn definition (prefab, system, name, tags)
using Step = Mission.Scripting.Framework.Tasks.KAPOBAJDOEJ; // a scripting task, yielded from objective coroutines
using StartSystem = BHNEPPPPMKH;                         // starting-system choice
using Tag = BLECEHNPNBH;
using BridgeObject = KFLPAPJAAGO;                        // bridge crew members who speak VO lines
using ViewScreen = UI.CAJKAPALCCO;                       // viewscreen messages

namespace StbcRevival.Missions
{
    /// <summary>
    /// Stellar Evacuation (Revival Missions): a star is about to erupt and a station in its system has to be
    /// emptied. Warp there; a transport waits an impulse jump from the station. The station's people are
    /// ready for the transporter a group at a time: scan the station, beam the group aboard, impulse to
    /// the transport and drop them off (within DeliverRange, as the game's own rescues "deliver survivors to
    /// safety"), then go back for the next group. [Revival] EvacuationMinutes sets the time (10 by default).
    /// Centred on Perseph (in the Trench). The station is the loaded one near the ship (found by tag or name: the
    /// map's tagged bodies elsewhere are unloaded placeholders). Each group waits in an escape pod by it (the
    /// station can't be the transporter's target: the captain's order checks need ship data it hasn't got);
    /// the pod's count is set on every player's game (the host shares it as a room property, "EVC"). Where there's a StarManager star
    /// (the E3 demo sector), the eruption is the E3 demo's own: that sector's star
    /// (tagged Star and Enemy) has a StarManager whose animation goes through "event1", "event2",
    /// "preCataclysm" and "cataclysm" (sent to every player), with the demo's warnings from the comms officer.
    /// Without that star (another map), the game's procedural rescue eruption stands in: the blast-wave
    /// prefab at the star. Either way, a minute's countdown at the end; still in the system at zero, the ship
    /// is lost. Warping out with survivors aboard saves them too. Scored on people saved.
    /// </summary>
    public sealed class StellarEvacuationSequence : MissionSequence
    {
        public override void CreateSequence()
        {
            var evacuation = Sequence.MakeNode<StellarEvacuationObjective>();
            Sequence.OnFail(evacuation).MissionFails();
            Sequence.OnSucceed(evacuation).MissionSucceeds();
            Sequence.StartObjective(evacuation);
        }

        public override void StartingSystemSelection(StartSystem selection)
        {
            selection.ForceRandomSystemSelection = true;
            selection.Choice = StartSystem.LPCLBKKEBMK.Random;
        }
    }

    public sealed class StellarEvacuationObjective : MissionObjective
    {
        internal const string Title = "STELLAR EVACUATION";
        private const string Directive = "Evacuate the station before the star erupts";
        private const string Escape = "Warp out before the star erupts";
        private const double DeliverRange = 5000;      // metres from the transport to hand the survivors over
        private const double ArrivedRange = 100000;    // metres from the quarantine point's marker: you've impulsed there
        private const float TransportDistance = 35000f; // metres from the station to the transport
        private const float FinalCountdown = 60f;       // the game's eruption: a minute from the blast to the end
        private const string ExplosionPrefab = "Sector/System/Mission 02 Prefabs/StarExplosion";
        private const string Home = "Perseph";
        private const string EscapePod = "Sector/Ships/Ship - EscapePodFull"; // the E3 demo's survivors' pods

        private static readonly string[] Freighters =
        {
            "Sector/Ships/Ship - Freighter A", "Sector/Ships/Ship - Freighter B", "Sector/Ships/Ship - Freighter C",
        };

        private readonly System.Random random = new System.Random(Guid.NewGuid().GetHashCode());
        private BodyRef player, station, transport, pod;
        private Map.SystemRoot system;
        private BodyRef quarantine; // the quarantine point (a placeholder: only its position is used)
        private int stationPeople;  // as last seen while the station was loaded
        private UnityEngine.Component star; // the E3 star's StarManager (its eruption animation), if the system has it
        private static readonly Type StarManagerType = HarmonyLib.AccessTools.TypeByName("StarManager"); // internal to the game
        private int total, placed, saved, spawnCount;
        private bool lost, erupting, spawningTransport;
        private object lastLive;   // the station's loaded body when last seen (a new one means it reloaded)
        private string notice;     // a short-lived line under the objective (group ready, people transferred)
        private float noticeUntil;

        public override void StartScript()
        {
            player = Objective.FFKKADIEOGP;
            Objective.AddStep("evacuation", Task.MFLJBKCDBMC.WithSubtaskCoroutine(Evacuate));
            Objective.AddStep("saved", Objective.Success());
            Objective.AddStep("lost", Objective.Failure());
        }

        private IEnumerator<Step> Evacuate()
        {
            foreach (var step in MissionAudio.Start(Task)) yield return step;
            yield return Task.HHNFJKHGEPH.WithMissionString(Title);
            yield return Task.OKLDNLKLLFJ.WithGroup("State_Group_Mus_Dyn").WithState("Explore");
            yield return Task.KNMMMJOOBHC.WithTime(8f);

            // The system: one with a space station, preferably with an erupting star of its own.
            system = PickSystem();
            string systemName = SystemName(system);
            string travel = $"Warp to the {systemName} system: its star is about to erupt";
            yield return Task.NECMCDDOFJB.WithDescriptionString(Directive);
            yield return Task.NECMCDDOFJB.AddSubDirectives(Directive, travel);
            if (Objective.KALBLMGFNIN != system)
            {
                yield return Task.BCJDEKKIHDD.AddMarker(new[] { system });
                while (Objective.KALBLMGFNIN != system) yield return Task.KNMMMJOOBHC.WithTime(2f);
                yield return Task.BCJDEKKIHDD.RemoveAllMarkers();
            }
            yield return Task.NECMCDDOFJB.RemoveSubDirective(Directive, travel);
            yield return Task.KNMMMJOOBHC.WithTime(3f); // out of warp
            Plugin.Log.LogInfo($"Stellar evacuation: in {systemName}");

            // The countdown starts as you arrive (before anything else, so a hitch below can't hold it up).
            star = CriticalStar(system);
            Plugin.Log.LogInfo(star != null ? $"Stellar evacuation: the star's own eruption ({star.name})" : "Stellar evacuation: no erupting star here; the blast-wave effect stands in");
            total = 10 * random.Next(6, 11); // 60-100 people
            int minutes = Math.Max(3, Math.Min(30, Plugin.EvacuationMinutes.Value));
            float end = UnityEngine.Time.time + minutes * 60f;
            yield return Task.NECMCDDOFJB.WithDescriptionString(Directive).WithTimer(minutes * 60.0 - FinalCountdown);
            yield return Task.CAJKAPALCCO.SendViewScreenMessage(ViewScreen.GCIOEGLLOLP.Warning, 5f, "Stellar eruption imminent: evacuate the station");
            yield return Task.OKLDNLKLLFJ.WithGroup("State_Group_Mus_Dyn").WithState("Moderate_Tension");

            // The station: one that's actually loaded here (the map's tagged bodies elsewhere in the system are
            // placeholders, "attic" bodies, until you impulse there, and the transporter won't beam from those).
            DumpBodies();
            var live = LiveStation();
            string stationName;
            if (live != null)
            {
                station = new BodyRef(live.ALPAOOKFIEE);
                stationName = Localized(live.ALPAOOKFIEE.BaseName);
                if (string.IsNullOrEmpty(stationName) || stationName.Contains("|")) stationName = "the station";
            }
            else
            {
                // No station here after all: a crippled freighter stands in for it.
                Plugin.Log.LogWarning($"Stellar evacuation: no station near the ship in {systemName}; using a freighter");
                station = new BodyRef();
                yield return Task.BFMBGDIDKEI.UsingSpawner(MakeSpawner(Freighters[random.Next(Freighters.Length)], "Colony ship", Tag.Friendly))
                    .AtAngleAndDistanceFromBody(player, random.Next(0, 360), 8000f).OutputTo(station);
                yield return Task.KDNOMKAPDKG.TellAI(station).ToDisable();
                stationName = "the colony ship";
            }
            Plugin.Log.LogInfo($"Stellar evacuation: station {stationName}, {Distance(player, station) / 1000:0} km away");
            yield return Task.BCJDEKKIHDD.AddMarker(new[] { station });

            // The drop-off: the system's quarantine point if it has one (Perseph does): an impulse jump away. The
            // transport waits there, spawned when you first arrive (its area isn't loaded before). Without one,
            // the transport waits TransportDistance from the station.
            quarantine = AllBodies().FirstOrDefault(b => Named(b, "quarantine"));
            transport = new BodyRef();
            string dropOff;
            if (quarantine != null)
            {
                dropOff = "the quarantine point";
                Plugin.Log.LogInfo($"Stellar evacuation: drop-off at the quarantine point, {Distance(player, quarantine) / 1000:0} km away");
            }
            else
            {
                dropOff = "the transport";
                yield return Task.BFMBGDIDKEI.UsingSpawner(MakeSpawner(Freighters[random.Next(Freighters.Length)], "Transport", Tag.Friendly))
                    .AtAngleAndDistanceFromBody(player, random.Next(0, 360), TransportDistance).OutputTo(transport);
                yield return Task.KDNOMKAPDKG.TellAI(transport).ToDisable(); // holds position
                yield return Task.BCJDEKKIHDD.AddMarker(new[] { station, transport });
                Plugin.Log.LogInfo($"Stellar evacuation: transport {Distance(station, transport) / 1000:0} km from the station");
            }
            Plugin.Log.LogInfo($"Stellar evacuation: {total} people on {stationName} in {systemName}, {minutes} minutes");

            string how = quarantine != null
                ? $"At {stationName}, scan each escape pod and beam the people aboard, then impulse to Quarantine, where a transport takes them"
                : $"At {stationName}, scan each escape pod and beam the people aboard, then impulse to the transport to drop them off";
            yield return Task.NECMCDDOFJB.AddSubDirectives(Directive, how);
            string status = Status(0);
            yield return Task.NECMCDDOFJB.AddSubDirectives(Directive, status);

            bool warned1 = false, warned2 = false, warned3 = false;
            bool ready = true;  // the next group can go onto the pad (the last one's been dropped off)
            float placedAt = 0; bool warnedEmpty = false;
            float said45 = 0, said30 = 0, said20 = 0, said10 = 0;
            for (;;)
            {
                yield return Task.KNMMMJOOBHC.WithTime(1f);
                float left = end - UnityEngine.Time.time;

                // Left the system: whoever's aboard is safe, and that's the end of it.
                if (Objective.KALBLMGFNIN != system)
                {
                    saved += Aboard();
                    break;
                }

                // The current group's escape pod, read only while it's loaded (you're at the station); if it
                // unloaded and came back without them, they go back aboard.
                int aboard = Aboard();
                bool atStation = Live(station);
                if (pod != null && Live(pod))
                {
                    var body = pod.AHMPFHDGMKJ;
                    if (lastLive != null && !ReferenceEquals(body, lastLive) && stationPeople > 0 && People(pod) == 0)
                    {
                        PutPeople(pod, stationPeople);
                        Beamable(pod);
                        Plugin.Log.LogInfo($"Stellar evacuation: {stationPeople} people back in the reloaded escape pod");
                    }
                    else stationPeople = People(pod);
                    lastLive = body;
                }
                int onStation = stationPeople;

                // The transport at the quarantine point, once you're there (its area is loaded then).
                if (quarantine != null && transport.AHMPFHDGMKJ == null && !spawningTransport && Distance(player, quarantine) <= ArrivedRange)
                {
                    spawningTransport = true;
                    yield return Task.BFMBGDIDKEI.UsingSpawner(MakeSpawner(Freighters[random.Next(Freighters.Length)], "Transport", Tag.Friendly))
                        .AtAngleAndDistanceFromBody(player, random.Next(0, 360), 3000f).FaceOtherBody(player).OutputTo(transport);
                    yield return Task.KDNOMKAPDKG.TellAI(transport).ToDisable(); // holds position
                    yield return Task.BCJDEKKIHDD.AddMarker(new[] { transport });
                    Plugin.Log.LogInfo($"Stellar evacuation: transport waiting at the quarantine point ({(transport.AHMPFHDGMKJ != null ? "spawned" : "not spawned")})");
                }

                // The next group, in an escape pod by the station, once the last one's been dropped off. (The
                // station itself can't be the transporter's target: the captain's order checks need ship data
                // a station hasn't got, and break every crew order. The E3 demo beamed people from pods too.)
                if (ready && atStation && onStation == 0 && aboard == 0 && placed < total && !erupting)
                {
                    ready = false;
                    placedAt = UnityEngine.Time.time;
                    int group = Math.Min(total - placed, 10 + random.Next(0, 6));
                    placed += group;
                    pod = new BodyRef();
                    yield return Task.BFMBGDIDKEI.UsingSpawner(MakeSpawner(EscapePod, "Escape pod", Tag.Friendly))
                        .AtAngleAndDistanceFromBody(station, random.Next(0, 360), 1500f).OutputTo(pod);
                    PutPeople(pod, group);
                    Beamable(pod);
                    stationPeople = group;
                    lastLive = pod.AHMPFHDGMKJ;
                    yield return Task.BCJDEKKIHDD.RemoveAllMarkers();
                    yield return Task.BCJDEKKIHDD.AddMarker(transport.AHMPFHDGMKJ != null ? new[] { pod, transport } : new[] { pod });
                    Plugin.Log.LogInfo($"Stellar evacuation: group of {group} in an escape pod ({placed}/{total} placed), {Distance(player, pod) / 1000:0} km away");
                    foreach (var step in Say($"{group} people waiting in an escape pod at {stationName}: scan it and beam them aboard")) yield return step;
                }

                // Survivors aboard and the transport close: hand them over.
                if (aboard > 0 && transport.AHMPFHDGMKJ != null && Distance(player, transport) <= DeliverRange)
                {
                    saved += aboard;
                    if (stationPeople == 0) ready = true;
                    yield return Task.CIAHOPCODGJ.WithTargets(player).SetCargo(Sector.CargoTypes.People, 0);
                    Plugin.Log.LogInfo($"Stellar evacuation: {aboard} delivered, {saved}/{total} saved");
                    foreach (var step in Say(saved >= total ? "Everyone's off the station. Warp out!" : $"{aboard} people transferred to the transport")) yield return step;
                }

                if (notice != null && UnityEngine.Time.time > noticeUntil)
                    foreach (var step in Say(null)) yield return step;
                if (!ready && !warnedEmpty && atStation && onStation == 0 && aboard == 0 && UnityEngine.Time.time - placedAt > 10f)
                {
                    warnedEmpty = true; // the group never showed up on the station, or vanished without reaching us
                    Plugin.Log.LogWarning($"Stellar evacuation: the escape pod shows no people ({placed} placed, {saved} saved)");
                }
                string now = Status(Aboard());
                if (now != status)
                {
                    yield return Task.NECMCDDOFJB.RemoveSubDirective(erupting ? Escape : Directive, status);
                    status = now;
                    yield return Task.NECMCDDOFJB.AddSubDirectives(erupting ? Escape : Directive, status);
                }

                // The science officer's warnings, then the eruption (the game's own, from its rescue mission).
                float span = minutes * 60f - FinalCountdown;
                if (!warned1 && left - FinalCountdown < span * 0.5f)
                {
                    warned1 = true;
                    if (star != null)
                    {
                        Stage("event1");
                        yield return Comms("PLAY_VO_E3CDD_E3RED_SCRIP_STAR_WARN1_001_02E3REDM_DRY_599");
                        yield return Comms("PLAY_VO_E3CDD_E3RED_SCRIP_STAR_WARN1_001_03E3REDM_DRY_600");
                    }
                    else
                    {
                        yield return Vo("PLAY_VO_CAMP_M02_OBJ02_SWARN_1ST_001_01ASCIO_DRY_1255");
                        yield return Vo("PLAY_VO_CAMP_M02_OBJ02_SWARN_1ST_001_02ASCIO_DRY_1256");
                    }
                }
                if (!warned2 && left - FinalCountdown < span * 0.25f)
                {
                    warned2 = true;
                    yield return Task.CAJKAPALCCO.SendViewScreenMessage(ViewScreen.GCIOEGLLOLP.Warning, 5f, "Stellar core unstable");
                    yield return Task.OKLDNLKLLFJ.WithGroup("State_Group_Mus_Dyn").WithState("Moderate_Tension");
                    if (star != null)
                    {
                        Stage("event2");
                        yield return Comms("PLAY_VO_E3CDD_E3RED_SCRIP_STAR_WARN2_001_02E3REDM_DRY_601");
                    }
                    else
                    {
                        yield return Vo("PLAY_VO_CAMP_M02_OBJ02_SWARN_2ND_001_01ASCIO_DRY_1258");
                        yield return Vo("PLAY_VO_CAMP_M02_OBJ02_SWARN_2ND_001_02ASCIO_DRY_1259");
                    }
                }
                if (!warned3 && left - FinalCountdown < 15f)
                {
                    warned3 = true;
                    if (star != null) yield return Comms("PLAY_VO_E3CDD_E3RED_SCRIP_STAR_WARN3_001_01E3REDM_DRY_594");
                    else
                    {
                        yield return Vo("PLAY_VO_CAMP_M02_OBJ02_SWARN_3RD_001_01ASCIO_DRY_1263");
                        yield return Vo("PLAY_VO_CAMP_M02_OBJ02_SWARN_3RD_001_02ASCIO_DRY_1264");
                    }
                }
                if (!erupting && left <= FinalCountdown)
                {
                    erupting = true;
                    foreach (var step in Erupt(status)) yield return step;
                    said45 = said30 = said20 = said10 = 0;
                }
                if (erupting)
                {
                    if (star != null && said45 == 0 && left <= 45f) { said45 = 1; yield return Comms("PLAY_VO_E3CDD_E3RED_SCRIP_STAR_FINAL_001_06E3REDM_DRY_598"); }
                    if (said30 == 0 && left <= 30f) { said30 = 1; yield return Vo("PLAY_VO_CAMP_M02_OBJ02_SWARN_COUNT_30S_001_01ASCIO_DRY_1328"); }
                    if (said20 == 0 && left <= 20f) { said20 = 1; yield return Vo("PLAY_VO_CAMP_M02_OBJ02_SWARN_COUNT_20S_001_01ASCIO_DRY_1330"); }
                    if (said10 == 0 && left <= 10f) { said10 = 1; yield return Vo("PLAY_VO_CAMP_M02_OBJ02_SWARN_COUNT_10S_001_01ASCIO_DRY_1332"); }
                    if (left <= 0f)
                    {
                        // Still here: the ship goes with the system, as in the game's mission.
                        lost = true;
                        Stage("cataclysm");
                        Plugin.Log.LogInfo($"Stellar evacuation: caught by the eruption; {saved}/{total} saved");
                        yield return Task.ABHANLLBILL.WithEventID("Play_SFX_Exp_Star_Explosion_Full_E3").ContinueImmediately();
                        yield return Task.HHNFJKHGEPH.WithMissionResult($"Lost in the eruption. {saved} of {total} people were saved", false);
                        yield return Task.HFBHFAGDFOM.BeginExternalView().AtRelativePosition(50f, -15f, 35f);
                        yield return Task.JFEBIIPBJCA.ToCurrentThrottleAndPower(100f, 2);
                        yield return Task.ECAJNALPIHI.DestroyShip();
                        break;
                    }
                }
            }

            yield return Task.BCJDEKKIHDD.RemoveAllMarkers();
            if (!lost)
            {
                Plugin.Log.LogInfo($"Stellar evacuation: escaped; {saved}/{total} saved");
                yield return Task.OKLDNLKLLFJ.WithGroup("State_Group_Mus_Dyn").WithState("Explore");
                yield return Task.HHNFJKHGEPH.WithMissionResult(saved >= total ? $"Station evacuated: all {total} people saved"
                    : $"{saved} of {total} people saved", true);
                yield return Task.KNMMMJOOBHC.WithTime(10f);
            }
            yield return Task.KKALNAMJCNH.WithName(lost ? "lost" : "saved");
        }

        // The eruption: the blast at the star (or, without one, far off), and a minute to warp out.
        private IEnumerable<Step> Erupt(string status)
        {
            Plugin.Log.LogInfo("Stellar evacuation: eruption");
            yield return Task.ABHANLLBILL.WithEventID("Play_SFX_Exp_Star_Explosion_Initial_Boom_E3").ContinueImmediately();
            if (star != null) Stage("preCataclysm");
            else
            {
                var body = Find(Tag.StarEruption).FirstOrDefault() ?? Find(Tag.Star).FirstOrDefault();
                var blast = Objective.MNKGANNGIKK.WithPrefab(ExplosionPrefab).InSystem(system).AssignName("explosion").AddTags(Tag.Enemy);
                if (body != null) yield return Task.BFMBGDIDKEI.UsingSpawner(blast).AtBody(body).OutputTo(new BodyRef());
                else yield return Task.BFMBGDIDKEI.UsingSpawner(blast).AtAngleAndDistanceFromBody(station, random.Next(0, 360), 60000f).OutputTo(new BodyRef());
            }
            yield return Task.OKLDNLKLLFJ.WithGroup("State_Group_Mus_Dyn").WithState("Tension");
            yield return Task.CAJKAPALCCO.SendViewScreenMessage(ViewScreen.GCIOEGLLOLP.Warning, 5f, "Stellar eruption: warp out now");
            if (notice != null) yield return Task.NECMCDDOFJB.RemoveSubDirective(Directive, notice);
            notice = null;
            yield return Task.NECMCDDOFJB.RemoveSubDirective(Directive, status);
            yield return Task.NECMCDDOFJB.WithDescriptionString(Escape).WithTimer(FinalCountdown);
            yield return Task.NECMCDDOFJB.AddSubDirectives(Escape, status);
            if (star != null)
            {
                yield return Comms("PLAY_VO_E3CDD_E3RED_SCRIP_STAR_FINAL_001_01E3REDM_DRY_596");
                yield return Comms("PLAY_VO_E3CDD_E3RED_SCRIP_STAR_FINAL_001_04E3REDM_DRY_597");
            }
            else yield return Vo("PLAY_VO_CAMP_M02_OBJ02_SWARN_COUNT_60S_001_01ASCIO_DRY_1326");
        }

        // A stage of the E3 star's eruption animation (sent to every player by the game).
        private void Stage(string trigger)
        {
            if (star == null) return;
            try
            {
                HarmonyLib.AccessTools.Method(StarManagerType, "SetAnimTrigger").Invoke(star, new object[] { trigger });
                Plugin.Log.LogInfo($"Stellar evacuation: star stage {trigger}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Stellar evacuation: star stage {trigger}: {e.Message}");
            }
        }

        // The E3 demo's star: tagged Star and Enemy, with the StarManager that animates its eruption.
        private UnityEngine.Component CriticalStar(Map.SystemRoot where)
        {
            if (StarManagerType == null) return null;
            try
            {
                var bodies = new List<BodyRef>();
                Objective.FindAllBodyWithTags(bodies, new[] { Tag.Star });
                foreach (var body in bodies.Where(b => b?.AHMPFHDGMKJ?.ALPAOOKFIEE?.System == where))
                    if ((body.AHMPFHDGMKJ as Sector.SectorBody)?.gameObject.GetComponent(StarManagerType) is UnityEngine.Component manager && manager != null)
                        return manager;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Stellar evacuation: finding the star: " + e.Message);
            }
            return null;
        }

        private string Status(int aboard) =>
            $"Saved: {saved} of {total}" + (aboard > 0 ? $" ({aboard} aboard, take them to the transport)" : "");

        private Step Comms(string id) => Task.JJFBKBLIMCC.WithEventID(id).OnBridgeObject(BridgeObject.Comms_Officer_Mori).ContinueImmediately();

        private Step Vo(string id) => Task.JJFBKBLIMCC.WithEventID(id).OnBridgeObject(BridgeObject.Science_Officer_Velis).ContinueImmediately();

        // Replaces the notice line (null: just clears it).
        private IEnumerable<Step> Say(string text)
        {
            string under = erupting ? Escape : Directive;
            if (notice != null) yield return Task.NECMCDDOFJB.RemoveSubDirective(under, notice);
            notice = text;
            noticeUntil = UnityEngine.Time.time + 8f;
            if (text != null) yield return Task.NECMCDDOFJB.AddSubDirectives(under, text);
        }

        // ---- Helpers ----------------------------------------------------------------------------------

        private int Aboard() => People(player);

        // The transporter beams only from a body whose life signs or cargo are known, and a body that counts as
        // already scanned (as the pods and stations do) can't be scanned again to find them. So they're made
        // known (on every player's game: SetSubsystemKnown is the game's RPC). Only for a body with ship data
        // (GetTargetInfo): the captain's order checks read it once the transporter can lock on, and without
        // it they fail and take every crew order with them.
        private static void Beamable(BodyRef body)
        {
            try
            {
                var sector = body?.AHMPFHDGMKJ as Sector.SectorBody;
                var targetable = sector?.GetDetectable() as Sector.TargetableComponent;
                bool shipData = sector?.GetTargetInfo() != null;
                Plugin.Log.LogInfo($"Stellar evacuation: pod {sector?.GetType().Name}, ship data {shipData}, "
                                   + $"life signs {(targetable == null ? "-" : targetable.IsSubsystemKnown(Sector.NHGGGDMADKM.Life) ? "known" : "unknown")}, "
                                   + $"cargo {(targetable == null ? "-" : targetable.IsSubsystemKnown(Sector.NHGGGDMADKM.Cargo) ? "known" : "unknown")}");
                if (targetable == null || !shipData) return;
                foreach (var subsystem in new[] { Sector.NHGGGDMADKM.Life, Sector.NHGGGDMADKM.Cargo })
                {
                    targetable.AddTargetableSubsystem(subsystem);
                    if (!targetable.IsSubsystemKnown(subsystem)) targetable.SetSubsystemKnown(subsystem, true);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Stellar evacuation: making the pod beamable: " + e.Message);
            }
        }

        // People onto a body: straight into its cargo bay, adding one if it has none (the game's own cargo task
        // does nothing without one), and shared with the crew's games (Update, "EVC").
        private static void PutPeople(BodyRef body, int count)
        {
            try
            {
                if (!(body?.AHMPFHDGMKJ is Sector.SectorBody sector))
                {
                    Plugin.Log.LogWarning("Stellar evacuation: the station isn't a body people can be put on");
                    return;
                }
                bool had = sector.GetCargo() != null;
                SetPeople(sector, count);
                Plugin.Log.LogInfo($"Stellar evacuation: {count} people on {sector.name} ({(had ? "its cargo bay" : "a cargo bay added")}); reads back {People(body)}");
                var room = Patches.PhotonNetworkType?.GetMethod("get_room", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.Invoke(null, null);
                if (room != null) PhotonProps.Set(room, RoomKey, $"{sector.FLOBHLBHLLJ}:{count}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Stellar evacuation: putting people aboard: " + e.Message);
            }
        }

        private static void SetPeople(Sector.SectorBody sector, int count)
        {
            var cargo = sector.GetCargo() ?? sector.AddCargo();
            cargo.LEDFEFEFFAO.Set(count, Sector.CargoTypes.People);
            cargo.DMPFGBPDOLN.Set(count, Sector.CargoTypes.People);
        }

        internal const string RoomKey = "EVC";
        private static string applied;
        private static float nextCheck;

        /// <summary>Every player: put the host's latest group on the station here too (its cargo bay may be ours alone).</summary>
        internal static void Update()
        {
            if (UnityEngine.Time.unscaledTime < nextCheck) return;
            nextCheck = UnityEngine.Time.unscaledTime + 1f;
            var photon = Patches.PhotonNetworkType;
            var room = photon?.GetMethod("get_room", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.Invoke(null, null);
            if (room == null || photon.GetMethod("get_isMasterClient", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.Invoke(null, null) is true) return;
            var props = room.GetType().GetMethod("get_customProperties")?.Invoke(room, null) as System.Collections.IDictionary;
            var value = props != null && props.Contains(RoomKey) ? props[RoomKey] as string : null;
            if (string.IsNullOrEmpty(value) || value == applied) return;
            var parts = value.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out var count)) return;
            var sector = UnityEngine.Object.FindObjectsOfType<Sector.SectorBody>().FirstOrDefault(b => b.FLOBHLBHLLJ.ToString() == parts[0]);
            if (sector == null) return; // not loaded yet: try again in a second
            SetPeople(sector, count);
            applied = value;
            Plugin.Log.LogInfo($"Stellar evacuation: {count} people on {sector.name} (from the host)");
        }

        private static bool Live(BodyRef body) => body?.AHMPFHDGMKJ is Sector.SectorBody sector && !(sector is Sector.AtticSectorBody);

        // A loaded station near the ship: the nearest real body tagged SpaceStation or named like a station.
        private Sector.SectorBody LiveStation()
        {
            try
            {
                var me = player.AHMPFHDGMKJ.ALPAOOKFIEE;
                var near = UnityEngine.Object.FindObjectsOfType<Sector.SectorBody>()
                    .Where(b => b != null && !(b is Sector.AtticSectorBody) && b.ALPAOOKFIEE != null && b.ALPAOOKFIEE != me
                                && b.ALPAOOKFIEE.System == system && b.ALPAOOKFIEE.ContainerTransform == me.ContainerTransform)
                    .Select(b => new { Body = b, Km = UnityEngine.Vector3d.Distance(b.ALPAOOKFIEE.Position, me.Position) / 1000 })
                    .Where(x => x.Km < 200).OrderBy(x => x.Km).ToList();
                foreach (var x in near.Take(25))
                    Plugin.Log.LogInfo($"Stellar evacuation: nearby '{x.Body.name}' / '{x.Body.ALPAOOKFIEE.BaseName}' ({x.Body.GetType().Name}), {x.Km:0} km"
                                       + $"{(SafeHasTag(x.Body.ALPAOOKFIEE, Tag.SpaceStation) ? ", SpaceStation" : "")}{(x.Body.GetCargo() != null ? ", cargo bay" : "")}");
                return near.Select(x => x.Body).FirstOrDefault(b => SafeHasTag(b.ALPAOOKFIEE, Tag.SpaceStation)
                                                                    || LooksLikeStation(b.name) || LooksLikeStation(b.ALPAOOKFIEE.BaseName));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Stellar evacuation: finding the station: " + e.Message);
                return null;
            }
        }

        private static bool SafeHasTag(SystemTransform t, Tag tag)
        {
            try { return t.HasTag(tag); }
            catch { return false; }
        }

        // The station: tagged SpaceStation, else a body named like one; null if neither.
        private BodyRef FindStation()
        {
            var tagged = Find(Tag.SpaceStation);
            if (tagged.Count > 0) return tagged[random.Next(tagged.Count)];
            var named = AllBodies().Where(b => LooksLikeStation(b.AHMPFHDGMKJ.ALPAOOKFIEE.BaseName) && !Named(b, "quarantine") && b.AHMPFHDGMKJ is Sector.SectorBody).ToList();
            return named.Count > 0 ? named[random.Next(named.Count)] : null;
        }

        private static bool Named(BodyRef body, string word)
        {
            var name = body?.AHMPFHDGMKJ?.ALPAOOKFIEE?.BaseName;
            return new[] { name, Localized(name) }.Any(text => !string.IsNullOrEmpty(text) && text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool LooksLikeStation(string name)
        {
            foreach (var text in new[] { name, Localized(name) })
                if (!string.IsNullOrEmpty(text) && new[] { "station", "starbase", "outpost", "depot", "colony", "base" }
                        .Any(word => text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0))
                    return true;
            return false;
        }

        private static readonly Tag[] DumpTags =
        {
            Tag.SpaceStation, Tag.RegionSpaceStation, Tag.POI, Tag.REGION, Tag.ImpulseDestination, Tag.WarpDestination,
            Tag.SpawnPoint, Tag.Planet, Tag.Moon, Tag.Star, Tag.StarEruption, Tag.Satellite, Tag.Ship, Tag.Neutral,
            Tag.Friendly, Tag.Enemy, Tag.Buoy, Tag.Asteroid, Tag.Anomaly, Tag.Nebula, Tag.Mission,
        };

        // Every body of the system found by any of DumpTags.
        private List<BodyRef> AllBodies()
        {
            var all = new List<BodyRef>();
            foreach (var tag in DumpTags)
                foreach (var body in Find(tag))
                    if (!all.Any(o => o.AHMPFHDGMKJ == body.AHMPFHDGMKJ)) all.Add(body);
            return all;
        }

        // What's in the system, for finding stations and the like on maps we don't know.
        private void DumpBodies()
        {
            try
            {
                foreach (var body in AllBodies().Where(b => !SafeHasTag(b.AHMPFHDGMKJ.ALPAOOKFIEE, Tag.SpawnPoint)))
                {
                    var t = body.AHMPFHDGMKJ.ALPAOOKFIEE;
                    var tags = DumpTags.Where(tag => { try { return t.HasTag(tag); } catch { return false; } }).Select(tag => tag.ToString()).ToArray();
                    var sector = body.AHMPFHDGMKJ as Sector.SectorBody;
                    Plugin.Log.LogInfo($"Stellar evacuation: body '{t.BaseName}' ({Localized(t.BaseName)}), {Distance(player, body) / 1000:0} km: "
                                       + $"{string.Join(" ", tags)}{(sector == null ? " [not a sector body]" : $" [{sector.GetType().Name}{(sector.GetCargo() != null ? ", cargo bay" : "")}]")}");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Stellar evacuation: listing bodies: " + e.Message);
            }
        }

        private static int People(BodyRef body)
        {
            try { return body?.AHMPFHDGMKJ?.GetCargo()?.LEDFEFEFFAO?.GetNumItemsOfType(Sector.CargoTypes.People) ?? 0; }
            catch { return 0; }
        }

        // Bodies with the tag in the evacuation system.
        private List<BodyRef> Find(Tag tag)
        {
            var found = new List<BodyRef>();
            try
            {
                Objective.FindAllBodyWithTags(found, new[] { tag });
                return found.Where(b => b?.AHMPFHDGMKJ?.ALPAOOKFIEE is var t && t != null && t.System == system).ToList();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Stellar evacuation: finding {tag}: {e.Message}");
                return new List<BodyRef>();
            }
        }

        // Perseph, if the map has it (the E3 sector). Otherwise a system with a space station, at random:
        // preferably one whose star can erupt, and not this one.
        private Map.SystemRoot PickSystem()
        {
            var here = Objective.KALBLMGFNIN;
            try
            {
                var all = (Objective.MLHNDAEKLHO ?? new List<Map.SystemRoot>()).Where(x => x != null).ToList();
                var stars = all.Where(x => CriticalStar(x) != null).ToList();
                Plugin.Log.LogInfo($"Stellar evacuation: systems {string.Join(", ", all.Select(SystemName).ToArray())}; "
                                   + $"the E3 star in {(stars.Count == 0 ? "none" : string.Join(", ", stars.Select(SystemName).ToArray()))}");
                var home = all.FirstOrDefault(x => (x.SystemName ?? "").IndexOf(Home, StringComparison.OrdinalIgnoreCase) >= 0);
                if (home != null) return home;
                if (stars.Count > 0) return stars[0];
                List<Map.SystemRoot> With(Tag tag)
                {
                    var bodies = new List<BodyRef>();
                    Objective.FindAllBodyWithTags(bodies, new[] { tag });
                    return bodies.Select(b => b?.AHMPFHDGMKJ?.ALPAOOKFIEE?.System).Where(s => s != null).Distinct().ToList();
                }
                var stations = With(Tag.SpaceStation);
                var erupting = With(Tag.StarEruption);
                Plugin.Log.LogInfo($"Stellar evacuation: stations in {string.Join(", ", stations.Select(SystemName).ToArray())}; "
                                   + $"erupting stars in {(erupting.Count == 0 ? "none" : string.Join(", ", erupting.Select(SystemName).ToArray()))}");
                foreach (var pool in new[] { stations.Where(s => erupting.Contains(s) && s != here), stations.Where(s => s != here), stations })
                {
                    var list = pool.ToList();
                    if (list.Count > 0) return list[random.Next(list.Count)];
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Stellar evacuation: picking a system: " + e.Message);
            }
            return here;
        }

        private static double Distance(BodyRef a, BodyRef b)
        {
            try { return UnityEngine.Vector3d.Distance(a.AHMPFHDGMKJ.ALPAOOKFIEE.Position, b.AHMPFHDGMKJ.ALPAOOKFIEE.Position); }
            catch { return double.MaxValue; }
        }

        private static string Localized(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            try { return SVR.Localization.GetLocalizedText(text); }
            catch { return text; }
        }

        private static string SystemName(Map.SystemRoot system) => system == null ? "this" : Localized(system.SystemName);

        private Spawner MakeSpawner(string prefab, string role, Tag allegiance) =>
            Objective.MNKGANNGIKK.WithPrefab(prefab).InSystem(Objective.KALBLMGFNIN)
                .AssignName($"{role} {++spawnCount}")
                .AddTags(allegiance, Tag.Ship);
    }
}
