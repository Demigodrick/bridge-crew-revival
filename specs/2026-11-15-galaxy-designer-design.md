# Galaxy designer: a second region beyond the Trench

## Intent

More places to discover in the Revival **Patrol** and **Free Roam** missions, without leaving the Trench.
Crews warp from Trench systems to new, hand-designed systems the same way they warp anywhere else. The new
systems hold "similar stuff" to the Trench: Patrol's jobs (raiders, freighters, rescues, restocking, scans)
land there because the systems have the same kinds of places Patrol looks for.

To build those systems we want a designer tool, in the spirit of `tools/bridge-designer`. It works in three
layers that match how the game itself is built:

1. **Galaxy:** where each new system sits on the helm's warp map.
2. **System:** what's in it: star, planets, stations, asteroid fields, nebulae, anomalies, and the jump
   (warp arrival) points.
3. **POI:** what kind of place each point of interest is, which decides what Patrol can send crews there for.

## Goals

- New systems appended to the Trench at load, for Patrol and Free Roam missions, reachable by warp from any
  Trench system and back.
- A second page on the helm warp map, holding the new region, with a button to flip between the Trench and it.
- Patrol picks new systems and their sites for its jobs like any Trench system.
- A browser-based designer that writes one galaxy file; the mod reads that file.
- The same experience for every crewmate in multiplayer.

## Non-goals (for this version)

- Any other mission: Ongoing Voyages, Stellar Evacuation, Last Stand, Fleet Battle, Kobayashi Maru, PvP.
  On those, the Trench is exactly as today. (Later versions may open the region to them.)
- Starting a mission in the new region: missions start in the Trench, and crews find the region by warping.
- Warps that take longer between regions: a warp to the region is like any other warp.
- A separately loaded sector, or swapping maps mid-mission.
- New art: models, skyboxes and icons come from the game's own.
- Hand-scripted contents at a POI (a specific fleet at a specific spot, a story). The mod's own spawning
  makes that possible later.
- More than one extra region at a time.

## Anti-goals

- The Trench must look and play exactly as before on every other mission, when no galaxy file is present,
  or when the crew can't all use it. No shifted system IDs, no moved Trench buttons.
- A crew must never desync because one player has a different galaxy file (or none): that freezes or splits
  the mission.
- No crash or frozen load from a bad file. A broken file is logged and ignored, never half-applied.

## How the game works today (what this builds on)

- `Map.SectorRoot` (a ScriptableObject loaded from Resources, e.g. the Trench) has `Systems`: a list of
  `SystemLink { Vector3 Position; SystemRoot System }`. `Position` x/z is where the system sits on the
  galaxy map.
- `SectorMap.AssignSystemIds` sets each `SystemRoot.SystemID` to its index in that list. So **appending** systems
  keeps every Trench ID as it is.
- `SectorMap.InitAllSystems` builds every system of the sector up front (`SystemRoot.MakeRunTimeClone`, which
  already uses `Object.Instantiate` on the asset). Warping is `SectorMap.ChangeActiveSystem(id)`: one map, the
  active system switches. Nothing is loaded from disk per system beyond its skybox.
- Every warp takes the same fixed time (`GenericWarp._warpTime`); system positions only place the warp
  map's buttons.
- `Map.SystemRoot`: name, `SystemIDTag`, `SystemTags`, skybox path, ambient and fill light, warp map icon and
  background sprites, `SystemTransforms` (a tree built from `Map.SystemNode`s).
- `Map.SystemNode`: position (with a distance unit), rotation, body `Prefab` (`Sector.SectorBody`), `Tags`
  (`NovaTagList`), parent/children, landing radius, map icon and name.
- `MissionMaps.Generate_Prefix` sets Revival missions up without Ongoing Voyage encounters and picks the
  starting system. For Patrol it shuffles all systems ("starts anywhere").
- Patrol (`src/Missions/Patrol.cs`) picks its next system with `PickSystem` and its sites by body tags
  (`SiteTags`: ImpulseDestination, Planet, Moon, SpaceStation, Asteroid, Anomaly, Nebula, ...); restocking
  needs a SpaceStation (or a station-named Satellite) nearby.
- `WarpPanel.BuildLocations` makes one button per system in `AllSystems`, placed by
  `UniverseToMapPosition(system.Position)` over a fixed universe width and height, on `_mapBase`. There are
  two kinds of warp panel: the helm console's and the viewscreen's (`_viewScreenPanel`).

## Approaches considered

1. **Extend the Trench, second page on the warp map (chosen).** Append the new systems to the Trench's
   system list before the sector initialises. On the warp panel, show only one region's buttons at a time,
   with a page button to flip. It keeps the Trench map as it is and gives the new region its own space. Cost:
   a new VR button on a console, and keeping two sets of buttons in step with the game's own button logic.
2. **Extend the Trench on one map.** Squeeze the new systems into the existing panel, or shrink the Trench to make room.
   Cheaper, but it clutters the Trench map and puts new systems over Trench background art.
