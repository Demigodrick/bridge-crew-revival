# Galaxy spike notes (draft)

Spike code: branch `spike/galaxy` (worktree `/home/patrick/stbc-galaxy-spike`), `src/Galaxy/Spike.cs`, never merged.
Config: `[Debug] GalaxySpike` = `<Trench system>` or `<Trench system>+nodes` (empty = off). Every log line starts
with `Galaxy spike:`.

## What the decompiled code says

### The hook

- `GamePhase_Bridge.CreateSectorAndScenario` does `Object.Instantiate(MapPrefab)`: `SectorMap.Awake` runs there and
  calls `AssignSystemIds` (obfuscated `LGAFCFEMMDA`, private). That numbers `Loader.GetLoadedMap().Systems` by index.
- The same coroutine then waits, runs `Resources.UnloadUnusedAssets()`, makes the scenario and only then calls
  `SectorMap.InitAllSystems()`. `InitAllSystems` uses the loaded map directly and does not renumber.
- `MakeRunTimeClone` reads `Systems[SystemID].Position`, so a link appended after `AssignSystemIds` must get its
  `SystemID` (= its index) set by hand.
- **Chosen hook: a prefix on `SectorMap.AssignSystemIds`**, the earliest point both steps see. A prefix on
  `InitAllSystems` is the fallback. Both call the same idempotent `Ensure()`, which sets the copy's `SystemID` itself.
  Postfixes on both log the resulting ID and the runtime system.
- Not chosen: the end of the Loader's map load. It's a coroutine (`CheckForMapLoadComplete`) writing a private
  field, and it runs long before the bridge, while the room's mission choice can still change.
- The `SectorRoot` stays loaded between missions. `Loader.UnloadOldMap` only `Resources.UnloadAsset`s it when another
  map loads. So the link is kept and added or removed per mission. It is rebuilt if the map asset or the setting
  changed.

### How nodes become SystemTransforms

- `SystemTransform` is a ScriptableObject, not a plain class. A `SystemRoot` asset holds a flat list of them, one per
  node, and each `SystemNode.Transform` points back at its transform.
- `InitAllSystems` goes through the links in order. For each system:
  1. Each asset transform gets `AssignSector(sectorMap, AllocateSectorID())`, which writes its `sector` and `id`.
     The campaign/procedural filter runs here too (`ShouldFilterOutNode`, tags `OnlyCampaign`/`OnlyProcedural`).
  2. `SystemRoot.MakeRunTimeClone` does `Object.Instantiate(root)`, clears the clone's list and adds
     `SystemTransform.Clone(runtimeRoot)` of each asset transform. `Clone` instantiates the transform, copies
     `System`, `id` and `sector`, and inserts it in the root's spatial hash.
  3. `MapBase.RegisterTransform` (`_transformsById.Add`) runs on each clone. A duplicate id throws. Starting tags
     come from `SystemNode.Tags.Tags` plus `SystemNode.Prefab.PrefabTags.Tags`.
  4. `LinkParentAndChildren` finds a clone's parent and children through `SystemNode._parent`/`_children` and each
     node's `Transform.id`. That is the asset transform's id, which step 1 just set.
- Ids are handed out in list order, so appending keeps every Trench `SectorId` and `SystemID`.
- `LoadSystem` (start) and `ChangeActiveSystem` (warp) call `SpawnMapBody` for each runtime transform:
  - It instantiates `SystemNode.Prefab`, or an empty `AtticSectorBody` when there is none.
  - It reads `SystemNode.ProceduralStatusEffectData.Length`, so that array must not be null.
  - `ChangeActiveSystem` also runs `Resources.UnloadUnusedAssets()` on every warp.
- In the attic, a body's position is `SystemTransform._position`.
- `SystemNode.SystemPosition` is the parent's position plus `UnitScale.ScaleVector3(Position, Scale)`, in metres.
  The units are Kilometers, Skybox (1e6 m), Lightseconds and AstronomicalUnits.
- The editor code that filled `_position`, `_rotation`, `_radius` and `_name` is stripped from the game.
  - The spike sets `_position = node.SystemPosition`, `_rotation = node.Rotation`, `_radius = node.Radius` and
    `_name = node name`.
  - It also logs, for the original system's first 8 transforms, the stored values next to the node's. **The log
    says whether these guesses are right.**
