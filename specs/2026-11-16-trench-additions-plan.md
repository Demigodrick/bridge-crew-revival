# Trench Additions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task by task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** New families (with POIs, regions, bodies, jump points) added to existing Trench systems from the galaxy file,
in Revival Patrol and Free Roam only, designed in the galaxy designer.

**Architecture:** The galaxy file gains a `trench` section (shared/Galaxy). The mod, at the region's AssignSystemIds
hook, swaps each affected Trench `SystemLink.System` for a modified copy (original content + new families) for
Patrol/Free Roam, and back to the original for other missions — the same pattern as `RegionLinks`. The designer opens
Trench systems with their existing families read-only and edits the additions.

**Tech Stack:** C# net35 (mod, BepInEx/Harmony, Unity 5.4), net8 (designer, tests, xunit), plain JS + canvas (page).

**Spec:** `specs/2026-11-16-trench-additions-design.md`. Earlier work this builds on: `specs/2026-11-15-galaxy-*`
(design, plan, spike notes), `specs/2026-11-16-map-data-layouts.md`.

## Global Constraints

- Additions apply only when the Trench is loaded **and** the mission is Revival Patrol or Free Roam, and only when the
  crew agrees on the file (existing GalaxyGate / GA / GH rules, unchanged). Every other mission sees the Trench as it ships.
- Add only: nothing existing in a Trench system is moved, changed or removed; system IDs and warp-map positions unchanged.
- The shared Trench assets (`SectorRoot`, `SystemRoot`, `SystemNode`) are never modified; copies only.
- Any problem in the file (region or additions) → the whole file is off, Trench only, one log line per problem.
- A file may contain only additions (no `systems`); the "region has no systems" check must not fire then.
- Default config (`[Galaxy] Region` empty) costs nothing, as now.
- The designer is a development tool for the user alone (not shipped).

## Review Focus

1. An addition naming a Trench system that doesn't exist (or by its plain name instead of the catalogue's "2824|Venov")
   → a clear problem naming it, file off. → Task 1 tests (`Trench_UnknownSystem`, `Trench_PlainNameMatches`).
2. A file with additions only → valid, loads, no region page/button. → Task 1 (`AdditionsOnly_Valid`), Task 2 manual step.
3. Patrol, then an Ongoing Voyage, then Patrol in one session → the swapped systems revert and re-apply cleanly, no
   duplicate families. → Task 2 (`TrenchSwap_*` tests) and manual step.
4. Additions in a system the crew starts in or warps to first → its impulse map shows both the old and the new families;
   warp-in points of the original still work. → Task 2 manual step.
5. Hand-edited file with an addition whose top-level node isn't a family → the same structural problem as for region
   systems, naming the Trench system and node. → Task 1 (`Trench_TopLevelNotFamily`).

---

### Task 1: The `trench` section in the galaxy file and its check

**Files:** Modify `shared/Galaxy/GalaxyFile.cs`, `shared/Galaxy/GalaxyCheck.cs`; Test `tests/Galaxy.Tests/TrenchAdditionsTests.cs`.

**Interfaces:**
- Produces: `public sealed class TrenchAddition { public string System; public List<GalaxyNode> Nodes = new List<GalaxyNode>(); }`;
  `GalaxyFile.Trench` (`List<TrenchAddition>`, JSON key `trench`, omitted when empty). Node ids unique within one
  addition; parse errors name `trench/<system>/<node>`.
- `GalaxyCheck.Problems` checks each addition with the same node rules as region systems (shared code, not copied),
  prefixing problems with `trench <DisplayName>/<node>:`; plus `trench <name>: no Trench system '<name>'`.
  System matching: exact catalogue name, or the plain name after "|" (case-insensitive); the file stores whatever was
  given; Task 2 resolves it the same way. The "region has no systems" problem only when both `Systems` and `Trench` are empty.
  Jump-point and station checks stay per region system (additions don't need their own jump point: the Trench system
  already has one).

- [ ] **Step 1: Failing tests:** `RoundTrip_KeepsTrenchAdditions`, `Parse_TrenchDuplicateNodeId_Throws` (message names
  `trench/2824|Venov/x`), `Trench_UnknownSystem` → `"trench Nope: no Trench system 'Nope'"`, `Trench_PlainNameMatches`
  ("Venov" matches "2824|Venov"), `Trench_TopLevelNotFamily` → `"trench Venov/x: top-level nodes must be families (Family
  Group) or a copied family"`, `Trench_UnknownPrefab`, `AdditionsOnly_Valid` (no systems, one valid addition → no
  problems), `Empty_File_StillNoSystems` (no systems, no additions → `"The region has no systems"`).
- [ ] **Step 2:** `dotnet test tests/Galaxy.Tests` → FAIL; implement; → PASS. `dotnet build src -c Release` builds.
- [ ] **Step 3: Commit** `Galaxy file: additions to the Trench's own systems`.