3. **A separately loaded sector.** Loading another sector mid-mission with every crewmate still in it means
   unloading the whole map, networked. Far riskier for the same result. Dropped.

The user picked 1.

## Design

### The galaxy file

One JSON file, `BepInEx/galaxies/<name>.json` in the game folder, shipped with the mod release (the repo
keeps it under `assets/galaxies/`, and `tools/package.sh` adds it). Only one is used at a time, chosen in config
(`[Galaxy] Region = <name>`, default the shipped one; empty = off). Outline (comments for this doc only):

```json
{
  "format": 1,
  "name": "Example Reach",
  "page": { "label": "Example Reach", "background": "trench-darkened" },
  "systems": [
    {
      "id": "kessar",                  // stable, unique in the file
      "name": "Kessar",
      "position": [x, z],              // where it sits on the second page, in the Trench's universe units
      "template": "Venov",             // the Trench system to copy look and feel from: skybox, lights,
                                       // system tags, map sprites (overridable below)
      "skybox": null,                  // optional: another system's skybox path, from the catalogue
      "systemTags": null,              // optional: replace the template's system tags
      "nodes": [
        {
          "id": "kessar-star", "parent": null, "name": "Kessar",
          "prefab": "<SectorBody prefab name from the catalogue>",
          "position": [x, y, z], "unit": "AstronomicalUnits",
          "rotation": [x, y, z],
          "tags": ["<tag names from the catalogue>"],
          "landingRadius": 1000
        }
      ]
    }
  ]
}
```

A **jump location** is a node with the game's warp-arrival/player-spawn tag. A **POI** is a node with one of the
tags Patrol looks for. The tool presents these as plain choices ("Jump point", "Station: restock, rescues",
"Asteroid field", "Nebula", "Anomaly: scans", "Impulse destination") and writes the tags. The builder
maps each choice to what Patrol actually does with it, from `Patrol.cs`.

### The catalogue (the tool's source of game data)

The game's ScriptableObjects can't be read offline (`src/AssetFile.cs` only knows meshes; the files carry no
type information). So the mod writes a catalogue from inside the game, on demand (a key or a
config switch, like the existing `GameDump`), to `BepInEx/galaxies/catalogue.json`:

- the Trench: every system's name, position, template data and node tree (so the tool can show the Trench
  for reference and offer each system as a template);
- every `SectorBody` prefab a Trench node uses (and any others found under Resources), with radius and a type;
- every tag used on Trench nodes and systems, and where;
- skybox paths; warp map universe bounds; the warp map background sprite exported as PNG.

The tool reads the catalogue; without one it says how to make it.

### In the mod: building the systems

A new `src/Missions/GalaxyRegion.cs` (or a `src/Galaxy/` folder if it grows):

- **When:** only when the loaded map is the Trench **and** the mission is Revival Patrol or Free Roam
  (`RevivalMissions.Selected` is `PatrolSequence` or `DebugExplorationModeSequence`), once the Trench
  `SectorRoot` is loaded and before `SectorMap.InitAllSystems`/`AssignSystemIds`. The builder finds the exact
  hook; an `InitAllSystems` prefix is the fallback.
- **What:** for each system in the file, `Object.Instantiate` its template `SystemRoot`, rename it, set
  `SystemIDTag`, tags and skybox, and replace its `SystemTransforms` with ones built from new `SystemNode`s
  (`ScriptableObject.CreateInstance`), resolving prefabs and tags by name. Append a `SystemLink` with its
  position.
- **Only for those missions:** the `SectorRoot` is a shared asset that stays in memory between missions. The
  region's links are built once per session and kept aside; they are appended before init for Patrol and
  Free Roam and removed before init for anything else, so every other mission sees the Trench's own list.
