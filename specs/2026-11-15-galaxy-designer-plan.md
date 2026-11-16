# Galaxy Designer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task by task. Steps use checkbox (`- [ ]`) syntax for
> tracking.

**Goal:** A second region of hand-designed systems appended to the Trench for Revival Patrol and Free Roam,
with a second page on the helm warp map, and a browser tool to design it.

**Architecture:**
- **Pure, Unity-free galaxy logic** lives in `shared/Galaxy/` (file model, parse/write, validation, hash, the
  crew gate, page assignment) plus `shared/MiniJson.cs` (moved from `src/`). It is compiled into the mod
  (net35), into the designer tool (net8) and into a new xunit project `tests/Galaxy.Tests` (net8), as
  `shared/Voice` already is.
- **Game-facing glue** lives in `src/Galaxy/`: the catalogue dump, building the systems into the Trench, the
  room hash check, and the warp map's second page.
- **The designer** is `tools/galaxy-designer/`, built like `tools/bridge-designer` (local web server, page
  embedded, single-file publish).

**Tech Stack:** BepInEx 5 / HarmonyX, C# `LangVersion latest`, net35 (Unity 5.4 Mono) for the mod; .NET 8 for
the tool and tests; xunit 2.4; plain JS + canvas 2D for the page (no new libraries).

**Spec:** `specs/2026-11-15-galaxy-designer-design.md` (read it first; this plan argues from it).

## Global Constraints

- Only when the loaded map is the Trench **and** `RevivalMissions.Selected` is `PatrolSequence` or
  `Mission.DebugExplorationMode.DebugExplorationModeSequence`. Every other mission sees the Trench's own list.
- Region systems are **appended** after the Trench's systems; Trench system IDs never change.
- Missions start in the Trench: `MissionMaps.FindSpawn` never picks a region system.
- Warps to the region are ordinary warps; no change to warp time.
- A file that fails validation is skipped whole, with one log line per problem; never half-applied.
- Region on only when the host has it on and every player in the room has the same file hash; otherwise off
  for everyone for that mission, and the host sees one line: `<Region name> is off: <player> doesn't have it`.
- Galaxy files: `BepInEx/galaxies/<name>.json`; catalogue: `BepInEx/galaxies/catalogue.json`; repo copy of
  shipped files: `assets/galaxies/`. Saves keep the previous file as `<name>.json.bak`.
