# How the game's missions work, and how to write one

A guide for Revival missions, built from mapping the game's mission system (`names.map`, its "Mission manager",
"Mission scripting framework" and "The game's missions" sections). Names here are the readable ones from
`names.map`: read the code in `decompiled/` (`dotnet run --project tools/xref -- dump`). The mod's own code has to
use the scrambled names on the left of `names.map` (as `src/Missions/*.cs` do: `Task.KNMMMJOOBHC` is
`Task.Wait`). What each of the game's own missions does is in [MISSION-CATALOGUE.md](MISSION-CATALOGUE.md).

## A mission's life

1. **Chosen.** `Loader` holds the choice. For a campaign mission, `Loader.Mission` is
   `_campaignElements[_campaignElementIndex].Mission`; for a procedural one, `_proceduralElements[...]` gives the
   map and mission-table names, and `GetMissionType()` says which kind it is.
2. **Map and sector loaded.** On the host, `GamePhase_Bridge.HostLoadMission` loads the map and the mission table,
   seeds the random generator and runs `CreateSectorAndScenario`: the `SectorMap` and `Loader.Scenario` (the
   `MissionManager`), then `Init(sector, spawnManager)`.
3. **Runner created.** `MissionManager.Awake` (master only) calls `CreateRunner`: `new MissionRunner(sequenceType,
   this)` for the campaign; `CreateProceduralRunner` picks a weighted mission table entry and objective table entry.
   Creating the runner instantiates the `MissionSequence`, which builds its objective graph (`CreateSequence`) and
   its encounter contract (`MakeContract`).
4. **Start system and encounters.** `StartingSystemSelection` reaches the sequence's override. Procedural missions
   then run `GenerateProceduralMission`: `SeedRandom`, `SectorMap.ChooseStartingSystem`,
   `EncounterPopulator.PopulateEncounters` (retrying with new seeds until every constraint holds); otherwise
   `ChooseStartingSystem` directly. Then `LoadStartingSystem` and `PreloadSequenceObjects`.
   *The mod hooks here:* `RevivalMissions` swaps `MissionManager.Runner` in a postfix on
   `GenerateProceduralMission`, and `MissionMaps` replaces the generation in a prefix.