- **Starts stay in the Trench:** `MissionMaps.FindSpawn` (Patrol's "start anywhere" shuffle and the fallbacks)
  only considers Trench systems.
- **Validation first:** every prefab, tag and template is resolved before anything is changed. Anything
  missing, the whole file is skipped with a log line naming what's missing.

### Multiplayer

Every player builds the map on their own PC, and system IDs are list positions, so all players need an
identical list. The host puts the galaxy file's hash in the room (as `MissionMaps` does with `RM`).
The region is on for a mission only when the host has it on and every player in the room reports the same
hash. Otherwise it is off for everyone for that mission, and the host sees one line saying why ("Example Reach
is off: Sam doesn't have it"). A player joining mid-mission without it is handled like other mod mismatches
(`ModVersionCheck`); the builder checks how a join mid-mission behaves and picks the safe option.

### The warp map's second page

- Every `WarpMapLocation` built by `BuildLocations` belongs to page 1 (Trench) or page 2 (new region), by
  system ID (IDs at or above the Trench's count are page 2).
- A page button on the warp panel, both the helm console's and the viewscreen's, shown only when the region
  is on. It shows only the current page's buttons and swaps the background (`_mapBase`'s image): page 2 uses
  the file's background choice, by default the Trench art darkened, or a PNG shipped with the file.
- New-region positions are mapped over the same universe bounds as the Trench, so the page-2 layout reuses
  `UniverseToMapPosition` as it is.
- The page follows the ship: it flips to the region the ship is in when the map opens and after arriving.
  The ship icon shows on its own region's page only. A selected warp destination on the other page stays
  selected (and its marker shows on that page).
- The ship icon on a cross-region warp flies to the edge of its page during the warp, and appears at the
  destination on the other page on arrival.
- Hand-interaction: the button must work with VR hand pokes the way the location buttons do, and with
  the desktop mouse.

### The designer tool

`tools/galaxy-designer/`, built like `tools/bridge-designer`: a small .NET program that serves a page on
127.0.0.1, finds the game folder the same way (`GameFinder.cs`, shared), and reads and writes
`BepInEx/galaxies/`. Saves keep the previous file as `.json.bak`.

Three views:

- **Galaxy:** the second page's map, drawn over the chosen background, with the Trench's systems shown
  faintly on a toggle for reference. Add, drag, rename, delete systems; pick each one's template.
- **System:** a top-down plan of one system with an elevation value per node, distances in the node's unit.
  Add nodes from the catalogue's prefabs (grouped: stars, planets, stations, asteroid fields, other), nest
  them (a station orbiting a planet), set rotation and landing radius.
- **POI:** for the selected node: is it a jump point, and what kind of place it is for Patrol, as named
  choices over tags, with a raw tag list for anything else.

A check button runs the same validation the mod does (unknown prefabs or tags, a system with no jump point,
a region with no station for restocking, duplicate ids, systems overlapping on the map) and lists the problems.

## The order of work

1. **Spike (throwaway):** in a test build, copy one Trench system by hand in code, append it for a Patrol
   mission, and warp to it and back, on its own and then with a second player. Check that Patrol sends a
   job there. This settles the load hook and `SystemNode`/`SystemTransform` building. Its lessons go into
   the plan.
2. Catalogue dump.
3. Loading a galaxy file for Patrol and Free Roam, with validation, and the multiplayer hash check.
4. The warp map's second page.
5. The designer tool.
6. The first region, designed with the tool.

## Calls made without the user (open to change)

- Missions start in the Trench; the region is found by warping.
- POIs offer what Patrol does with their tags; hand-scripted contents wait for a later version.
- One extra region at a time, chosen in config, shipped with the mod.
- The region turns off for the whole crew if anyone lacks the identical file, rather than blocking the join.
- Catalogue dumped from inside the game, rather than read offline.
- System view is 2D top-down with an elevation number, not a 3D editor.
- Page 2 background defaults to the Trench art darkened.
- The ship icon flies to its page's edge on a cross-region warp.

## Left to the builder

- The exact hook point before system init, and how a mid-mission join without the file is handled.
- How to build `SystemTransform`s for new nodes (the spike will show).
- The page button's look and placement on each warp panel.
- The named POI choices in the tool, from what Patrol does with each tag.

## Revision after the spike (2026-11-15)

The spike (`specs/2026-11-15-galaxy-spike-notes.md`) proved the approach in game: an appended system shows on
the warp map, warps there and back work, Patrol gives jobs in it, other missions see the Trench untouched. It
also showed one thing this design missed:

**Every system is ROOT → FAMILY → POI → REGION → bodies.** The impulse map is built from families (an impulse
map page, with its backdrop sprite) and the POIs inside them (named, clickable spots); regions hold the bodies,
spawn points and warp destinations. That is the user's own "system → jump location/POI → what's in it", with
the family as the page that groups POIs. So:

- **Galaxy file:** nodes stay one tree per system, plus three optional node fields: `mapName` (the label on the
  impulse map), `familyBackground` and `mapIcon` (sprite names from the catalogue). And a node may be
  `{ "copyFamily": { "system": "<Trench system>", "family": "<family node name>" }, "id": ..., "name": ... }`:
  a whole Trench family copied as new nodes (proven in the spike) and renamed.
- **Check:** top-level nodes are families (prefab "Family Group" or a copyFamily); POIs (prefab "Point of
  Interest") sit directly under a family; each system has at least one family with a POI; the jump-point
  check counts copied families' spawn points. The mod adds the ROOT node itself.
- **Catalogue:** only the Trench's own systems; each family listed by system and name with its POIs (for
  copyFamily); the sprite names nodes use (`FamilyMapBackground`, `SystemMapIconSprite`); the warp map art from
  wherever it really is.
- **Designer, system view:** organised as families → POIs → regions → bodies. "Add family" makes an empty one or
  copies a Trench family. The plan view shows one family at a time, like the impulse map.
- **Names:** plain names everywhere (a `"2824|..."` text key shows as the original's name).
