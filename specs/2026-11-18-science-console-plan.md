# Science Console Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task by task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A walk-up science console on each bridge. In Revival Patrol, scanning things with it (two of three mini
games, held matched together) gives the ship small upgrades, and enemies get a little stronger after every completed
assignment.

**Architecture:**
- **Pure logic in `shared/Science/`, unit-tested in `tests/Science.Tests`:** the mini game models, scan progress,
  the upgrade ledger and its wire format.
- **Game-side code in `src/Science/`:**
  - placement: a shipped per-bridge file, built by a second `BridgeParts` set
  - the screen: copies of the game's console widgets, plus textures drawn by the mod
  - a "use console" mode added to Bridge Walk
  - host-owned state in room properties, with a client→host event
  - Harmony patches that apply the upgrades and enemy steps
  - Patrol hooks that spawn the things to scan

**Tech Stack:** C# net35 (mod: BepInEx 5.4 / Harmony / Unity 5.4, compiled against the obfuscated game DLL), net8 +
xUnit 2.4.2 (tests), plain JS (bridge designer page).

**Spec:** `specs/2026-11-18-science-console-design.md`. Mockups:
`/home/patrick/stbc/.superpowers/brainstorm/437533-1795043084/content/science-minigames-v4.html` (the mini game maths
there is the reference for Task 1).

## Global Constraints

- Work in `/home/patrick/stbc-science-console` on branch `science-console`. Never in `/home/patrick/stbc`.
- **Patrol only.** Every patch and tick is a no-op unless `ScienceSession.Active` is true (Task 3), so every other
  mission plays exactly as today.
- **On together or not at all.** The console, scanning and enemy steps are on for a Patrol session only if the
  ship's bridge has a shipped science placement. The host decides at Patrol start and publishes it.
