# Star Trek: Bridge Crew mission catalogue (for designing Revival missions)

What each of the game's own missions does, and what it uses, for designing Revival missions. Names are the readable ones from `names.map` (generate `decompiled/` with `dotnet run --project tools/xref -- dump` to read the code); the mod's code must use the scrambled names on the left of `names.map`. See MISSIONS.md for how missions work and how to write one. Sources: decompiled/Mission.*, BorgVariants, Mission.Tables.

## How the game builds a mission
- **Sequence** (`MissionSequence`): `CreateSequence()` makes nodes with `MakeNode<Objective>()` and wires them with `OnSucceed(x).RunObjective(y)`, `RunObjectivesInParalell(a,b,c)`, `OnFail(x).MissionFails()` and `MissionSucceeds()`. `StartingSystemSelection` picks the start system: random for procedural missions, fixed by tag for the campaign (Donatu, Edinov, Tyris, Devin, DS-4219, Elgar, Gamma Hydra for the Kobayashi Maru). `MakeContract()` returns the nested `Contract`, a class of static `"id|text"` localisation strings.
- **Objective** (`KLGBKLNANOO<Contract>` for procedural missions, plain `MissionObjective` for the campaign): `StartScript()` calls `Objective.AddStep(name, tasks...)` many times. Step names are string fields set in the .ctor (`_stepXxx` in the map; `EKJFIMNCAEN` = "stepOne" is the entry step everywhere). Coroutine helpers are `IEnumerator<TaskStep>` methods, now named after their compiler iterator classes (DespawnEnemies, GetStationRef, PlaySuccessMusic...).
- **Random VO**: each line is a field holding `"PLAY_AI_<SPEAKER>_<CODE>_RND"` plus a string[] of variants (`_voX` / `_voX_Lines`). StartScript replaces the field with `Lines[Objective.Random.Next(...)]`. Speakers: ACOMS = Comms (Mori), ASCIO = Science (Velis), CAPTF = female captain's log, CSURI = Stillwater (Suribachi), CRAL = Salvok (Raleigh), CHUX = Ukwu (Huxley), ICAP1-4 = independent captains, SCHEE = FSC officer Scheer. Campaign and DLC lines are `PLAY_VO_CAMP_Mxx_...` and `PLAY_VO_DLC_PROC_<MODE>_...`.
- **Procedural spawns are data, not code.** Objectives launch `Encounter.<Name>` spawn tables (`Task.LaunchEncounterSpawnTable.UsingEncounter`). The ships come from SpawnGroup assets: `SpawnGroupEntry` (SectorBodyRef, Configurators, Tags, SpawnMode, NearOtherType, FacingType, Angle, Distance) plus `SpawnGroup.FormationType` (None or Wedge), weighted in SpawnGroupTable. Ongoing Voyage chooses a mission with MissionTable, then ObjectiveTable (ObjectiveScript type plus EncounterContractSettings). `Mission.Procedural` itself holds only SpawnConfigurators: Config_AddCargo_1-3, Config_AddPeopleCargo_1-6/18, Config_EmptyPeopleCargo, Config_EmptyTribblesCargo, Config_DisableShip. The campaign (and Borg-mode challenges) spawn prefabs directly with `BodySpawner.WithPrefab("Sector/...")`.
- **Shared intro pattern** (every procedural mission): steps "camera sequence", "camera angle", "cancel camera" and "hail". They play `Play_CLOG_Sequence_On`, show an external view at (0,7,8) and then (0,7,-60) with a fade, and play a random captain's log (`Encounter.VO_CaptainsLog`, Captain_Female). PlayerButtonPressSkip skips it, and `Play_Switch_Music_Dynamic_PT02` starts the dynamic music. Next come HailBridge, `Play_CON_Aeg_Gen_Viewscreen_Hail_On/Off`, a talking head or waveform on the viewscreen, the directive with a system name and sub-directives, and finally PlayerInSystemTrigger(SystemB).
- **Shared music**: switch group `State_Group_Mus_Dyn`, states `Explore`/`Exploration`, `Moderate_Tension` and `Tension`. The success stinger is `Play_Mus_Mission_01_StormySea_Federation_CallToArms` (PlaySuccessMusic).
- **Shared ending pattern** ("final fight"/"return" objectives): reacquire the station, Ambush/NoAmbush, PlayerInRangeNotDetected, FinalFight, then ResetVictoryCheck. `PlayerMinimumHealth` keeps the player alive during cutscenes, `MissionText.WithMissionResult` records each result line, and Romulan_Admiral or the ally signs off.