5. **Player ship spawned.** `ScenarioBase.Start` → `OwnerStart` → `SpawnPlayerShip` (`FindSpawnPoint` or the
   populator's `PlayerSpawnPoint`), tagged Player, with hull callouts at 50/25/10/0%.
6. **Sequence started.** Once every player is synced, `GamePhase_Bridge.FadeToBridge` sends `FadeToBridgeEvent`; a
   frame later `_safeToRunSequence` is set, and the next `UpdateMission` calls `StartRunner` (records
   `MissionStartTime`, `Runner.Run()`), which runs the start objective (one `ObjectiveRunner` per objective).
7. **Objectives tracked.** When an objective succeeds or fails, `EvaluateLinks` follows the sequence's
   `OnSucceed`/`OnFail` links: the next objectives, or the end (`MissionIsSuccess`/`MissionIsFailure`); an
   unhandled failure fails the mission. Directives (the captain's objective list) live in
   `MissionManager.Directives` and sync to clients. The monitors (Defend/Destroy/Scan targets, cargo collection,
   directive markers) update in `SharedUpdate`.
8. **The end.** `OnRunnerSuccess`/`OnRunnerFailure` → `EndMission(status, line1, line2)` → RPC
   `Multicast_MissionOver` → `MissionOverEvent`. `UpdateMission` also ends it on its own: hull at 0 with
   `MonitorPlayerHealth` on (`PlayerDeathMissionEnd`, "HULL COMPROMISED"), the timeout (120 minutes by default), and
   an `EndMissionRequestEvent` (pause-menu abort: *aborted*, shown as a failure; the captain leaving).
9. **Results.** `MissionResultManager` resets on `FadeToBridgeEvent`, collects `AddMissionResult(text, success)`
   lines (what `Task.MissionText.WithMissionResult` adds) and `Add*Stat` stats, and on `MissionOverEvent` adds play
   time, hull integrity and torpedoes fired. `MissionResultsUI` shows them with the XP and rank animation.

### What the manager offers a mission

- **Directives:** `AddDirective`, `CompleteDirective`, `FailDirective`, `RemoveDirective`, the SubDirective calls,
  `ResetTimerToValue`, `AddSystemName`, `MissionString`.
- **Monitors:** Defend/Destroy/ScanTargetsMonitor (`BeginDirective`, `Monitor*`, `Progress()`, success/fail), and
  `CargoCollectionMonitor` (it only counts beams from bodies registered with `MonitorSectorBody`, which spawning does
  at spawn: cargo put aboard later needs registering, see Patrol's `CountBeamsFrom`).
- **Health callouts:** `AddHealthMonitor(body).AddThreshold(%, vo, crew)`.
- **Navigation:** `WarpingEnabled`, `CannotExitWarp`, `PauseLoadingNewSystem`, `UseAltWarpDestinations`,
  `SystemAvailable[]`, `POIAvailable`, `DirectiveSectorIDs`/`MarkedSystemIDs` (map markers).
- **Ending and presentation:** `MonitorPlayerHealth` (off to handle ship loss yourself), `SendMissionFailureEvent`,
  `DisplayViewScreenMessage`/`DisplayScreenMessage`, `FadeScreenToBlack`, `BlackoutBridgeViewscreen`.
- **State:** `Blackboard` (named variables shared by every objective), `Random` (seeded), `RunStandaloneObjective`,
  `PlayerShip`, `Sector`, `SpawnManager`.

## Writing a mission

- **Sequence:** subclass `MissionSequence`, override `CreateSequence`: `var a = Sequence.MakeNode<ObjA>()`, then
  `Sequence.OnSucceed(a).RunObjective(b)` (or `.RunObjectivesInParalell(b, c)`, `.RunRandomObjective(n, ...)`,
  `.MissionSucceeds()`), `Sequence.OnFail(a).MissionFails()`, `Sequence.StartObjective(a)`. The objectives of a
  graph share one Blackboard. (Revival missions: `src/Missions/Patrol.cs` and the others.)
- **Objective:** subclass `MissionObjective` (or `EncounterObjective<MyContract>` for a typed `Encounter` field) and
  override `StartScript()`. It has `Objective` (the ObjectiveApi: `AddStep`, `Success`/`Failure`, `Find*`), `Task`
  (the TaskBuilders) and `Blackboard`.
- **Steps:** `Objective.AddStep("name", t1, t2, ...)` adds a named list of tasks that run **in order** (each
  finishes before the next starts). `Objective.Success(...)`/`Failure(...)` may only be the last task; it ends the
  objective. Steps run **in parallel**.
  - **The first step added starts by itself; any other must be started**: `Task.StartSteps.WithName(...)`
    (optionally `.AndWaitUntilAllFinish()`/`.AndWaitUntilSomeFinish(n)`), stopped with `Task.StopSteps.WithName(...)`.
    A step added while another runs and never started simply never runs (this is how Patrol's Borg cube once never
    appeared).
  - A step can restart itself (a loop).
- **Waits and triggers are tasks** that don't finish until something happens: put one in a step to block on it,
  several to require them in turn, parallel steps for "any of". Threshold triggers (`HealthTrigger`, transport,
  scan, AI detection, directive progress) compare a 0-100 value with a threshold:
  `Task.HealthTrigger.WhenTarget(body).IsLessThan(30)`.
- **Logic:** `Task.Condition.If(intVar, 3).GreaterThanEquals().ThenRunStepAElseStepB("a", "b")`,
  `Task.Condition.WaitUntil(var, 5).IsLessThan()` or `WaitUntil(() => bool)`; `Task.VarOp.Set/Add/Sub/Increment/Random`;
  `Task.Signal.Broadcast("x")`/`.Receive("x")` between steps and objectives;
  `Task.ObjectiveScript.RunObjective<T>("name")`/`.WaitForObjective(...)` for side scripts (how Explore runs its
  random events).
- **Into C#:** `Task.Run.WithAction(...)`/`.WithSubtaskCoroutine(...)`: the Revival missions do most of their logic
  in coroutines yielding tasks.
- **Encounters, declared:** a contract class with public `EncounterSystem`, `EncounterRegion`, `EncounterSpawned`,
  `EncounterSpawnedGroup`, `EncounterSpawnTable`, `StringEntry` fields; in `DefineConstraints(c)`:
  `c.AddSystem(ref SysA).SetAsStartingSystem()`, `c.AddRegion(ref R).InSystem(SysA).OfType(RegionType.Nebula).IsIsolated()`,
  `c.AddSpawned(ref Ally).SpawnIn(R)` (or `.Near(body, ring)`, `.Towards(...)`, `.UsingPrefab(...)`),
  `c.AddSpawnTable(ref Fleet).InRegion(R)`; returned from `MissionSequence.MakeContract()`. The populator tries
  system and region assignments until every constraint holds. (Not used by a Revival mission yet: they place
  things by code.)
- **Encounters, launched:** `Task.LaunchEncounterSpawned.UsingEncounter(Encounter.Ally)`,
  `Task.LaunchEncounterSpawnTable.UsingEncounter(Encounter.Fleet)` (`.InRegion(..)`, `.WithSpawnMode(..)`); AI tasks
  take an EncounterBody or EncounterBodyGroup as well as a BodyRef.
- **Ad-hoc spawns:** `Task.Spawn.UsingSpawner(spawner.WithPrefab(..).AssignName(..).AddTags(..))...OutputTo(bodyRef)`,
  as Patrol does; `.UsingSpawnMode(SpawnMode.Warp)` warps it in, `.ApplyingConfigurators(typeof(...))` sets it up
  (e.g. `BorgPhaseExplore`), `Task.SpawnBodyFromCloak` decloaks it in.

### The task builders, by purpose

- **Flow:** `Wait` (`WithTime`, `WaitOneFrame`), `Run`, `StartSteps`, `StopSteps`, `StepTrigger`
  (`IsRunning`/`IsNotRunning`), `Condition`, `VarOp`, `Signal`, `ObjectiveScript`, `Log`.
- **Triggers:** `RangeTrigger` (`WhenTarget(s)(..).OfBody/OfPosition(..).IsWithin/IsOutside(m)`,
  `DontTriggerWhileImpulsing`), `HealthTrigger`, `TransportTrigger` (% of a target's cargo beamed off),
  `TransportLockTrigger`, `ScanTrigger` (`WhenTargetSubsystem(body, sys).IsEqualTo(100)`: fully scanned),
  `TraverseStateTrigger` (warp/impulse state), `WarpStateTrigger`, `SystemLoadedTrigger`, `PlayerInSystemTrigger`,
  `PointOfInterest` (in a POI), `AIDetectionTrigger`, `EnemyDetectedPlayerTrigger`, `AIHackedTrigger`,
  `AIDisengageTrigger`, `AIHail`, `PlayerButtonPressSkip`.
- **Spawning:** `Spawn`, `SpawnBodyFromCloak` (`WithDelay`), `LaunchEncounterSpawned`, `LaunchEncounterSpawnTable`,
  `RandomizeSpawnTable`, `UnspawnBodies` (`WithTags`), `AIDespawn` (`UsingMode(SpawnMode.Warp)`).
- **AI:** `AIObjectives` (`ToAddObjectives(..).OfObjectiveType(Attack...)`), `AIAggro`, `AIPatrol`,
  `AddAISearchPoint`, `AIMoveTo` (`ToDestination`, `WithWaitTime`), `AIConvoy` (`ToJoin`/`ToLeave`,
  `AsGuardShip`/`AsConvoyShip`), `AIConfigureAlarm` (reinforcements), `AIConfigureTether`, `AIConfigureDisengage`,
  `AIDisableDisengage` (no cloaking away: what Patrol's raiders get), `AIShields` (subsystem strengths),
  `AIVulnerable`, `AITeam`, `AIDisable`, `AIBorgSetRegeneration`, `AILastKnownLocation`.
- **Directives and mission UI:** `Directives` (descriptions, counters, timers, sub-directives, complete/fail),
  `MissionText` (title, results, stats), `Markers`, `WarpDestination`/`POIDestination` (activate systems and
  places), `Warping` (enable/disable, enter/exit, destination), `CaptainIntel`, `ViewScreenMessage`,
  `CargoTransfer` and the Defend/Destroy/Scan directive monitors, targets and progress triggers.
- **Player ship:** `SetStats` (hull, shields, subsystems, torpedoes, cargo), `Throttle`, `PlayerMinimumHealth`
  (can't be killed below it), `PlayerHealthMonitor`, `PlayerPowerGraph` (damage power routes),
  `PlayerShipEffects.PlayEmpWave`, `BorgInfection`, `BorgRepairEffects`, `DestroyShip`.
- **Presentation:** `ExternalView`, `SetCameraFade`, `BlackoutViewscreen`, `PlayViewscreenMovie`, `HailBridge`
  (an NPC hails the bridge), `PreloadTalkingHead`, `ToggleViewscreenTalkingHead`, `ToggleViewscreenWaveform`,
  `PlaySound`, `PlayVoice`, `PlayMusic`, `PlayLoopingSound`, `MusicState` (`State_Group_Mus_Dyn`: Explore,
  Moderate_Tension, Tension), `SetSoundSwitch`, `VOControl`.

## Lessons from the Revival missions

- **Start every extra step** (see Steps above).
- **Scanned and beamable:** the transporter only locks onto a body whose life signs or cargo are known (scanned),
  and a body that already counts as fully scanned can't be scanned again. Leave a target unscanned
  (`SetSubsystemKnown(All, false)`) to make the crew scan it; mark it known to skip that.
- **Cargo counted:** see the CargoCollectionMonitor note above.
- **Instant repairs:** the game's ships don't heal, so effects tied to damage (bridge fires and sparks, the
  Enterprise-D's auxiliary-power lighting, subsystem alarms) don't undo themselves after `SetStats` repairs:
  `LastStandBridgeRepair` undoes them for missions that repair (`RevivalMissions.RepairsShip`).
- **Cloaked ships:** a Bird of Prey cloaks to disengage and in combat; a mission waiting for "all gone" must handle
  a ship that stays cloaked (Patrol's `Remaining`) or switch disengaging off.
- **Endless missions:** quitting is an abort, shown as a failure; Patrol turns it into a success after an
  assignment (`RevivalMissions.EndRequest_Prefix`).
- **Multiplayer:** the mission runs on the host only; what each player's game sees comes from the game's own sync
  (directives, markers, spawned bodies). State the mod adds on top (people aboard, ship choice) needs sharing
  itself, as room or player properties.
