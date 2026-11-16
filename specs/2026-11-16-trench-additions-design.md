# Trench additions: new families and POIs in existing Trench systems

## Intent

The user wants to add to the Trench's own systems (e.g. a new family with a station and an asteroid field in Venov),
designed in the galaxy designer, without changing or removing anything that's already there. It's for the same purpose
as the region: more to discover in Revival Patrol and Free Roam. The designer stays a development tool for the user.

## Decided with the user

- Additions apply only to **Revival Patrol and Free Roam**, like the region; every other mission sees the Trench as it ships.
- Add only: new families (hand-made or copied from another Trench system) under an existing system's ROOT, with their
  POIs, regions, bodies and jump points. Nothing existing is moved, changed or removed.

## Design (my calls, open to change)

- **Same galaxy file.** A galaxy file gets an optional `trench` section: `[{ "system": "<Trench system name as in the
  catalogue, e.g. 2824|Venov>", "nodes": [ ...the same node format as region systems... ] }]`. The nodes' top level must
  be families, exactly as in a region system. One file = one variant of the map; `[Galaxy] Region` turns all of it on.
  A file may have only additions and no new systems.
- **Same checks and crew rules.** GalaxyCheck validates the additions like region nodes (structure, models, tags,
  sprites, copied families; plain names; a Trench system must exist). The file hash covers the additions, so the crew
  agrees or leaves exactly as now. Any problem → the whole file is off (Trench only).
- **In the mod: swap, don't edit.** For Patrol/Free Roam, before the sector initialises (the region's AssignSystemIds
  hook), each Trench `SystemLink` that has additions gets a modified copy of its `SystemRoot` (Instantiate, then its
  own nodes and transforms rebuilt as in the region code: the original's transforms reused by reference, plus the new
  nodes/transforms under the copied ROOT), and the link points at the copy. For any other mission the links point back
  at the originals (as RegionLinks restores the Trench list). The shared Trench asset itself is never modified, and the
  system keeps its ID and position.
- **Designer.** Double-clicking a Trench system on the galaxy map opens it like a region system: its existing families
  shown read-only (names and POIs from the catalogue, drawn on their own impulse-map pages), plus "Add family" /
  "Copy a Trench family" for new ones, edited exactly like region families. Trench systems with additions get a mark on
  the galaxy map. Saving writes them to the file's `trench` section.
- **Impulse map / Patrol.** New families appear as extra impulse-map pages of that system; Patrol's job picker sees
  their stations, asteroid fields, anomalies etc. by tag, as it does for the region.

## Non-goals

Moving, changing or removing existing Trench content; Ongoing Voyages or other missions; moving systems on the warp map.

## Risks to check in game

The copied SystemRoot must behave identically for its existing content (impulse map, encounters Patrol uses, warp-in
points); the spike proved instantiate-and-rebuild for whole systems, but not "original transforms reused alongside new
ones" — if reuse misbehaves, rebuild the original's nodes as copies too (the spike's CopyNode does this).