## Ongoing Voyage (procedural)

### Defend (Defend_Sequence1): "Defend Ally Ships"
- Flow: DefendIntro (Stillwater hails, warp to SystemB) → DefendSpawnAllies (Ally1 Suribachi, Ally2 Raleigh, optional WelcomingCommittee) → in parallel, DefendSuribachiBattle + DefendSuribachiHealth → in parallel, DefendRaleighBattle + DefendRaleighHealth → DefendFinalBattle.
- Objectives: protect the Suribachi from EnemyFleet1, move to RegionB/C to save the Raleigh from EnemyFleet2, then a final battle against EnemyFleet3 plus an `EnemyDecloak` group and timed `EnemyReinforcements`. A Klingon flagship captain hails before it.
- Mechanics: HealthTrigger on the allies fails the objective, `Warping.CannotExitWarp` locks the player into the fight, and the allies get AIPatrol/AIMoveTo/AIConfigureTether, AIVulnerable and SetStats. DestroyDirective(Progress)Trigger counts kills, and EnemyDetectedPlayerTrigger switches the music to Tension.
- VO: CSURI/CRAL hails, ACOMS ally-health callouts (SUR50/25/10/00, RLG50...), Klingon_Flagship_Capt taunt. Music: Explore → Moderate_Tension → Tension → CallToArms.
- Ends: fails if the Raleigh battle or the final battle fails. Results: Protect Suribachi/Raleigh, Eliminate hostiles, Survive.

### Defend2 (Defend2_Sequence1): "Defend the Disabled Suribachi"
- Flow: Defend2Intro → Defend2Setup (spawns a disabled Suribachi with AIDisable, the EnemyFleet and the WelcomingCommittee) → Defend2ProtectSuribachi → Defend2Outro.
- Survive while the Suribachi effects repairs (a random 240-600 s timer). Ally reinforcements (Raleigh, Huxley via `Encounter.AllyFleet`) arrive after 180-210 s. Enemy reinforcements run on a timer or progress trigger. Enemies flee if they have not detected you, and a Klingon hails (VO_EnemyTaunt1/2).
- Mechanics: per-ship health monitors with 50/25/10/0 % VO, "suribachi abandon ship" failure, "early failure", RevealAllSubsystems on the allies, a DespawnEnemies coroutine, and a 5 % "Darmok" fan line (bb DefendDarmokChance).
- Ends: thanks, then each ally warps out (AIDespawn after Warping), and PlaySuccessMusic.

### Rescue (Rescue_Sequence1): "Unidentified distress source"
- Flow: RescueIntro (distress signal, independent captain hail loop) → in parallel: RescueMain, RescueLocateBuoys, RescueRandomEnemies, RescueAlarmBeacons, RescueDyingStar → after RescueMain, RescueReturnSurvivors.
- Main: find and scan buoys A-C to triangulate the source (objLocate*), find the RescueTarget (sometimes empty), scan it, lower its shields, and transport survivors (objCollectSurvivors). A Klingon **harvester** may try to beam survivors first; scan it, damage it, and stop it escaping (MonitorHarvesterTransport). Klingons and reinforcements arrive on a timer.
- Hazards: alarm beacons A-F call RandomEnemyA-C when they detect the Aegis (AIConfigureAlarm). The **dying star** (Tag.StarEruption) warns at a random time, deactivates warp to SystemB and counts down 60 s (`PLAY_VO_CAMP_M02_OBJ02_SWARN_*`), then spawns `Mission 02 Prefabs/StarExplosion`. You must warp out with cargo; failure ends the mission.
- Return: the station at SystemC, then a KlingonAmbush or KlingonFinalFight; Romulan_Admiral signs off. Results: Beam, Deliver, Civilians rescued.