- What `+nodes` builds, all with `ScriptableObject.CreateInstance` and `HideFlags.DontUnloadUnusedAsset`:
  - **Each node:** fields copied from a Trench template node of the same kind (prefab, rotation, sprites, map
    name, landing radius, status effects). `Scale = Kilometers`, `Root = copy`, `_parent`/`_children` set.
  - **Node tags:** the template's own tags plus the required one, without `OnlyCampaign`/`OnlyProcedural`. Both
    `_tags` and `_tagNames` are set.
  - **Each transform:** `_systemNode`, `_position`, `_rotation`, `_radius` and `_name` set, and
    `node.Transform = transform`. The copy's `SystemTransforms` is replaced by the 4 new transforms.
  - **The tree:** star at the template star's position; planet as the star's child at the template planet's
    position; station as the planet's child; player spawn (`MissionManager.NovaPlayerSpawnDefaultTags`, which is
    `Player`) 30 km beyond the station.
  - **Templates:** star from the original system, planet from the original (else anywhere in the Trench), station
    from any Trench system.

### SystemRoot fields that matter

| Field | Used by |
| --- | --- |
| `SystemName` | warp panel labels. Might be a localisation key; the spike appends " II" and the log/warp map shows how it looks. |
| `SystemID` | the list index; set by `AssignSystemIds` or by the spike |
| `Position` | set from the link by `MakeRunTimeClone`; places the warp map button |
| `SystemTransforms` | the node tree |
| `_skyboxPath`, ambient/fill light | `LoadSystem` |
| `SystemTags` | `ChooseStartingSystem` (system label tags) |
| `SystemIDTag` | telemetry/achievements only (`TrackingManager`) |
| `SystemMapBackground`, `WarpMap3dVisualPrefab` | map visuals; copied as-is |
| `_spatialHash` | runtime-only; built fresh in the constructor of the clone |

### Risks

- **Plain mode shares the original's asset transforms and nodes.**
  - `Object.Instantiate` copies the reference list. `InitAllSystems` then gives those asset transforms a second id
    when it reaches the copy.
  - The original is already cloned and linked by then. The only reader of `SystemNode.Transform` is
    `LinkParentAndChildren`, and `SystemNode.Root` is never read, so both should still work. The real feature
    should not share them.
- **The copy keeps the original's system label tags.** A tag-based start choice could pick it.
- **Patrol may start in the copy.** `MissionMaps.FindSpawn` shuffles all systems for Patrol. The real feature must
  keep starts in the Trench (design already says so).
- **The `SystemName` + " II" display is unknown** (raw key or localised name).
- **The `+nodes` rotation and position conventions are unconfirmed** (see the logged comparisons). Parent-relative
  rotation is ignored.
- **The `ROOT`/`FAMILY`/`POI` hierarchy tags drive `ContainerTransform`.** The new nodes carry whatever their
  templates had, so impulse grouping might look odd.
- **Every player must have the same setting** (no room check in the spike), or system ids differ → desync.

## To test in game

Put the spike build's `StbcRevival.dll` in the game (game closed). Its config file is
`BepInEx/config/stbc.revival.cfg`, section `[Debug]`.

**Step 3: plain copy**
1. Set `GalaxySpike = Venov` (any Trench system name works; a wrong name logs the list of names).
2. Host Revival Patrol solo.
3. In `BepInEx/LogOutput.log`, look for:
   - `Galaxy spike: SectorMap.AssignSystemIds prefix (from SectorMap.Awake): appended ... new ID N, Trench count N (equal, as expected)`
   - `after AssignSystemIds: ... has ID N`
   - `after InitAllSystems: N+1 runtime systems; ours: '... II' ID N ...`
   - the `transform '...' / node '...'` comparison lines. Copy these into Results.
4. On the helm warp map: a second button next to the original (offset x+200, z+200 in universe units). Check
   the label it shows.
5. Warp to it. Same bodies as the original. Warp back. Note any error in the log.
6. Start a different mission (e.g. Free Roam or an Ongoing Voyage) in the same session. The log should say
   `removed ...` or `list untouched`, and the warp map should show no extra button.

**Step 4: new nodes**
1. Set `GalaxySpike = Venov+nodes`, then restart the game, or at least start a new mission (the setting change
   rebuilds).
2. In the log, look for the `template star/planet/station/player` lines, four `new node Spike ...` lines and
   `after InitAllSystems: ... 4 transforms`. Each `runtime transform` line should show a parent (except the star)
   and its tags.
3. Warp to `... II`. Star, planet and station visible? Impulse to the planet and station works?
4. Set `[Debug] TestPatrolAssignment = rescue` (or let Patrol run a few assignments). Does a job land in `... II`?
   Restocking at the Spike Station?

**Step 5: two players**
1. Both PCs on the spike build with the **same** `GalaxySpike` value. Host Patrol, the other joins.
2. Both warp to `... II` and back. Both see the same bodies, and the job marker on the same system.
3. Check both logs for the same new ID. Look for desync or freeze, and for errors mentioning `SectorId` or
   `_transformsById`.
4. Optional: the joiner with `GalaxySpike` empty. Expect breakage. Note how it fails; it is input for the room
   hash check.

## Results