- Config: `[Galaxy] Region` (string, default the shipped region's name; empty = off) and
  `[Galaxy] CatalogueKey` (KeyCode, default `None`).
- Room property key for the host's hash: `GH`; player property key for each player's hash: `GH`.
- No new art; models, skyboxes, sprites come from the game.
- Never replace the plugin DLL while the game is running (it freezes the game); build, then start the game.

## Review Focus

1. **A player whose file differs mid-session** (host updates the file, a joiner has an older one): the region
   must be off for that mission, never desynced. → Task 4 `Gate_*` tests, Task 5 manual two-player check.
2. **Patrol after the region turns off between missions in one session** (Patrol with region, then Ongoing
   Voyage, then Patrol again): the Trench list must be back to its own systems each time, and on again
   without duplicates. → Task 5 `RegionLinks_*` tests and manual step.
3. **A galaxy file naming a prefab or tag a different game build doesn't have:** skipped whole, logged, game
   loads normally. → Task 2 validation tests, Task 5 manual step.
4. **Warp target selected on the other page, then the page flipped back and forth**: selection and its marker
   survive; the helm can still engage. → Task 6 manual step.
5. **Hand-edited JSON** (comments, trailing commas, missing fields, duplicate ids, a parent that doesn't
   exist, a node that's its own ancestor): clear message naming the system/node, no exception escapes. →
   Task 2 tests.

---

### Task 1: Spike (throwaway)

A test build on a throwaway branch `spike/galaxy` (off `galaxy-designer`), never merged. Its only product is
`specs/2026-11-15-galaxy-spike-notes.md` on `galaxy-designer`.

**Files:**
- Create (spike branch only): `src/Galaxy/Spike.cs`, a `[Debug] GalaxySpike` string config in `src/Plugin.cs`
- Create (kept): `specs/2026-11-15-galaxy-spike-notes.md`

- [ ] **Step 1:** `git worktree add -b spike/galaxy /home/patrick/stbc-galaxy-spike galaxy-designer`.
- [ ] **Step 2:** In `Spike.cs`, when `[Debug] GalaxySpike` names a Trench system and Patrol is selected,
  before `SectorMap.InitAllSystems`: `Object.Instantiate` that `SystemRoot`, rename it `"<name> II"`, and
  append a `SystemLink` at the original position plus (200, 0, 200). Log the hook used and the new ID.
- [ ] **Step 3:** Build (`dotnet build src -c Release`), start the game, host Patrol solo. Expected: a second
  button on the warp map; warp to it and back works; log shows the new ID equals the Trench count.
- [ ] **Step 4:** Replace the copied system's nodes with ones made by `ScriptableObject.CreateInstance<SystemNode>()`
  (one star, one planet tagged `Planet`, one node tagged `Player`, one `SpaceStation`) and find how
  `SystemTransforms` are made from nodes (look at `SystemTransform` constructors and who calls them in
  `/home/patrick/stbc/decompiled`). Warp there. Expected: the bodies appear; Patrol sends a job there
  within a few assignments (or `[Debug] TestPatrolAssignment` forces one).
- [ ] **Step 5:** Two players (the user's second PC or a friend), both on the spike build: warp there and back,
  check both see the same system and the job.
- [ ] **Step 6:** Write the notes: the hook (method and prefix/postfix), how nodes become `SystemTransform`s,
  which `SystemRoot` fields must be set, what broke, and any change these imply to Tasks 5–6. Commit the
  notes on `galaxy-designer`; leave the spike branch unmerged.

**Gate:** Tasks 5 and 6 are revised from these notes before they start. Tasks 2–4 and 7–8 don't depend on them.

---

### Task 2: Galaxy file model, parse, write, validate, hash

**Files:**
- Move: `src/MiniJson.cs` → `shared/MiniJson.cs` (add `public static string Write(object value)`: the inverse
  of `Parse`, indented two spaces, keys in insertion order, numbers invariant culture with "R")
- Create: `shared/Galaxy/GalaxyFile.cs`, `shared/Galaxy/GalaxyCheck.cs`, `shared/Galaxy/Catalogue.cs`
- Modify: `src/StbcRevival.csproj` (add `<Compile Include="../shared/MiniJson.cs" Link="Shared/MiniJson.cs" />`
  and `../shared/Galaxy/*.cs`)
- Create: `tests/Galaxy.Tests/Galaxy.Tests.csproj` (as `tests/Voice.Tests`, linking `shared/MiniJson.cs` and
  `shared/Galaxy/*.cs`), `tests/Galaxy.Tests/GalaxyFileTests.cs`, `tests/Galaxy.Tests/GalaxyCheckTests.cs`

**Interfaces (namespace `StbcRevival.Galaxy`, all `public` so the tool can use them):**
- `GalaxyFile { int Format; string Name; Page Page; List<GalaxySystem> Systems; }`
- `Page { string Label; string Background; }` (`Background`: `"trench-darkened"` or a PNG file name next to the json)
- `GalaxySystem { string Id; string Name; float X, Z; string Template; string Skybox; List<string> SystemTags; List<GalaxyNode> Nodes; }`
- `GalaxyNode { string Id; string Parent; string Name; string Prefab; float X, Y, Z; string Unit; float RotX, RotY, RotZ; List<string> Tags; float LandingRadius; }` (`Unit` one of `Kilometers`, `Skybox`, `Lightseconds`, `AstronomicalUnits`; default `LandingRadius` 1000)
- `static GalaxyFile GalaxyFile.Parse(string json)`: throws `FormatException` with a message naming the system/node id for a structural error.
- `string GalaxyFile.ToJson()`: writes the format in the spec (`position: [x, z]`, node `position: [x,y,z]`, `rotation: [x,y,z]`).
- `static string GalaxyFile.Hash(string json)`: lowercase hex SHA-256 of the UTF-8 bytes with `\r\n` → `\n`.
- `Catalogue { List<CatalogueSystem> TrenchSystems; List<CataloguePrefab> Prefabs; List<string> Tags; List<string> Skyboxes; List<string> SpawnTags; float UniverseMinX, UniverseMinZ, UniverseWidth, UniverseHeight; string Background; }` with `static Catalogue Parse(string)` and `string ToJson()`.
  - `CatalogueSystem { string Name; float X, Z; string Skybox; List<string> SystemTags; List<GalaxyNode> Nodes; }`
  - `CataloguePrefab { string Name; string Kind; double Radius; }` (`Kind`: `star`, `planet`, `moon`, `station`, `asteroids`, `nebula`, `other`)
- `static List<string> GalaxyCheck.Problems(GalaxyFile file, Catalogue catalogue)`: empty when valid.

- [ ] **Step 1: Move MiniJson, write the failing tests** in `GalaxyFileTests`:
  - `RoundTrip_KeepsEverything`: parse the spec's outline (with real values), `ToJson`, parse again, all fields equal.
  - `Parse_RejectsDuplicateSystemIds` / `Parse_RejectsDuplicateNodeIds`: `FormatException` whose message contains the id.
  - `Parse_UnknownUnit_Throws` naming the node.
  - `Parse_MissingOptionalFields_Defaults` (`Skybox` null, `SystemTags` null, `LandingRadius` 1000, rotation 0).
  - `Parse_TrailingCommaOrComment_ThrowsFormatException` (not any other exception type).
  - `Hash_IgnoresLineEndings`: `Hash("a\r\nb") == Hash("a\nb")`; `Hash_DiffersOnContent`.
  - `MiniJson_WriteThenParse_RoundTrips` nested objects, arrays, strings with quotes and unicode, doubles.
- [ ] **Step 2:** `dotnet test tests/Galaxy.Tests` → FAIL (types missing).
- [ ] **Step 3:** Implement `GalaxyFile`, `Catalogue`, `MiniJson.Write`.
- [ ] **Step 4: Failing tests** in `GalaxyCheckTests`, each asserting one message in `Problems`:
  - `UnknownPrefab` → `"kessar/kessar-star: no prefab 'X' in the game"`
  - `UnknownTag` → `"kessar/kessar-star: no tag 'X' in the game"`
  - `UnknownTemplate` → `"kessar: no Trench system 'X' to copy"`
  - `ParentMissing` → `"kessar/moon: parent 'nope' isn't in this system"`
  - `ParentCycle` → `"kessar/a: is its own ancestor"`
  - `NoJumpPoint` → `"kessar: no jump point (a node tagged Player)"` (the tag from `Catalogue.SpawnTags`)
  - `NoStationInRegion` → `"No station anywhere in the region: Patrol can't restock there"`
  - `SystemNameClashesWithTrench` → `"kessar: 'Venov' is already a Trench system"`
  - `SystemsOverlap` (within 2% of universe width) → `"kessar and dorn overlap on the map"`
  - `NoSystems` → `"The region has no systems"`
  - `Valid_NoProblems`
- [ ] **Step 5:** `dotnet test tests/Galaxy.Tests` → FAIL; implement `GalaxyCheck.Problems`; → PASS.
- [ ] **Step 6:** `dotnet build src -c Release` → builds (MiniJson move, net35 compatible: no tuples beyond
  what `LangVersion latest` on net35 already uses in the repo, no `HashSet` APIs missing on 3.5).
- [ ] **Step 7: Commit** `Galaxy file: model, check and hash, shared by the mod and the designer`.

---

### Task 3: The catalogue dump

**Files:**
- Create: `src/Galaxy/GalaxyCatalogue.cs`
- Modify: `src/Plugin.cs` (bind `[Galaxy] CatalogueKey`, call `GalaxyCatalogue.Update()` from the existing
  per-frame update where `MissionMaps.Update()` is called)

**Interfaces:**
- Consumes: `Catalogue`, `CatalogueSystem`, `CataloguePrefab`, `GalaxyNode` (Task 2).
- Produces: `internal static class GalaxyCatalogue { static void Update(); static Catalogue Build(Map.SectorRoot trench, WarpPanel panel /* may be null */); }`

- [ ] **Step 1:** Implement `Build`: Trench systems from `SectorRoot.Systems` (name, link position, skybox path,
  `SystemTags` names, node tree from `SystemNode` → `GalaxyNode` with prefab name, unit, tags from
  `NovaTagList._tagNames`); prefabs = distinct node prefabs plus every `Sector.SectorBody` under
  `Resources.LoadAll("Sector")`, `Kind` from their tags (Star/Planet/Moon/SpaceStation/Asteroid/Nebula, else
  other); `Tags` = every name of the `BLECEHNPNBH` enum; `SpawnTags` = names of
  `MissionManager.NovaPlayerSpawnDefaultTags` and `...AlternateTags`; universe bounds and background from a
  live `WarpPanel` if there is one (its `_universeWidth/Height` and offsets, `_mapBase` image sprite encoded
  as PNG to `trench-map.png` beside the catalogue), else left 0/null.
- [ ] **Step 2:** `Update()`: on `CatalogueKey` down, load the Trench `SectorRoot` (`Resources.Load` of the
  Trench path from `MissionMaps`), build, write `BepInEx/galaxies/catalogue.json`, log
  `Galaxy: catalogue written (<n> systems, <m> prefabs)`.
- [ ] **Step 3: Verify in game:** set `CatalogueKey = F9`, press it at the shuttle and again on the bridge in a
  Patrol mission. Expected: the file parses with `Catalogue.Parse` (quick check: a one-off `dotnet test`
  filter or the tool in Task 7), has every Trench system the warp map shows, and on the bridge also the
  universe bounds and `trench-map.png`.
- [ ] **Step 4: Commit** `Galaxy: catalogue of the Trench, its prefabs and tags, for the designer`.

---

### Task 4: The crew gate and page assignment (pure logic)

**Files:**
- Create: `shared/Galaxy/GalaxyGate.cs`, `tests/Galaxy.Tests/GalaxyGateTests.cs`

**Interfaces:**
- `public sealed class GateResult { public bool On; public string Reason; }`
- `static GateResult GalaxyGate.Decide(bool missionWantsRegion, string regionName, string hostHash, IList<KeyValuePair<string, string>> players /* name, hash or null */)`
- `static bool GalaxyGate.IsRegionSystem(int systemId, int trenchCount)` (`systemId >= trenchCount`)

- [ ] **Step 1: Failing tests:**
  - `Gate_OffWhenMissionDoesNotWantIt` (Reason null: nothing to tell the host)
  - `Gate_OffWhenHostHasNone` (hostHash null or `""`, `""` being what the host publishes with the region off; Reason null)
  - `Gate_OnWhenAllMatch`
  - `Gate_OffWhenOnePlayerMissing` → `Reason == "Example Reach is off: Sam doesn't have it"`
  - `Gate_OffWhenOnePlayerDiffers` → same message shape (an old file counts as not having it)
  - `Gate_TwoPlayersMissing` → `"Example Reach is off: Sam and Alex don't have it"`
  - `IsRegionSystem_Boundary`: `(14, 14)` true, `(13, 14)` false
- [ ] **Step 2:** Run → FAIL; implement; run → PASS.
- [ ] **Step 3: Commit** `Galaxy: who can use the region, and which page a system is on`.

---

### Task 4b: Families and POIs in the galaxy file and its check

From the spike (design: "Revision after the spike"). Every system is ROOT → FAMILY → POI → REGION → bodies.

**Files:**
- Modify: `shared/Galaxy/GalaxyFile.cs`, `shared/Galaxy/GalaxyCheck.cs`, `shared/Galaxy/Catalogue.cs`
- Test: `tests/Galaxy.Tests/GalaxyFileTests.cs`, `tests/Galaxy.Tests/GalaxyCheckTests.cs`

**Interfaces:**
- `GalaxyNode` gains `string MapName; string FamilyBackground; string MapIcon; CopyFamily CopyFamily;` (JSON
  `mapName`, `familyBackground`, `mapIcon`, `copyFamily`; all optional, null when absent, not written when null).
- `public sealed class CopyFamily { public string System; public string Family; }` (JSON `{ "system", "family" }`).
  A node with `copyFamily` needs only `id`, `name`, `parent` (must be null) and optional `position`/`unit`; `prefab`
  is not required for it.
- `CatalogueSystem` gains `List<CatalogueFamily> Families`; `public sealed class CatalogueFamily { public string Name;
  public List<string> Pois; public bool HasSpawn; }` (`HasSpawn`: any node under it carries a `SpawnTags` tag).
- `Catalogue` gains `List<string> Sprites` (names usable as `familyBackground` / `mapIcon`).
- Constants in `GalaxyCheck`: `FamilyPrefab = "Family Group"`, `PoiPrefab = "Point of Interest"`.

- [ ] **Step 1: Failing tests** (messages exact):
  - `RoundTrip_KeepsMapFieldsAndCopyFamily`; `Parse_CopyFamilyWithoutPrefab_Ok`; `Parse_CopyFamilyWithParent_Throws` naming the node.
  - `TopLevelNotFamily` → `"kessar/x: top-level nodes must be families (Family Group) or a copied family"`
  - `PoiNotUnderFamily` → `"kessar/x: a Point of Interest must sit directly under a family"`
  - `NoPoi` → `"kessar: no family with a Point of Interest: the impulse map would be empty"`
  - `UnknownCopyFamily` → `"kessar/x: no family 'F' in Trench system 'S'"` (catalogue present)
  - `UnknownSprite` → `"kessar/x: no sprite 'X' in the game"` (for `familyBackground` or `mapIcon`; catalogue present)
  - `JumpPoint_FromCopiedFamily_Counts` (copied family with `HasSpawn` true satisfies the jump-point check)
  - `KeyedName` → `"kessar: use a plain name, not a text key ('2824|...')"` (system or node name containing `|`)
  - `NullCatalogue_SkipsCopyFamilyAndSprites` (structure checks still run)
  - `Valid_NoProblems` updated to a valid family → POI → region tree.
- [ ] **Step 2:** `dotnet test tests/Galaxy.Tests` → FAIL; implement; → PASS. `dotnet build src -c Release` builds.
- [ ] **Step 3: Commit** `Galaxy file: families, Points of Interest and copied families`.

### Task 4c: Catalogue fixes from the spike (mod)

**Files:** Modify `src/Galaxy/GalaxyCatalogue.cs`.
**Interfaces:** Consumes Task 4b's `CatalogueFamily`, `Catalogue.Sprites`.

- [ ] **Step 1:** List only the Trench's own systems: add `internal static int? TrenchCountOverride` (Task 5 sets it
  to the Trench's own count while a region is appended); when set, take only the first that many systems.
- [ ] **Step 2:** For each system, `Families`: every node tagged FAMILY (or prefab "Family Group") under ROOT, its POI
  children's names, and `HasSpawn`.
- [ ] **Step 3:** `Sprites`: distinct names of every `FamilyMapBackground` and `SystemMapIconSprite` on Trench nodes.
- [ ] **Step 4:** Warp map art: find where the warp panel's background image really is (search `WarpPanel`'s children
  for an `Image`/`RawImage` with a sprite/texture covering `_mapRect`; log what was found) and export it.
- [ ] **Step 5:** Fix the deferred minors: node id suffix collisions; `Resources.UnloadUnusedAssets()` after
  `LoadAll("Sector")`; sRGB blit.
- [ ] **Step 6:** Build; in-game check for the user: F5 on the bridge gives 15 systems, families with POIs, sprites,
  and `trench-map.png`.
- [ ] **Step 7: Commit** `Galaxy catalogue: families, sprites and the warp map art`.

### Task 4d: The designer's system view in families and POIs

**Files:** Modify `tools/galaxy-designer/web/app.js`, `index.html`, `style.css`.
**Interfaces:** Consumes Task 4b's file fields and catalogue `Families`/`Sprites`.

- [ ] **Step 1:** System view: a family list (tabs or a list) above the node tree; "Add family" → empty (prefab
  "Family Group", unit Lightseconds) or "Copy a Trench family" (picker of catalogue families by system, writes a
  `copyFamily` node). The plan shows one family at a time.
- [ ] **Step 2:** Inside a family: "Add POI" (prefab "Point of Interest", with `mapName` and `mapIcon` fields),
  inside a POI: "Add region", inside a region: bodies as before (the POI view's kind choices stay on bodies and
  regions). A copied family shows read-only with its POI names from the catalogue.
- [ ] **Step 3:** Family fields: name, `mapName`, `familyBackground` (sprite picker).
- [ ] **Step 4:** Verify with Playwright against the real catalogue (copy `BepInEx/galaxies/catalogue.json` from the
  game into the testdata folder first): a system with one copied family and one hand-made family → POI →
  region → planet + jump point; Check passes; save; reopen.
- [ ] **Step 5: Commit** `Galaxy designer: families and Points of Interest`.

---

### Task 5: Building the region into the Trench (revise from spike notes first)

**Files:**
- Create: `src/Galaxy/GalaxyRegion.cs`, `src/Galaxy/GalaxyRoom.cs`
- Modify: `src/Plugin.cs` (`[Galaxy] Region`; `Patch` and `Update` calls), `src/Missions/MissionMaps.cs`
  (`FindSpawn`: skip systems where `GalaxyGate.IsRegionSystem(id, GalaxyRegion.TrenchCount)`)
- Create: `tests/Galaxy.Tests/RegionLinksTests.cs` with `shared/Galaxy/RegionLinks.cs`

**Interfaces:**
- Consumes: `GalaxyFile`, `GalaxyCheck`, `GalaxyGate` (Tasks 2, 4, 4b); the hook and node-building recipe from the spike notes (`specs/2026-11-15-galaxy-spike-notes.md`, "Changes implied" 1–4: AssignSystemIds prefix; Instantiate + rewire for copied families; `_position = node.SystemPosition` for new nodes; ROOT node added by the mod; plain names). Sets `GalaxyCatalogue.TrenchCountOverride`.
- Produces:
  - `internal static class GalaxyRegion { static int TrenchCount { get; } static bool Active { get; } static string RegionName { get; } static void Patch(Harmony h); }`
  - `public static class RegionLinks { static void Apply<T>(IList<T> sectorList, IList<T> trench, IList<T> region, bool on); }`: makes `sectorList` exactly `trench` followed by `region` when on, exactly `trench` when off, whatever it held before (generic so it's tested with ints).
  - `internal static class GalaxyRoom { static void Update(); static string HostHash { get; } }`: host sets room `GH` to its hash (or "" when region off in config); every player sets player property `GH` to their own file's hash.

- [ ] **Step 1: Failing tests** `RegionLinks_OnAppends`, `RegionLinks_OffRestores`, `RegionLinks_OnTwiceNoDuplicates`,
  `RegionLinks_OnOffOn_SameAsOn` (Review Focus 2). Run → FAIL; implement `RegionLinks.Apply`; → PASS.
- [ ] **Step 2:** `GalaxyRegion`: on first Trench load, read `BepInEx/galaxies/<Region>.json`, run
  `GalaxyCheck.Problems` against a catalogue built in memory (`GalaxyCatalogue.Build(trench, null)`); log each
  problem and stop if any. Else build each `SystemRoot` per the spike recipe (template `Instantiate`, name,
  `SystemIDTag` = `"Galaxy_" + id`, tags, skybox, nodes) and keep the `SystemLink`s aside.
- [ ] **Step 3:** At the hook, `RegionLinks.Apply(trench.Systems, trenchOwn, regionLinks, on)` with
  `on = GalaxyGate.Decide(selected is Patrol or Free Roam, name, room GH, players' GH).On`; host logs
  `Reason` and shows it once as a game popup, as `CrewPassword.Notify` does
  (`UI.PopupMgr.ShowPopup(text, null, UI.PDHCCBFJODN.Notification, true)` in a try/catch).
- [ ] **Step 4:** `MissionMaps.FindSpawn`: region systems excluded from candidates.
- [ ] **Step 5: Verify in game (solo):** a three-system test file (`assets/galaxies/test-reach.json` written by
  hand from the catalogue). Patrol: region systems on the warp map (page 1 for now), warp there and back,
  Patrol sends a job there. Then an Ongoing Voyage in the same session: the warp map shows only the
  Trench. Then Patrol again: region back, no duplicate buttons. Then break the file (unknown prefab): log
  names it, Patrol loads with the Trench only.
- [ ] **Step 6: Verify (two players):** same file → both see the region. Joiner with the file removed →
  region off for both, host sees `Test Reach is off: <name> doesn't have it`. Then a player **without** the
  file joins a Patrol already running with the region on: find what the game does (the joiner builds the
  Trench without the region, so IDs past the Trench don't exist for them). If they'd desync, refuse the join
  with a popup naming the region, as the mod refuses other mismatches; note the choice in the commit.
- [ ] **Step 7: Commit** `Galaxy: a region of new systems beyond the Trench, for Patrol and Free Roam`.

---

### Task 6: The warp map's second page (revise from spike notes first)

**Files:**
- Create: `src/Galaxy/WarpPages.cs`
- Modify: `src/Plugin.cs` (patch call)

**Interfaces:**
- Consumes: `GalaxyRegion.Active`, `GalaxyRegion.TrenchCount`, `GalaxyRegion.RegionName`, `GalaxyGate.IsRegionSystem`.
- Produces: `internal static class WarpPages { static void Patch(Harmony h); }`

- [ ] **Step 1:** Postfix `WarpPanel.BuildLocations` (only when `GalaxyRegion.Active`): record each
  `WarpMapLocation`'s page; add a page button cloned from `_locationTemplate` (so it takes hand pokes and
  the mouse like the location buttons), labelled with the other page's name (`"The Trench"` /
  `Page.Label`), placed in a corner of `_mapRect` clear of Trench systems.
- [ ] **Step 2:** `ShowPage(panel, page)`: activate only that page's location buttons; swap `_mapBase`'s image
  (page 2: the Trench sprite tinted to 40% brightness, or the file's PNG loaded with `Texture2D.LoadImage`);
  ship icon visible only on its own system's page; the selected destination's marker stays on its page.
- [ ] **Step 3:** Follow the ship: `ShowPage` to the active system's page on `OnShowWarpPanel` and after arrival
  (postfix `OnWarpStateChangedEvent` when the phase is arrival, the branch that calls `SetCurrentLocation`).
  On a cross-page warp, `UpdateShipIconMove`'s target becomes the page edge toward the destination.
- [ ] **Step 4:** Both panels: the helm console's and the viewscreen's (`_viewScreenPanel`) each get their own
  button and page state.
- [ ] **Step 5: Verify in game (VR and desktop):** with the test region: button flips pages; pokes and mouse
  both work; select a region system from page 1 via page 2, flip back and forth (Review Focus 4), engage →
  arrive, page shows the region with the ship; warp back. No button on an Ongoing Voyage or with the region
  off.
- [ ] **Step 6: Commit** `Galaxy: the warp map's second page, with a button to flip between them`.

---

### Task 7: The designer program (server)

**Files:**
- Create: `tools/galaxy-designer/galaxy-designer.csproj` (as bridge-designer's: net8, invariant globalization,
  version from the mod, page embedded; `<Compile Include>` links `../bridge-designer/GameFinder.cs`,
  `../../shared/MiniJson.cs`, `../../shared/Galaxy/*.cs`), `tools/galaxy-designer/Program.cs`
- Create: `tests/Galaxy.Tests/DesignerApiTests.cs` (the request handler, in-process)

**Interfaces:**
- Consumes: `GalaxyFile`, `Catalogue`, `GalaxyCheck` (Task 2); `GameFinder` (bridge-designer).
- Produces (HTTP on 127.0.0.1, default port 8771, same args as bridge-designer: `--game --port --no-open --stay --web`):
  - `GET /api/catalogue` → catalogue.json, or 404 `{ "error": "No catalogue yet: in the game, press <CatalogueKey> at the shuttle (set [Galaxy] CatalogueKey first)" }`
  - `GET /api/trench-map.png`
  - `GET /api/galaxies` → `["test-reach", ...]`
  - `GET /api/galaxy/{name}` / `PUT /api/galaxy/{name}` (body: file json; writes `.bak` first; rejects names outside `[a-z0-9-]+`)
  - `POST /api/check` (body: file json) → `{ "problems": [...] }` (parse errors become one problem)
  - static page from embedded `web/`
- Put the handler in a class `DesignerApi { DesignerApi(string galaxiesDir); (int status, string contentType, byte[] body) Handle(string method, string path, byte[] body); }` so tests drive it without sockets; link `tools/galaxy-designer/DesignerApi.cs` into the test project.

- [ ] **Step 1: Failing tests:** `Catalogue_MissingGives404WithHowTo`, `Put_WritesBakOfPrevious`,
  `Put_RejectsBadName` (`"../x"`, `"A B"`) → 400, `Check_ReturnsProblems`, `Check_BadJsonIsOneProblem`.
- [ ] **Step 2:** Run → FAIL; implement `DesignerApi` and `Program.cs` (listener, open browser, stop when the
  page closes unless `--stay`, as bridge-designer); → PASS.
- [ ] **Step 3: Commit** `Galaxy designer: the program, serving the catalogue and galaxy files`.

---

### Task 8: The designer page

**Files:**
- Create: `tools/galaxy-designer/web/index.html`, `app.js`, `style.css` (follow bridge-designer's look and structure)

**Interfaces:** Consumes the Task 7 API only.

- [ ] **Step 1: Galaxy view:** canvas over the page-2 background (Trench map darkened, or chosen PNG), mapped
  with the catalogue's universe bounds. Add (click empty space), drag, rename, delete systems; template picker
  (catalogue Trench systems); "show the Trench" toggle draws Trench systems faintly.
- [ ] **Step 2: System view:** top-down canvas of one system; nodes listed as a tree beside it; add node from
  prefabs grouped by `Kind`; drag on the plan sets X/Z, a number field sets Y (elevation); unit select;
  parent select (nesting); rotation and landing radius fields.
- [ ] **Step 3: POI view** (panel for the selected node): "Jump point" checkbox (adds/removes the first
  `SpawnTags` tag); kind choices mapped to tags, with what Patrol does there, from `Patrol.cs`'s use of
  `SiteTags`, `NearStation` and the scan/anomaly jobs: Station → `SpaceStation` "restock, rescues";
  Asteroid field → `Asteroid`; Nebula → `Nebula`; Anomaly → `Anomaly` "scans"; Planet/Moon → `Planet`/`Moon`;
  Impulse destination → `ImpulseDestination`; plus a raw tag list (catalogue tags, autocomplete).
- [ ] **Step 4:** Save (PUT), Open (list), New, and Check (POST /api/check, problems listed, click one to select
  its system/node). Unsaved-changes warning on leave.
- [ ] **Step 5: Verify by hand:** `dotnet run --project tools/galaxy-designer -- --web tools/galaxy-designer/web`
  with the catalogue from Task 3: rebuild `test-reach` in the tool, check passes, save, load it in game
  (Task 5 step 5) and it plays the same.
- [ ] **Step 6: Commit** `Galaxy designer: the page, for the galaxy, its systems and their POIs`.

---

### Task 9: Shipping it

**Files:**
- Modify: `tools/package.sh` (copy `assets/galaxies/` to `galaxies/` in the zip; publish galaxy-designer for
  win-x64 and linux-x64 beside the bridge designer as `designer/Galaxy Designer.exe` and
  `designer/galaxy-designer`), `docs/install.sh`, `docs/install.ps1` (put `galaxies/` into `BepInEx/galaxies`,
  without overwriting a user's own files other than the shipped ones), `README.md` (one line under Patrol
  and Free Roam; one line about the designer)
- Create: `assets/galaxies/<region>.json` (the region the user designs; until then `test-reach.json` is not
  shipped and `[Galaxy] Region` defaults to empty)

- [ ] **Step 1:** Update the scripts; run `tools/package.sh`; check the zip has `galaxies/` and both designer
  binaries; install on a clean copy with `docs/install.sh` and see `BepInEx/galaxies/` filled.
- [ ] **Step 2:** `dotnet test tests/Galaxy.Tests` → all pass; `dotnet build src -c Release` → no warnings added.
- [ ] **Step 3: Commit** `Galaxy: ship the designer and the region with the mod`.

---

## Addendum (2026-11-16): designer polish, after the user's walkthrough

The user tried the designer on real data and asked for both the bugs fixed and the tool made easier to use.
Seen: Trench names shown as text keys ("2824|Venov"); background a stretched 64×64 frame; the Open list appears
late; a station-model body shows "Station" unticked; the model picker shows internal names ("Set2ClassY014",
"Ship - SpaceStationProcedural 22"); the system plan mixes AU and km so circles and labels pile up.

### Task 10: Catalogue: readable names, pictures, sprites, the real map art (mod)

**Files:** `src/Galaxy/GalaxyCatalogue.cs` (+ a new `src/Galaxy/GalaxyThumbnails.cs` for the picture taking),
`shared/Galaxy/Catalogue.cs`, `tests/Galaxy.Tests` (catalogue round-trip of the new fields).

**Interfaces (catalogue JSON, all optional so older catalogues still parse):**
- `CatalogueSystem.DisplayName`: the English part of the name ("2824|Venov" → "Venov"); plain names unchanged.
- `CataloguePrefab.Label` (the body's in-game display name in English if it has one — e.g. from its
  TargetInfoComponent / its text key "5796|Space Station" → "Space Station" — else a tidy version of the prefab
  name), `CataloguePrefab.Thumb` (file name of its picture under `galaxies/thumbs/`, or null).
- `Catalogue.SpriteFiles`: map of sprite name → PNG file name under `galaxies/sprites/` (family backdrops, map icons).
- `Catalogue.Background`: the real warp map art, found with `FindMapArt(panel, panelOnly: true)` (the search the
  pages use, which found `WarpPanel/bg_image` in game), exported to `trench-map.png`.

**Pictures:** on the catalogue key, for each prefab with Kind star/planet/moon/station/asteroids/nebula/other,
instantiate it inactive far from everything on a layer no game camera renders, frame it with a temporary camera
(orthographic or perspective fitted to its renderer bounds, plain black background, a light), render 128×128 to a
RenderTexture, save PNG, destroy the instance and camera. Spread over frames (a coroutine, a few per frame) with a
progress log line; skip and log any prefab that throws; never leave an instance behind. Only from the shuttle or
the briefing room (not on the bridge mid-mission). Sprites: export each `FamilyMapBackground` / `SystemMapIconSprite`
through the same RenderTexture copy the map art uses.

**Verify:** build, tests. In game (user): F5 in the shuttle → `galaxies/thumbs/*.png` (a few hundred), `sprites/`,
a big `trench-map.png`; log line with counts; no hitch beyond a few seconds.

### Task 11: Designer: the bugs, a picture picker, the impulse map page view

**Files:** `tools/galaxy-designer/web/*`, `tools/galaxy-designer/DesignerApi.cs` (serve `galaxies/thumbs/*.png` and
`galaxies/sprites/*.png`, names checked like galaxy names), tests for the new routes.

1. Bugs: show `DisplayName` (or the part after "|") for Trench systems everywhere; background = `trench-map.png`
   only if it's larger than 256 px wide, else the plain dark fill; the Open list shows at once (and opens on one
   click); a body whose model is a station shows Station ticked and locked ("from its model"), likewise asteroid
   field / nebula / planet / moon when the model's kind says so.
2. Model picker: a panel of picture tiles grouped by kind (Stars, Planets, Moons, Stations, Asteroid fields,
   Nebulae, Other), each tile the picture (or a kind icon if none) with the readable label and the internal name in
   small print; a search box; recently used first.
3. Plan view: one family at a time drawn as its impulse map page — its backdrop sprite (from `sprites/`), POIs
   placed by their positions relative to the family's centre, scaled to fit (as the game's FamilyRegion does:
   centre point + pixels per metre), with their map icon and map name. Clicking a POI zooms into it: its regions,
   bodies and jump points in kilometres, with a breadcrumb back to the page. Dragging works at each level in that
   level's unit.

**Verify:** Playwright against the user's real catalogue (copy it plus thumbs/sprites into testdata, or point
`--game` at the game folder read-only); screenshots of the picker and both plan levels.

### Change of approach (2026-11-16): the designer reads the game files itself

Main now has an offline reader for the map data (`tools/xref`: Layout.cs, UnityAssets.cs, ReadAssets.cs; notes in
`specs/2026-11-16-map-data-layouts.md`). The user chose: the designer builds its catalogue from the game files on
startup; the game is needed only once, for the model pictures (taken automatically on the first shuttle visit).

### Task 12: The designer reads the game files

**Files:** `tools/galaxy-designer/GameCatalogue.cs` (new), `tools/galaxy-designer/galaxy-designer.csproj` (link the
reader files from `tools/xref` — share them, don't copy — and Mono.Cecil), `DesignerApi.cs`, tests.

- On startup (and on a "Reload game data" request), build a `Catalogue` from `<game>/stbc_Data`:
  the Trench (`SectorRoot` named "0 - Sector - Trench A"): its systems (name, `DisplayName`, link position, skybox,
  system tags, node tree with prefab names, units, tags, map names, sprites), families per system (the shared
  `CatalogueFamilies.From`), every `SectorBody` prefab a Trench node uses plus the others in resources.assets
  (name, kind from prefab tags, radius, `Label` from the body's TargetableComponent `_uiName` text key → English
  where the files give it), all tag names (from `NovaTagList` strings seen plus the game's Tag enum names via Cecil),
  spawn tags (`Player`, `AltPlayerSpawn`), universe bounds (x −265..265, z −115..115 — read from the WarpPanel
  prefab/scene fields if reachable, else these constants), sprites used by nodes exported as PNGs into a cache
  folder, and the "WarpMap" sprite as the background PNG.
- Text keys: "2824|Venov" → "Venov" (the English after "|").
- Merge in the pictures from the in-game run (`BepInEx/galaxies/thumbs/` + its index) when present.
- Cache the result next to the tool (keyed by the game files' sizes and dates) so later starts are instant.
- `GET /api/catalogue` serves it; the old in-game `catalogue.json` is no longer read for game data.
- Tests: against a small fixture is not possible (game files can't be in the repo); unit-test the pure parts
  (text keys, kind from tags, merge of pictures), and a smoke test that runs only when `GAME_DIR` is set.
- Verify with the real game folder: same 15 systems / 55 families / sprites as the in-game catalogue, the WarpMap
  background 2048×1024.

### Task 10b: The in-game catalogue becomes the picture run only

**Files:** `src/Galaxy/GalaxyCatalogue.cs`, `src/Galaxy/GalaxyThumbnails.cs`.
- Remove the bridge map-art capture and the worker-thread map encoding; the automatic shuttle run takes only the
  model pictures and writes `galaxies/thumbs/` plus a small index (`thumbs.json`: prefab name → file, format).
- F5 stays as a manual re-take of the pictures. Keep every safety behaviour reviewed in Task 10.

### Rescope (2026-11-16)

The user: the galaxy designer is a development tool for them only — it isn't shipped (no release zip, installers or
README for it) and needs no other-player testing. Task 9's designer packaging is dropped. Whether a region designed
with it ships with the mod is decided separately, when there is one.