### Rescue2 (Rescue2_Sequence)
- Rescue2Intro → Rescue2Setup (DebrisField with DebrisA-D, RescueTarget, RandomEnemyA/B, WelcomeCommittee) → Rescue2RescuePods → Rescue2ReturnSurvivors.
- Rescue2RescuePods: lifepods A/B (some empty), KlingonHarvesters racing for them (HarvesterBranch), a star eruption (the same StarExplosion pattern), "transport from station", Klingons leave and warp out. Fails if pods are lost or you abort early.
- Return as in Rescue (FinalFightBranch, KlingonFinalAmbush, Independent_Capt_2 thanks).

### Recover (Recover_Sequence1): "Locate and retrieve a fugitive"
- Flow: RecoverIntro → in parallel RecoverSetup + RecoverAlarmBeacons + RecoverDyingStar → RecoverFindFugitive → RecoverReturnFugitive.
- Setup spawns the FugitiveShip (one of three variants) among decoys A/B, pirates PirateEscortA-C, NeutralGroupA/B and a KlingonPatrol. A contact hails, and the contact may leave or be destroyed. There is a friendly-fire check (CheckForDamageSource), and an indie captain (Independent_Capt_3) helps.
- Find: scan decoys and the target (ScanDirectiveTargets) until you find the fugitive. It flees up to three times (range or time triggers) and turns vulnerable within range or over time. Beam the fugitive aboard (objBeam, TransportTrigger).
- Return: objReturn, ambush or final fight (Klingon_Captain_2 taunts), spawns `Ship - Science B` as an ally. Dying star: warp-cancel unless cargo > 0.

### Recover2 (Recover2_Sequence): unfinished prototype
- Recover2Intro (stepOne only) → Recover2Setup (FugitiveShip, EnemyPatrolA/B, SpaceStation, welcome) → Recover2Monitor (CollectCargo success/failure) → Recover2Final (EnemyFleet and AllyFleet at the station). Almost no VO.

### Research (Research_Sequence1): "Investigate anomalies"
- Flow: ResearchIntro → ResearchSpawnPatrols → in parallel: ResearchScanAnomalies (main), ResearchDistressCall, ResearchPirateEncounter, ResearchAlarmBeacons, ResearchDyingStar → ResearchReturnData.
- Main: scan 7 anomalies in SystemB/SystemC (objScanAnomalies1-7, CapScanThreshold). Science reports after the 1st, 2nd and 3rd scans, and each report calls in EnemyPatrolA/B.
- Side quest: a distress call (RescueShipA under RescueAttackersA). PickRescueEvent chooses RescueTypeA-D, and the target turns vulnerable by range or over time. Pirates either catfight with neutrals or make a flyby attack.
- Return: transmit the data at the station (TransmitData), then an ambush (AmbushPlayerB/C) or a final fight.

### Intercept (Intercept1_SEQUENCE): "Intercept the raiders"
- Flow: InterceptIntro (warp-in effect `Mission03_WarpInEffect`, bb AttackIterator) → in parallel InterceptAttackTracker (reruns itself when it fails) + InterceptEnemyEscape → InterceptResolve.
- The raiders hit space stations in SystemA-E in turn. The tracker gives a travel directive to the attacked system and POI (bb currentSystem, currentRegion, spacestation) and replaces the station with `SpaceStationX_destroyed`. Then SpawnRescueTargets spawns escape pods; collect them with CollectCargoDirective.
- Per-site objectives InterceptAttackSiteB-E (copies for each system, not referenced from code): response timer (bb responseTimer, RecordPlayerResponseTime), Hail1, ArriveOnSite, Battle or Aftermath, RemoveLifeForms if you are late, an alarm ship (SetupAlarmShip with AIConfigureAlarm), EnemyReinforcements, and a PirateBranch.
- Ends: AllyHail from Stillwater, AttackersDestroyed. Fails if the enemies escape after you leave early.