Run 1 (2026-11-15, solo, Patrol, `GalaxySpike = Venov`, offset +200/+200):
- Hook worked: AssignSystemIds prefix from SectorMap.Awake appended '2824|Venov II' with new ID 15 = Trench count 15; InitAllSystems built 16 runtime systems (511 transforms in the copy). No errors from the spike. Patrol started in Toth.
- Not visible on the warp map: its position (133.6, 289) is outside the universe (z only -115..115). Rebuilt with the copy at (79.6, -10) for run 2.
- Catalogue (F9, clashed with GameDumpKey/AvatarDumpKey = F9; moved to F5): 16 systems (included Venov II: must list only the Trench's own), 411 prefabs, 117 tags, 15 skyboxes, spawn tags [Player, AltPlayerSpawn], universe x -265..265, z -115..115. No trench-map.png: `_mapBase` has no background sprite; find where the warp map art lives. System names are text keys like '2824|Venov' (show the English part in the tool).
- Unrelated: `bridge layout failed: DllNotFoundException: MonoPosixHelper` (GZip in BridgeShare.cs, on main).

## Changes implied for Tasks 5–6

1. **Hook:** prefix on `SectorMap.AssignSystemIds` (called from `SectorMap.Awake`), `InitAllSystems` prefix as
   the fallback. Proven: appended system gets ID = Trench count; Trench IDs unchanged; other missions on the
   Trench see the list untouched (15 systems).
2. **Building nodes:** `Object.Instantiate(node)` + rewiring `_parent/_children/Root/Transform/View`, and a new
   `SystemTransform` per node, works (run 4). For nodes the designer makes from scratch, a transform's stored
   `_position` equals `node.SystemPosition`, `_rotation` = `node.Rotation`, `_radius` = `node.Radius` (run 1:
   diff 0 m on all 511 Venov transforms).
3. **A system must be ROOT → FAMILY → POI → REGION → bodies.** The impulse map shows a FAMILY's POIs (tags POI
   under a container tagged FAMILY, `FamilyMapBackground` backdrop, `SystemMapName` labels). Flat nodes give an
   empty impulse map. The galaxy file, its check, the catalogue and the designer's system view must model these
   levels; copying a whole Trench family as a starting point is proven to work (run 4: impulse map, bodies,
   a Patrol job there).
4. **Names:** plain names. `'2824|Venov II'` shows as "VENOV" (the text key wins).
5. **Catalogue fixes:** list only the Trench's own systems (it listed the appended one); the warp map art is not
   on `_mapBase`'s Image: find it; add the sprite names a node can use (`FamilyMapBackground`,
   `SystemMapIconSprite`) so the file can name them.
6. **Not yet seen in game:** the region removed again after a Patrol mission in the same session (run 4 went
   Ongoing Voyage → Patrol, the other order); two players. Task 5's tests and in-game steps cover both.
7. Patrol's own job picker uses the region unchanged ("offering defend in VENOV" landed in the copy).

Run 2 (copy at (79.6, -10)): the button shows on the warp map between Devin and DS-4219, labelled "VENOV" (the
'2824|' text key wins over the rest of the name: region systems need plain names, no 'id|' prefix), with a
smaller dot than the Trench systems (map icon sprite not set or different size: check SystemMapIconSprite).
Run 2 (cont.): warped to the copy and back (two arrivals in the log, no errors; user confirms). Patrol listed 16
systems including the copy. The second mission in the same session wasn't in that log (game quit) — check in run 3.

Run 4 (`Venov+nodes` = copy of FAMILY Venov III as new nodes, 98 transforms): impulse map shows the family
and its 3 POIs; bodies there; Patrol offered a job in the copy and the user flew it. Before that, in the same
session, an Ongoing Voyage on the Trench logged "list untouched (15 systems)". No errors.

Run 3 (`Venov+nodes`, 4 flat hand-made nodes star → planet → station/spawn): the bodies exist (tactical's local map
shows "Space Station" at 2.4 Mm) but the **impulse map is empty** and "Current location: /" is blank.
Why: the impulse map (FamilyMap / FamilyRegions) is built from nodes tagged POI inside containers tagged FAMILY,
with SystemNode.FamilyMapBackground as its backdrop and SystemMapName as labels. Every Trench system is
ROOT → FAMILY (prefab "Family Group", Lightseconds) → POI (prefab "Point of Interest") → REGION → bodies.
Venov: FAMILY Cloud, Venov III, Venov Major, Venov II, Venov IV, each with 2–3 POIs.
**Implication for the design:** the designer's system view must work in these levels: family (an impulse map
page, with its backdrop) → POI (named spot on it) → region → bodies; that's the user's own "system → jump
location/POI → contents". Run 4 copies a whole Venov family as new nodes to prove building that from scratch.