- **Starting numbers** (all in `ScienceTuning`, Task 1):
  - crew +5% per scan, cap +50% per area
  - enemies +3% per completed assignment, rotating damage → shields → speed
  - lock threshold 70% match, aiming for a 30–45 s scan
  - ring nudge ⅓
  - science range 20,000 m (Patrol's `EngageRange`)
- The scan bar moves only while **both** picked games are ≥ 0.70, and it never goes backwards.
- **Standing only.** The console takes no input from a seated player.
- **Upgrades reset** at every Patrol start or restart (`PatrolObjective.StartScript`).
- **No DLL swap while the game runs.** Build with `dotnet build src -c Release`. Deploying the DLL and in-game checks
  happen with the game closed; ask the user before deploying.
- Shared code must compile for net35: no `ValueTuple`, no `Span`, no `System.HashCode`.
- User-visible text is in English, with German in `TranslationDe.cs`, as the mod's other strings are.
- Room property keys `SCU` (session + ledger) and `SCP` (scan progress) are new; custom event code **170** is new.
  The spec's list of codes in use (BridgeWalk.cs:927) must not be reused.

## Review Focus

1. **A player joins mid-Patrol, or mid-scan.** They should see the same upgrades, the same partly done scans and who
   is at the console, read from room properties. *Test: Task 2 `Ledger_RoundTripsThroughWireFormat`; in-game check
   in Task 9.*
2. **The player at the console disconnects or sits down abruptly.** "In use" must clear within a few seconds and
   progress must be kept. *Test: Task 2 `Claims_ExpireWithoutHeartbeat`.*
3. **The host leaves and HostHandoff moves the crew.** Totals and progress live in room properties, so the new host
   must adopt them, not reset them. Only `StartScript` resets. *Test: Task 2 `Ledger_FromWire_IgnoresGarbage`, and
   Task 3 adopts the existing `SCU` when becoming host.*
4. **Leaving the system mid-scan.** Its targets despawn. Their progress entries are dropped and the console goes
   back to the list, with no exception or stuck "LOCKED". *Test: Task 2 `Progress_DropsTargetsNotListed`.*
5. **Bad or hostile payloads on event 170** (NaN, out-of-range progress, unknown target, a non-claimant reporting).
   The host ignores them. *Test: Task 2 `Host_RejectsReportsFromNonClaimant` and `Host_ClampsProgress`.*

---

### Task 1: Mini game models and scan progress (shared)

**Files:**
- Create: `shared/Science/MiniGames.cs`, `shared/Science/ScanProgress.cs`, `shared/Science/ScienceTuning.cs`
- Create: `tests/Science.Tests/Science.Tests.csproj` (copy Galaxy.Tests' csproj without Cecil and the GameFiles
  links; include `../../shared/Science/*.cs`), `tests/Science.Tests/MiniGameTests.cs`, `tests/Science.Tests/ScanProgressTests.cs`
- Modify: `src/StbcRevival.csproj` (add `<Compile Include="../shared/Science/*.cs" Link="Shared/Science/%(Filename).cs" />`)

**Interfaces (Produces):**
- `enum MiniGameKind { Signal, Sweep, Tuning }`
- `abstract class MiniGame`:
  - `MiniGameKind Kind`
  - `int ControlCount` (3 for all three)
  - `float GetControl(int i)` and `void SetControl(int i, float v)`, with each control normalised to 0..1
  - `void Tick(float time)`, which moves the drifting target
  - `float Match` (0..1)
  - `static MiniGame Create(MiniGameKind k, int seed)`
- The three kinds are `SignalGame`, `SweepGame` and `TuningGame`. Each follows the mockup's maths:
  - Signal: frequency/amplitude/phase falloffs 1.5 / 0.5 / 1.6 rad.
  - Sweep: three rings. A ring counts as aligned within 10°, and `Match = Π clamp(1-off/60)^0.6`. `SetControl` on
    ring i nudges its neighbour by `ScienceTuning.RingNudge` × the change.
  - Tuning: `Π exp(-((x-t)/0.22)^2)` on 0..1 controls.
- `static class MiniGamePick { static MiniGameKind[] For(int targetSeed) }` returns two distinct kinds,
  deterministic per seed.
- `sealed class ScanProgress`:
  - `float Value` (0..100)
  - `bool Locked`
  - `void Advance(float matchA, float matchB, float dt)`
  - `bool Done`
- `static class ScienceTuning`: `LockThreshold = 0.70f`, `BaseRate`, `BonusRate`, `RingNudge = 1f/3`,
  `CrewStep = 0.05f`, `CrewCap = 0.50f`, `EnemyStep = 0.03f`, `RangeMetres = 20000f`.

- [ ] **Step 1: Write failing tests**
  - `Advance_DoesNothingBelowThresholdOnEither`: `Advance(0.69f, 1f, 10f)` leaves `Value == 0` and `Locked == false`.
  - `Advance_NeverGoesBackwards`: advance at (0.9, 0.9), then at (0.1, 0.1); `Value` is unchanged by the second call.
  - `Advance_PerfectFinishesIn30To45Seconds`: with (1, 1) and dt 0.1, `Done` becomes true between 30 s and 45 s;
    with (0.7, 0.7), at no less than 1.0× that time and within 120 s.
  - `Pick_TwoDistinctKindsDeterministic`: for seeds 0..999 the pair is distinct, the same seed gives the same pair,
    and all three pairs occur.
  - `Signal_MatchIsOneOnTarget_ZeroFarOff`, plus the same for Sweep and Tuning. Set the controls to the game's
    target, which is exposed as `internal float Target(int i)` for tests through `InternalsVisibleTo` or a test
    hook, then assert `Match > 0.95`. Opposite controls give `Match < 0.1`.
  - `Sweep_TurningOneRingNudgesNeighbour`: moving ring 0 by 0.3 moves ring 1 by 0.1 (±0.001).
  - `Drift_IsSlow`: over 1 s of `Tick`, `Match` from a perfect setting stays > 0.85. Over 60 s with no input it
    drops below 0.7 at least once.
- [ ] **Step 2:** Run `dotnet test tests/Science.Tests`. Expect FAIL (types don't exist).
- [ ] **Step 3:** Implement the types above. Pick `BaseRate` and `BonusRate` so that the timing test passes. Keep
  the rate rule: `rate = BaseRate + (min(a, b) - 0.7) / 0.3 * BonusRate`.
- [ ] **Step 4:** Run `dotnet test tests/Science.Tests`. Expect PASS. Run `dotnet build src -c Release`. Expect
  success.
- [ ] **Step 5:** Commit: `Science: mini game models and scan progress`.

### Task 2: Upgrade ledger, claims and wire formats (shared)

**Files:**
- Create: `shared/Science/Ledger.cs`, `shared/Science/ScanBoard.cs`, `shared/Science/ScienceWire.cs`
- Test: `tests/Science.Tests/LedgerTests.cs`, `tests/Science.Tests/ScanBoardTests.cs`

**Interfaces (Produces):**
- `enum UpgradeArea { Weapons, Shields, Impulse, Repair }`
- `enum EnemyArea { Damage, Shields, Speed }`
- `sealed class Ledger`:
  - `bool On`
  - `float Crew(UpgradeArea a)`
  - `float CrewVsFaction(int team)` (team is the game's `Team` enum as int)
  - `float Enemy(EnemyArea a)`
  - `int EnemySteps`
  - `void AddScan(UpgradeArea a)` and `void AddFactionScan(int team)`, each capped at `CrewCap`
  - `void AddEnemyStep()`, rotating Damage → Shields → Speed
  - `string ToWire()` and `static Ledger FromWire(string s)` (null or garbage gives a fresh ledger with `On=false`)
- `sealed class ScanBoard` (host-owned target state):
  - `void SetTargets(IEnumerable<string> ids)`: drops entries not listed and keeps the progress of those listed
  - `float Progress(string id)`
  - `bool TryClaim(int actor, float now)`
  - `void Heartbeat(int actor, float now)`
  - `int Claimant(float now)`, where 0 means free; claims expire 3 s after the last heartbeat
  - `void Release(int actor)`
  - `bool Report(int actor, string id, float progress, float now)`: accepted only from the claimant, finite, and
    not lower than the stored value; clamps to 0..100
  - `bool Complete(int actor, string id, float now)`: once per id
  - `string ToWire()` and `static ScanBoard FromWire(string s)`
- `static class ScienceWire`: the event-170 payload codec.
  - `byte[] Claim()`, `byte[] Release()`, `byte[] Heartbeat()`
  - `byte[] Report(string id, float p)` and `byte[] Complete(string id)`
  - `static bool TryRead(byte[] b, out ScienceMsg m)`, with `ScienceMsg { Kind, Id, Progress }`

- [ ] **Step 1: Write failing tests**
  - `AddScan_StepsFivePercentCappedAtFifty`
  - `EnemyStep_RotatesDamageShieldsSpeed`: after 4 steps, Damage is 0.06, Shields 0.03, Speed 0.03.
  - `Ledger_RoundTripsThroughWireFormat`, including faction entries and `On`.
  - `Ledger_FromWire_IgnoresGarbage`: `""`, `null`, `"x;y"` and NaN fields each give `On == false` and no exception.
  - `Progress_DropsTargetsNotListed`
  - `Claims_ExpireWithoutHeartbeat`: a claim at t=0 with no heartbeat makes `Claimant(3.1f) == 0`.
  - `Host_RejectsReportsFromNonClaimant`
  - `Host_ClampsProgress`: a report of 250 stores 100, and NaN is rejected.
  - `Complete_CountsOncePerTarget`
  - `Wire_RejectsTruncatedAndOversizedPayloads`: ids are limited to 64 chars.
- [ ] **Step 2:** Run `dotnet test tests/Science.Tests`. Expect FAIL.
- [ ] **Step 3:** Implement. Wire strings use `;`/`,` separators and invariant culture, like StellarEvacuation's
  `EVC`.
- [ ] **Step 4:** Run `dotnet test tests/Science.Tests`. Expect PASS. Run `dotnet build src -c Release`.
- [ ] **Step 5:** Commit: `Science: ledger, scan board and wire formats`.

### Task 3: Session and sync (host state in the room)

**Files:**
- Create: `src/Science/ScienceSession.cs`
- Modify:
  - `src/Missions/Patrol.cs`: `StartScript()` (around line 100) calls `ScienceSession.PatrolStarted()`. After
    `completed++` (around line 172) it calls `ScienceSession.AssignmentCompleted()`.
  - `src/Plugin.cs`: `Safely("science", ScienceSession.Update)` next to "restock sync".

**Interfaces:**
- Consumes: `Ledger`, `ScanBoard`, `ScienceWire` (Task 2); `SciencePlacement.HasForCurrentBridge` (create `src/Science/SciencePlacement.cs` here as a stub returning `false`; Task 6 fills it in); `PhotonProps.Set`; `RevivalMissions.Selected`;
  `NDIOGIGMPBH.RaiseEvent` / `OnEventCall`.
- Produces `static class ScienceSession`:
  - `bool Active` (we're in Patrol and `Ledger.On`)
  - `Ledger Ledger` (the latest from `SCU`) and `ScanBoard Board` (the latest from `SCP`)
  - `bool IsHost`
  - `void PatrolStarted()` (host: `On = SciencePlacement.HasForCurrentBridge`, a fresh ledger and board, then
    publish)
  - `void AssignmentCompleted()` (host: `AddEnemyStep`, publish, notice)
  - `void SendClaim()`, `SendRelease()`, `SendHeartbeat()`, `SendReport(string id, float p)`,
    `SendComplete(string id)` (the host looks up the target's area/team itself via `ScienceTargets`; clients are never trusted for it)
  - `event Action<string> Notice`, which shows a line on every machine
  - `void HostSetTargets(IEnumerable<string> ids)`

- [ ] **Step 1:** Implement publishing and reading.
  - **Host:** writes `SCU` and `SCP` with `PhotonProps.Set(room, key, wire)`. Writes happen only on change, and at
    most 4 per second.
  - **Clients:** read once a second, and whenever the room name changes (as RestockSync does).
  - **New host:** when this machine becomes host with an existing `SCU`, it adopts it.
  - **Event 170:** hooked lazily, like BridgeShare. The host applies messages to `Board`, and on `Complete` to
    `Ledger`. Messages with a sender id ≠ claimant are dropped by `ScanBoard`.
- [ ] **Step 2:** Implement notices. Use `Bridge.Instance.SendConsoleMessage(UI.MIFGHKACDIH.All, text, 6f)` (the
  call StellarEvacuation's `Say()` uses) on the host. The texts are "Graviton stream analysed: shields +5%" (kind
  name + area + new total) and "Enemy shields +3%". Add German to `TranslationDe.cs`.
- [ ] **Step 3:** Run `dotnet build src -c Release`. Expect success with no new warnings.
- [ ] **Step 4:** Commit: `Science: session state shared through the room`.

### Task 4: Upgrades and enemy steps take effect

**Files:**
- Create: `src/Science/ScienceEffects.cs`
- Modify: `src/Missions/RevivalMissions.cs` (`Patch`: add `ScienceEffects.Patch(harmony)` next to
  `FleetBattleArmor`); `tools/verify` (add each new patch target to its list)

**Interfaces:** Consumes `ScienceSession.Active`, `ScienceSession.Ledger`.

Every prefix and postfix returns at once unless `ScienceSession.Active`. All of them are wrapped in try/catch and
fall back to no change, as `FleetBattleArmor` does. These are the patch targets, from the decompiled code
(`/home/patrick/stbc/decompiled`):

| Effect | Target | Where it runs | Change |
|---|---|---|---|
| Crew weapons | `AI.AgentShip.takeHit` (both overloads; phaser `ref __2, __4`, torpedo `ref __1, __3`) | host | `× (1 + Crew(Weapons) + CrewVsFaction(victimTeam))` when the attacker is `PlayerSpacecraft` |
| Enemy damage | `BaseSpacecraft.takeHit(Vector3, float, Subsystem, SectorBody, float, float)` | host | `× (1 + Enemy(Damage))` when the victim is `PlayerSpacecraft` and the attacker is an `AgentShip` |
| Crew shields | `BubbleShieldServer.shieldHit(float, SectorBody, out float)` | host | `__0 /= (1 + Crew(Shields))` on the player ship |
| Enemy shields | `AgentShip.MaxShieldHealth` getter (`LPAMAFAACEJ`) postfix | host | `× (1 + Enemy(Shields))` |
| Crew impulse | `SimpleEngineClient.currentMaxSpeed` (`JGAFJNCGLIM`) and `currentMaxYaw` (`PMJCEJAEPHD`) getter postfixes | every machine | `× (1 + Crew(Impulse))` on the player ship |
| Enemy speed | `AgentShip` speed (`_maxSpeed` read site or `SpeedScale` `IPOFKCAAOHH` getter postfix) | host | `× (1 + Enemy(Speed))` |
| Crew repair | `SubsystemServer.AutoRepair` (`PPBDHDJCFKF`) postfix on player subsystems | host | add `pointsAdded × Crew(Repair)` |

The victim's team comes from `GetTargetInfo()` as `Sector.TargetInfoComponent`, then `.DGGJOECIFAF`.

- [ ] **Step 1:** Implement `ScienceEffects.Patch(Harmony)`, with a warning log per target it can't find, as
  `FleetBattleArmor` has.
- [ ] **Step 2:** Add a debug config `[Debug] ScienceLedger` (a wire string the host forces at Patrol start), so the
  effects can be checked in game without scanning.
- [ ] **Step 3:** Run `dotnet build src -c Release` and `dotnet run --project tools/verify -- "<game dir>"`. Expect
  every new target found.
- [ ] **Step 4: In-game check** (ask the user; game closed while deploying). Start Patrol with
  `ScienceLedger` = weapons +50%, enemy damage +30%. The log line per hit shows the scaled values. Other missions
  show no scaling.
- [ ] **Step 5:** Commit: `Science: upgrades and enemy steps take effect in Patrol`.

### Task 5: Things to scan in Patrol

**Files:**
- Create: `src/Science/ScienceTargets.cs`
- Modify: `src/Missions/Patrol.cs`. After `PickSite()` (line 160), when `ScienceSession.Active`, run
  `ScienceTargets.SpawnReadings(site)` as steps. Add a background step for the whole patrol
  (`Objective.AddStep(name, Task.MFLJBKCDBMC.WithSubtaskCoroutine(ScienceTargets.DebrisLoop()))`, as Borg does
  at lines 165-166) that spawns queued debris.

**Interfaces:**
- Produces `static class ScienceTargets`:
  - `IEnumerable<Step> SpawnReadings(BodyRef site)`
  - `IEnumerable<Step> DebrisLoop()`
  - `IList<ScienceTarget> InRange(float metres)`, every machine
  - `class ScienceTarget { string Id; string Name; UpgradeArea? Area; int Team; Vector3d Position; }`
- `Id` is the body's guid (`SectorBody.FLOBHLBHLLJ`). Pairs and progress are keyed by it.
  `MiniGamePick.For(Id.GetHashCode())` must be stable across machines, so use a hash written for this
  (FNV-1a), not `string.GetHashCode`.

- [ ] **Step 1: Readings.** Spawn 3 props per site from `Scannables` A–D, using Research's spawn call
  (Patrol.cs:880-882): name "Unidentified reading N", tag `Tag.Spawned`, 6–15 km from the site
  (`OffSurface` for bodies). Map the prop to an area:
  - A Protomatter → Weapons
  - B String fragment → Repair
  - C Graviton → Shields
  - D Verteron → Impulse

  These must stay out of Tactical's way: don't call `Unknown()` on them. The host then calls
  `ScienceSession.HostSetTargets` with the live ids.
- [ ] **Step 2: Debris.**
  - Listen for `Sector.EEKAKKNBAIH` (BodyDiedEvent) through `CIFDOFLFGEA.AddListener`, on the host only, while
    Active. Handle only `AI.AgentShip` with a hostile team (Klingon, Romulan, NeutralHostile, Borg).
  - Queue its position and team. `DebrisLoop` spawns one prop per queued death using
    `AtAngleAndDistanceFromBody(player, angle, distance)`, computed from the death position relative to the player.
  - Name it "<Faction> debris". `Area = null`, and `Team` = the faction.
  - Prop choice: try the Explore props and the game's debris or wreck prefabs in `GameDump`'s list. Pick the one
    that reads as wreckage and note it in a code comment.
- [ ] **Step 3: Lifetime.** Targets go when scanned (host: unspawn, as Patrol unspawns its spawned bodies) or when
  the ship leaves the system (`Abandoned()` / the system changes). The host calls `HostSetTargets` with what
  remains.
- [ ] **Step 4:** `InRange` on clients finds the targets by the ids in `SCP`, reading positions from the sector.
- [ ] **Step 5:** Run `dotnet build src -c Release`. Do the in-game check with the user: three readings at each
  site, and debris appears where a raider died.
- [ ] **Step 6:** Commit: `Science: readings at every site and debris from destroyed enemies`.

### Task 6: Science placement per bridge, shipped with the mod

**Files:**
- Create: `src/Science/SciencePlacement.cs`, plus `assets/bridges/science/README.md` (one line: "Science console
  positions, made with the bridge designer. Shipped with the mod.")
- Modify:
  - `src/BridgeParts.cs`: turn the static singleton into named sets. Add `static BridgeParts For(string set)`, and
    `Start` and `Clear` per set. The layout keeps its own set; science uses `"science"`.
  - `src/BridgeLayout.cs`: call `SciencePlacement.Update()` in the same scene binding.
  - `tools/package.sh` and `docs/install.sh`: nothing to change, `assets/bridges` already ships. Confirm that
    `science/` lands in `BepInEx/bridges/science/` and that user layouts never read that subfolder (`OwnFile`
    requires files directly in `bridges/`).
  - `tools/bridge-designer`: add a "Science console" added part and "Save science placement" (writes
    `assets/bridges/science/<layout name>.json` in the repo).

**Interfaces:**
- Produces `static class SciencePlacement`:
  - `bool HasForCurrentBridge`
  - `Transform ScreenAnchor` (null if none)
  - `Vector2 ScreenSize` (metres)
  - `Vector3 StandPoint` (bridge-local, for the reach check)
- File format: `{ "format": 1, "added": [ ...same as a layout's added parts... ], "screen": { "pivot": [x,y,z],
  "turn": deg, "tilt": deg, "size": [w,h] }, "stand": [x,y,z] }`.
- Lookup is by `BridgeLayout.LayoutName(scene)`. A class bridge (`Bridge_scene_02-<Class>`) falls back to
  `Bridge_scene_02.json`. If no file is found, `HasForCurrentBridge` is false.

- [ ] **Step 1:** BridgeParts sets. A layout reload must not clear the science set, and the other way round.
- [ ] **Step 2:** Implement `SciencePlacement`. The host-only BridgeShare replacement must **not** apply to the
  science file: everyone reads their own shipped copy.
- [ ] **Step 3:** Add the designer marker and save. In the designer, the screen rectangle shows as a translucent
  plane on the added part, so it can be lined up with the console's face.
- [ ] **Step 4:** Run `dotnet build src -c Release`. In game, with a test file for `Bridge_scene_02`, the console
  piece appears for everyone and a user layout reload leaves it alone.
- [ ] **Step 5:** Commit: `Science: console placement per bridge, shipped with the mod`.

### Task 7: The console screen

**Files:**
- Create: `src/Science/ScienceScreen.cs` (canvas, widgets, drawing) and `src/Science/ConsoleWidgets.cs`
- Modify: `src/Galaxy/WarpPages.cs`, so that `MakeButton` and `Neutralise` move to `ConsoleWidgets` and are reused
  from there. WarpPages behaviour stays unchanged.

**Interfaces:**
- Consumes `SciencePlacement.ScreenAnchor` and `ScreenSize`; `MiniGame` (Task 1); `ScienceTargets.InRange`
  (Task 5); `ScienceSession.Board` and `Ledger`.
- Produces from `ConsoleWidgets`:
  - `Button CloneButton(Transform parent, string label, Action onPress)`
  - `SliderInteraction CloneSlider(Transform parent, Action<float> onChange)`
  - `void Register(IEnumerable<HandInteraction> list)` and `void Unregister(...)`: adds to and removes from the
    local `HandAnimator`'s `DPAFLMHJMEC` list and sets `OLMKBPANPIO`, as PvpRoles.cs:507-537 does
- Produces `sealed class ScienceScreen` (one per bridge load):
  - `void Show()` and `void Hide()`
  - `void Tick(float dt)`
  - `bool InUse` (local player holds the claim)
  - `ScreenMode { Targets, Scan, OutOfRange, Busy }`

- [ ] **Step 1:** Build the canvas on `ScreenAnchor` with world-space sizing. The buttons are copies of the helm's
  IMPULSE button, as `WarpPages` does. The sliders are copies of `ConsoleHelm.throttleSlider` (`SliderInteraction`,
  which already drags by VR hand and by desktop mouse). Turn the throttle copy horizontal and range it 0..1.
- [ ] **Step 2: Targets screen.** Up to 5 rows from `InRange(ScienceTuning.RangeMetres)` showing name and distance.
  Tapping a row sends a claim and opens Scan with `MiniGamePick.For(id)`.
- [ ] **Step 3: Scan screen.** Two game panels, each with 3 sliders and a `RawImage` with a 256×128 `Texture2D`
  redrawn at about 20 Hz with `SetPixels32` (waveforms, rings, meter as in the mockup). Below them sit the shared
  scan bar and the upgrades line.
  - `ScanProgress` advances locally, starting from `Board.Progress(id)`.
  - It sends `SendReport` 4× a second while locked, and `SendHeartbeat` 1×.
  - At 100 it sends `SendComplete` and goes back to Targets.
- [ ] **Step 4: Out of range and Busy.**
  - Target out of range: show "Out of range: helm, close to N km", pause, and send a report.
  - Another claimant: show "In use by <name>".
- [ ] **Step 5:** Run `dotnet build src -c Release`. Commit: `Science: the console screen and mini games`.

### Task 8: Using the console while standing

**Files:**
- Create: `src/Science/ScienceUse.cs`
- Modify: `src/BridgeWalk/BridgeWalk.cs`. Add a `Walker.UsingConsole` gate next to the `typing` one in
  `TickInner`, and in `WalkPatches` let `NoAction` and `NoAxis` pass hand-pull/grab axes and mouse buttons while
  `ScienceUse.Engaged`.

**Interfaces:**
- Consumes `Walker.Standing`, `Walker.InVr`, `Walker.Me`; `SciencePlacement.StandPoint`; `ScienceScreen`;
  `ConsoleWidgets.Register`.
- Produces `static class ScienceUse`:
  - `bool InReach`: standing, within 1.2 m of `StandPoint`
  - `bool Engaged`
  - `void Tick()`

- [ ] **Step 1: Reach.** When `InReach` and standing, call `ConsoleWidgets.Register` for the screen's
  interactions. Leaving reach, or sitting down, unregisters them, sends `SendRelease` and keeps the progress.
  Seated players are never registered.
- [ ] **Step 2: VR.** While in reach, exempt the hand index-pull and grab axes from `NoAxis`, so pokes and slider
  drags work. Walking stays on the sticks.
- [ ] **Step 3: Desktop.** In reach, show "F: use science console" (config `[Science] UseKey`, default `F`, which
  no Bridge Walk or mod key uses).
  - Pressing it sets `Engaged`: walking and mouse-look stop, the camera eases to face the screen, and the cursor
    is unlocked and shown.
  - The use key or Esc leaves Engaged. While Engaged, Esc is swallowed (`Walker.SwallowEscape`) so it doesn't also
    sit you down.
- [ ] **Step 4:** Run `dotnet build src -c Release`. Commit: `Science: use the console standing, in VR and on a
  monitor`.

### Task 9: Bridge positions, end-to-end check, release text

**Files:**
- Create: `assets/bridges/science/Bridge_scene_02.json`, `Retro_Bridge_Scene.json`, `Bridge_scene_1701D.json`,
  `Bridge_scene_Holodeck.json` (the user places them in the designer)
- Modify: `README.md` (one "Science console" line under New missions → Patrol), `docs/index.html` (the same line)

- [ ] **Step 1:** The user places the console on each of the four bridges with the designer from Task 6. Check each
  class bridge (Nova, Sovereign, AbramsEnterprise, Defiant, Voyager). Where the class layout moves things so the
  Aegis spot is blocked, add a `Bridge_scene_02-<Class>.json`.
- [ ] **Step 2: End-to-end check with the user, two players, game closed while deploying.** Check each Review Focus
  item:
  1. joining mid-scan
  2. the scanning player quitting
  3. host handoff
  4. warping out mid-scan
  5. one Patrol with no scans, to confirm the enemy steps are felt
  6. one Patrol on a shared bridge, which plays as today
- [ ] **Step 3:** Tune `ScienceTuning` from what was felt. Rerun `dotnet test tests/Science.Tests` (the timing test
  bounds may move with the user's agreement).
- [ ] **Step 4:** Commit: `Science: console positions on every bridge`.