### Explore (Explore_SEQUENCE): the patrol mode with random events
- ExploreIntro, then in parallel: ExploreMainPatrol (succeeds the mission), ExplorePassiveRandomizer, ExploreScannables, ExploreTraffic, ExploreTravelTracker. ExploreMainPatrol uses `Task.ObjectiveScript.RunObjective<T>("name")` to start ExploreActiveRandomizer, which starts one *active* event at a time. Events take turns on the hail channel through a lock (bb "ExploreHail", ReleaseExploreHail).
- Active events: **Identify** (scan groups A-C of cargo ships, transponders, fugitives or asteroids to find the target, then return the data to a science vessel); **CollectData** (scan anomalies for FSC Scheer while Huxley hails; return to stations C/D/E; full or near completion and ImpressScheer); **DistressCall** (signal source, escape pods, immediate or delayed ambush); **Escort** (Suribachi, Huxley or a science ship through waypoints A-C with ambushes; the contact warps out on success or failure); **Particles** (harvest particles at a long-range detectable POI).
- Passive events: **AllyEncounter** (Raleigh patrol or transport attacked, with ReadyTransport); **Ambush** (ambush1EnemyFleet, taunt, move-to or patrol behaviour, reinforcements, retreat VO); **InvincibleForce** (Borg Cube hails, regenerates and is near-invulnerable; flee by warp; uses `Ship - Borg Cube` with BorgPhaseExplore); **EnergyWave** (`Sector/System/EnergyWaveFX` sweeps the system; escape by impulse or warp, or take DamagePhaser/Engine/ShieldRouteNode, OverloadSubsystems and damage to other ships; CaptainIntel warnings).
- ExploreScannables: `Prop - ScannableObjectA-D` give Crystal, Fragment, Graviton and Verteron results. ExploreTraffic spawns Pirate A/B, WarpDriven A-D and Science A-C traffic on warp or impulse. ExploreSurveyPatrol (unreferenced) is an older main patrol with long-range scans and `Prop - NavPoint Explore` survey points, reporting to Admiral Woodward.
- Music: Exploration, plus Moderate_Tension or Tension during events. VO: `PLAY_VO_DLC_PROC_EXPLR_*` (INDY1-4, SCHEE, SURIB lines).

### Borg mode (Borg_Sequence, DLC)
- BorgIntro → in parallel BorgChallengeRunner + BorgCubeController + BorgScoreKeeper → BorgOutro once the runner succeeds.
- Runner: warp through SystemB → C → D. In each system, PickChallenges takes 3 of 6 challenges, run at one of two regions (region pickers). The challenges (each with an indie captain intro and outro, FailIfShipIsDestroyed, and WarpStateTriggersSuccess/Failure):
  - **BombShip**: find a haywire prototype (`Freighter D Cargo` + `Mission 00 Prefabs/TerroristBomb`), with 60 s and 30 s containment countdowns; drop its shields and transport it.
  - **BossBattle**: bossBattle spawn table, Romulan or Klingon talking head, save a distressed ship as a sub-objective.
  - **ProtectConvoy**: convoyFleet against convoyPirateHunter; the convoy warps out.
  - **PirateArtifact**: a pirate (Pirate A/B) has stolen an artifact; it surrenders or drops cargo (`WarpDriven B CARGO`, `EscapePodFull CARGO`), with an optional rescue.
  - **PlanetKiller**: `Ship - Planet Killer` with full escape pods and a long-range transport; "noble sacrifice" is allowed.
  - **ProtectAllies**: Huxley, Suribachi and Raleigh (or their TNG variants, `Ship - * TNG Cargo`) fly set paths against D5, BoP and Romulan Scout/Medium/DDeridex waves, then hand over data by transport.
- Cube controller: a Borg teaser encounter, spawn timers after the 1st and 2nd warps, three cube spawns plus a final one with HP-phase controllers. `BorgVariants.BorgPhase0-3` set MaxHealth to 500/1000/1500/2000 with regeneration 50/s after 10 s; BorgPhaseExplore sets 4000 with no regeneration. Mechanics: AIBorgSetRegeneration, BorgRepairEffects.PowerDown/PowerUp, the `PlayerShipEffects.PlayEmpWave` EMP pulse, BorgInfection.Start/Clear (the nanites challenge), `Warping.UseAlternateWarpLocations`, and the music `Play_Mus_DLC_Borg_First_Encounter` / `Play_Mus_DLC_Borg_Approaching`.
- Score keeper: per-challenge warp, success, failure and sub-objective recorders, BorgKilledByPlayer, bb "borg score", and a bridge multiplier message.