### Task 2: In the mod: swap Trench systems with additions for Patrol/Free Roam

**Files:** Modify `src/Galaxy/GalaxyRegion.cs` (and its node-building helpers; split out `src/Galaxy/TrenchAdditions.cs`
if GalaxyRegion grows past ~700 lines); Create `shared/Galaxy/LinkSwap.cs`; Test `tests/Galaxy.Tests/LinkSwapTests.cs`.

**Interfaces:**
- Consumes: Task 1's `GalaxyFile.Trench`; existing `GalaxyRegion` build path (`Build`, `BuildSystem`, `GameParts`,
  copied-family recipe, node/transform creation), `RegionLinks`.
- Produces: `public static class LinkSwap { public static void Apply<TLink,TSys>(IList<TLink> links, Func<TLink,TSys> get,
  Action<TLink,TSys> set, IDictionary<TSys,TSys> originalToCopy, bool on); }` — when on, each link whose current
  system is a key gets the copy; when off, each link whose current system is a copy value gets its original back;
  idempotent (generic so it's tested with plain objects).

- [ ] **Step 1: Failing tests** `TrenchSwap_OnReplaces`, `TrenchSwap_OffRestores`, `TrenchSwap_OnTwice_NoChange`,
  `TrenchSwap_OnOffOn_SameAsOn`, `TrenchSwap_UnrelatedLinksUntouched`. Run → FAIL; implement `LinkSwap`; → PASS.
- [ ] **Step 2:** In the build (once per file hash, like the region): for each addition, resolve the Trench system
  (Task 1's matching rule) from the Trench's own links; make `copy = Object.Instantiate(original)`; give the copy the
  original's existing SystemTransforms **by reference** plus new transforms for the addition's nodes built with the
  region's recipe, the new families parented under the original's ROOT node's transform (do not modify the original
  ROOT node object: if parenting needs the ROOT node's `_children` changed, make a copy of the ROOT node for the copy
  system, as the region does, and point the existing top-level families' parent references only through the copy's
  transform tree — read the spike notes and the region code; choose and log). Keep the original's name, SystemIDTag,
  tags, skybox and lighting. Validation and resolution happen before anything is built (all-or-nothing, with the region).
- [ ] **Step 3:** At the AssignSystemIds hook, after `RegionLinks.Apply`, call `LinkSwap.Apply(map.Systems, …, copies,
  on)` with the same `on` decision; on the "off"/other-mission path, `LinkSwap.Apply(..., false)` so a session that ran
  Patrol and then another mission restores the originals. Log `Galaxy region: Trench additions on: <n> families in
  <systems>` / `... off`.
- [ ] **Step 4:** If reusing the original transforms by reference misbehaves in the checks below, fall back to copying
  the original's whole node tree for the copy (the spike's CopyNode); note which in the report.
- [ ] **Step 5:** Build, tests. **In-game check (user):** a test file `assets/galaxies/test-additions.json` (additions
  only: Venov gets one hand-made family "Venov Outpost" → POI → region → station + jump point; Silva gets a copied
  family) — instructions: set `[Galaxy] Region = test-additions`, host Patrol, warp to Venov, the impulse map shows
  Venov's five families plus "Venov Outpost"; impulse to the new station; Venov's original start/warp-in points still
  work; Patrol can offer a job there; then an Ongoing Voyage in the same session → Venov has its five families only;
  Patrol again → the addition is back, no duplicates.
- [ ] **Step 6: Commit** `Galaxy: additions to the Trench's own systems, for Patrol and Free Roam`.

### Task 3: The designer edits Trench systems

**Files:** Modify `tools/galaxy-designer/web/app.js`, `index.html`, `style.css`.

**Interfaces:** Consumes Task 1's file shape (`trench: [{system, nodes}]`) and the catalogue's Trench systems/families.

- [ ] **Step 1:** Galaxy view: double-click a Trench system (they're drawn faintly) opens it in the system view. Trench
  systems with additions get a visible mark (e.g. a small "+n" badge) on the map and in a "Trench additions" list in the
  side panel.
- [ ] **Step 2:** System view for a Trench system: its existing families listed read-only (names and POI names from the
  catalogue, each drawn on its own impulse-map page read-only, as copied families are now), plus "Add family (empty)" /
  "Copy a Trench family", editing exactly as for region systems (POIs, regions, bodies, jump points, model picker,
  plan levels). No template/position fields (a Trench system's own are used). Saving writes the addition under
  `trench` with the system's catalogue name; an addition with no families is dropped on save.
- [ ] **Step 3:** Check results naming `trench <name>/<node>` select that system and node when clicked.
- [ ] **Step 4: Verify** with Playwright on the real game folder (read-only; a scratch galaxies folder): add a family to
  Venov, save, reopen, Check passes; the badge shows; a region system still edits as before. Screenshots.
- [ ] **Step 5: Commit** `Galaxy designer: additions to the Trench's own systems`.