### HunterTest (Test_Sequence): developer wave-survival test
- HunterWaves: Wave1-3 enemy spawn tables (RandomizeSpawnTable). A warp destination picker moves the fight between SystemA-C, ApplyRandomDamageToAegis damages the player's power graph, and a score is kept (bb finalScore, waveWipeScore, movementScore).

### ProtoRescueMission: prototype
- One objective that launches RescueTableA-C in MissionSystem and checks CollectCargo. Has its own AddPeopleCargo configurator.

## Campaign

### M0 "Shakedown" (Donatu; M0Sequence)
- M0Intro (Woodward talking head) → M0PatrolScanRagusa (patrol, scan incoming Freighter E traffic for smuggled wildlife, scan the Ragusa; teaches warp orders) → M0RescueCrew (scan a freighter for life, transport survivors) → M0Nicholson (impulse to Section VI, scan Plasma Coolant A-C to triangulate the USS Nicholson, beam survivors, destroy the ship for its `Mission00_WarpCoreBreach` and reach 20 km; render aid to the Beltane by scan, then System Intrusion to disrupt its engines; avoid `Prop - GraviticMine` (5 km activation)) → M0KobayashiMaru → M0Debrief.
- Prefabs: Freighter A/B/E, `Mission00_PlasmaCoolant_01/02`, `Mission00_WarpCoreBreach`, `Prop - BeaconBuoy`, GraviticMine. Uses AIHackedTrigger, CaptainIntel and TransportTrigger.

### Kobayashi Maru (M0KobayashiMaru; also KMSequence → KMDebrief)
- "IN CONFLICT": rescue the `Ship - Kobayashi Maru` without violating the Klingon Neutral Zone (MonitorNeutralZoneApproach and MonitorNeutralZoneCross). Klingon "roulette" spawns (FillRouletteDistances 5-30 km, FillRouletteAngles in 45° steps) and Waves 01-04 decloak (`SpawnBodyFromCloak`: D5, BoP, Warbird). The KM is destroyed if you dawdle in Section I/VI/X; beacon buoys "check left/right". Music `Play_Mus_Mission_00_Shakedown_Tension_Strings`. The no-win scenario ends with Woodward's debrief on both success and failure.

### M1 "Stormy Sea" (Edinov; M1Sequence)
- M1Intro (captain's log; escort the Huxley, Captain Ukwu, to Silva) → M1SilvaInvestigation (scan Silva IV and its moon long-range; an asteroid-field distress beacon; a booby-trapped `Mission01_ExplodingBarrel_Copy` near the Huxley; launch a probe into a nebula (`Mission_01_AnomalyCloudSpherical_Alt4_Lightning`) to find a disabled `Class J` ship, then scan and hack it; Pirate A/B attack) → M1EscortHuxleyHome.
- Music: the StormySea suite (Introduction, Exploration variants, PirateBattle, Federation_Motif, CallToArms). Uses AIHackedTrigger, AIConfigureDisengage and bb "Huxley Health".

### M2 "Sun and Shield" (Tyris; M2Sequence)
- M2Intro (Salvok + Klingon talking heads) → M2RescuePolaris (the Polaris, a `Science C`, must be rescued before an asteroid collision: disrupt its engines, render aid to a research station; Raleigh present) → M2StellarEruption (locate escape pods (randomised full/empty) near an erupting star; `AnomalyNoVisual_90km` heat zone damages an unshielded hull; Klingon D5s decloak and later warp out; escape before the stellar core eruption (`StarExplosion`); "Civilians Recovered: {0}").
- Unused objectives: M2RendezvousRaleigh, M2WarpToStar. Music: PLAY_MUS_MISSION_02_SUNANDSHIELD_*.

### M3 (Devin → Arna; M3Sequence)
- M3Intro (captain's log flypath, civilian Science A/B) → M3InfiltrateNetwork: a stealth mission. Hack `Sector/Klingon_Sensor` network nodes (sensor B within or beyond 70 km), avoid detection (CheckDetection, SensorTripped), and gather intel in the Trench with probes (`Prop - Probe_Federation`, `Mission03_ProbeTarget`). Klingon patrols spawn on a shrinking timer (480 → 300 s; "Time until next Klingon Patrol"). Alarm-variant ships (`D5 Cruiser Alarm`, `Bird Of Prey Alarm`), a Warbird reveal, `Mission03_WarpInEffect`, `Mission03_ProtoMatterExplosion`. Uses AIConfigureAlarm, AILastKnownLocation, AIDetectionTrigger and SpawnBodyFromCloak.

### M4 "Survival" (DS-4219; M4Sequence)
- M4Intro (captain's log with visual bridge damage and initial Aegis damage via PlayerPowerGraph) → M4EscapeDS4219: fight through enemy spawn sets 01-05 and blocking forces at POIs Alpha-Epsilon. Scouts, BoPs and a "nemesis" Warbird respawn and hunt with AI-cheat search ranges. Rescue civilians from the Herodotus (`Freighter C`); impulse-jump counting; random Aegis damage; Warbird pauses; a looping alarm sound → M4SaveSuribachi (destroy the Klingon Warbird attacking the Suribachi (`Ship - Saladin`); ambient crew voices cheer).
- Music: PLAY_MUS_MISSION_04_SURVIVAL_* (WITHINTHENEBULA, WARBIRD, SURIBACHIARRIVES...).

### M5 "Fleet Battle" (Elgar → Toth; M5Sequence)
- M5Intro (allied fleet Huxley, Suribachi and Raleigh; refs stored on the blackboard) → M5WarpToToth ("Remove threat of protomatter weapon") → M5FleetBattle (skirmishers A-C, answer a hail, main battle groups A-F of D5/BoP/Warbird; allies forced into the area of operations, then given targets; flagship discovered and pursued) → M5Flagship (Klingon Flagship and D5 escorts with tethers; capture the protomatter weapon by transport, escape to the nebula edge, `Mission05_ProtomatterExplosion` when at a safe distance; ending cinematic).
- Unused: M5EscapeExplosion, M5WarpToElgar. Music: PLAY_MUS_MISSION_05_FLEETBATTLE_* (TroubleLooms, RaceAgainstTime, ValiantStandTogether...).

### FP1 / E3 / Intel demo (show builds)
- FP1: FP1ApproachHuxley (asteroid cluster, approach to teleport range) → FP1RescueHuxleyCrew (transport while Klingons attack) → FP1Ambush (AmbushWing).
- E3: E3WarpToStar → E3RescueSurvivors (research station Elo 4: scan and transport three groups of escape pods while a star goes critical (skybox and sun-intensity changes, `StarCataclysm`/`StarEvent1/2`); D5 and Warbird; "Survivors rescued: x/y").
- IntelDemoSequence: one objective per station (Engineering, Helm, Tactical, Captain) through `StationSwitchManager` (StartHelm(60), SetPhaserMaximum, TargetClosestEnemy, PrepareWarp/Warp).

## Reusable mechanics and assets the Revival missions (LastStand, FleetBattle, Patrol, StellarEvacuation) don't use yet
The mod already uses Spawn, Directives, PlayVoice/PlaySound, MusicState (Explore, Moderate_Tension, Tension), CargoTransfer, WaitForSubsystem, AIObjectives, AIShields, AIVulnerable, Warping, ExternalView, ViewScreenMessage, StarExplosion, Borg Cube + BorgPhaseExplore, and cloak alarm sounds. Not used yet:
1. **NPC hails with faces**: `Task.HailBridge` + `PreloadTalkingHead` + `ToggleViewscreenTalkingHead.Show(true, NPCHailingManager.<Face>)` (FedCaptainStillwater/Ukwu/Salvok, FedAdmiralWoodward, KlingonCaptainRegular/Flagship, RomulanCaptain, Borg), or `ToggleViewscreenWaveform` for voice-only, bracketed by `Play_CON_Aeg_Gen_Viewscreen_Hail_On/Off`.
2. **Captain's-log intro**: external-view camera cuts, fade, `Encounter.VO_CaptainsLog` random line, PlayerButtonPressSkip, `Play_Switch_Music_Dynamic_PT02`.
3. **Triggers**: HealthTrigger (ally HP thresholds with 50/25/10/0 % comms VO), RangeTrigger, TraverseStateTrigger (warp/impulse states), PlayerInSystemTrigger, WarpStateTrigger, EnemyDetectedPlayerTrigger / AIDetectionTrigger (stealth), TransportTrigger, AIHackedTrigger (System Intrusion objectives), Signal.Broadcast/Receive between objectives.
4. **Scan and collect directives**: ScanDirectiveTargets / ScanDirectiveProgressTrigger (scan N anomalies, buoys or decoys), CollectCargoDirectiveTargets (escape pods), DestroyDirectiveProgressTrigger, CaptainIntel.AddCaptainIntel, MissionText.WithMissionRatioStat/PercentageStat.
5. **AI behaviours**: AIPatrol and AIMoveTo (escort paths, ally patrols), AIConfigureTether, AIAggro, AIConfigureAlarm (alarm buoys and alarm ships that summon reinforcements), AILastKnownLocation, AIConfigureDisengage, `SpawnBodyFromCloak` (decloak ambushes), AITeam, AIHail.
6. **Hazards**: the dying-star countdown (Tag.StarEruption, `PLAY_VO_CAMP_M02_OBJ02_SWARN_*`, WarpDestination.DeactivateSystems), the heat zone `AnomalyNoVisual_90km`, the energy wave `Sector/System/EnergyWaveFX` with PlayerPowerGraph.Damage*RouteNode, `Prop - GraviticMine`, `Mission00_WarpCoreBreach`, `Mission05_ProtomatterExplosion`, the nebula cloud `Mission_01_AnomalyCloudSpherical_Alt4_Lightning`, the exploding barrel `Mission01_ExplodingBarrel_Copy`.
7. **Borg kit**: BorgPhase0-3 configurators (500-2000 HP with regeneration), AIBorgSetRegeneration, BorgRepairEffects.PowerDown/Up, the PlayerShipEffects.PlayEmpWave EMP, BorgInfection, `Play_Mus_DLC_Borg_*`.
8. **AI variants**: KlingonVariants (BirdOfPreyAmbusher/Fast/Full/Shark/Slow/Strafer, D5Full/Long/Lure/Shark/Short/Strafer, WarbirdBoss/Long/Short/Slugger/Strafer/Swarmer/Tanker) and RomulanVariants (DeridexBase/CloakFlee/Full, Flagship, ScoutFull/MineLayer/Sniper), each a SpawnConfigurator that loads "AI Attack Patterns/..." and "AI Disengage Patterns/..." resources (e.g. "Romulan Scout - Mine Layer", "D5 - Bait Disengage").
9. **Prefabs not yet used**: `Ship - Planet Killer`, `Ship - Kobayashi Maru`, `Ship - Class J`, `Ship - Science A/B/C/D`, `Ship - WarpDriven A-D` (+ `CARGO`), `Ship - Freighter D/E` (+ `Cargo`), `Ship - * TNG Cargo` allies, `Ship - Alarm Bouy`, `Ship - D5 Cruiser Alarm`, `Ship - Bird Of Prey Alarm`, `Ship - EscapePodEmpty`, `Sector/Klingon_Sensor`, `Prop - Probe_Federation`, `Prop - ScannableObjectA-D`, `Prop - NavPoint Explore`, `Prop - Buoy`, `Prop - BeaconBuoy`, `Debris - Big 01-03`, `Script Aid - LongRange Detectable`, `Script Aid - Spawnable Reference Point`, `Mission 00 Prefabs/TerroristBomb`, `Mission03_WarpInEffect`, `Mission03_ProbeTarget`.
10. **Cargo configurators**: Config_AddPeopleCargo_N (adds a Life subsystem), Config_AddCargo_N, Config_EmptyTribblesCargo, Config_DisableShip. Use them to make any spawned ship a rescue or retrieval target.
11. **Music cues**: the campaign suites (StormySea, SunAndShield, Survival, FleetBattle), `PLAY_MUS_DLC_ROMULAN_FIRST_ENCOUNTER`, E3 tension cues, and the briefing-room AAR music.
12. **Structure ideas**: parallel side objectives (alarm beacons, random enemies, dying star) next to the main thread; event randomisers (`ObjectiveScript.RunObjective<T>`) with a shared hail lock; a final-fight branch on return; a response timer that turns lateness into worse results (Intercept).
