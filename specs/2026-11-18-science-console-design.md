# Science console: walk-up scans that upgrade the ship (Patrol)

## Intent

A new bridge position, the science officer, that isn't a seat. Each bridge gets a science console at a fixed
spot. Any crew member can stand up (Bridge Walk, on by default), walk over and use it. The console scans things in
space, and each finished scan gives the ship a small upgrade that the crew notices in the next fight.

The point is the trade-off: the person at the science console has left their own station empty. To make that
trip worth it, enemies get a little stronger after every completed assignment, so a crew that never scans slowly
falls behind. A crew that scans regularly can stay ahead indefinitely.

## Decided with the user

- **A walk-up console, not a role.** There's no briefing-room slot and no NPC. It sits at a chosen spot on each
  bridge and ships with the mod, so every crewmate sees it in the same place.
- **A different kind of scan from Tactical's "scan target".** Tactical keeps its own scan.
- **Patrol only, to start.** Upgrades last for the Patrol session and reset when it ends, so they have to be
  earned again every time.
- **Upgrades are felt in fights.**
- **Enemies get small improvements after each completed assignment** (not on a clock), and the crew must be able
  to stay ahead by scanning.
- **Destroyed enemies leave something to scan.** The game removes wrecks and they can't be targeted, so this has
  to be added.
- **There are three mini games: match the signal, line up the sensor sweep, and find the right numbers.** Each
  scan picks **two of the three at random**, and both must be **held matched at the same time**.
- **No passive progress.** The scan only moves while both mini games are matched; otherwise it stops.
- **Build approach A:** a touchscreen made from the game's own console parts (below).

## Approaches considered (for the console screen)

- **A. A touchscreen from the game's console parts (chosen).** Buttons and sliders are copies of the game's own
  console widgets (HandInteraction + Toggle/slider), made the way `WarpPages` copies the helm's IMPULSE button.
  VR fingertip pokes and drags and the desktop mouse already work with them. The mod draws the mini game graphics
  (waveforms, rings, meter) onto a texture on the screen. It looks native and reuses input that already works in
  VR, on monitors and through Meta Link. The downside is that controls are drags on a screen, not physical knobs.
- **B. A custom panel with grab-and-twist knobs.** The most tactile, but it needs new grab code for VR, a separate
  mouse scheme, has the highest risk across headsets, and looks out of place on the bridge.
- **Ruled out: taking over an existing spare console** (such as the Enterprise-D backwall science terminal).
  Most bridges have none, and the game ties consoles to stations and seats.

## Design

### The console on the bridge

- **Mesh.** A console piece borrowed from one of the game's bridges and placed with `BridgeParts`, positioned per
  bridge (Aegis, Enterprise, Enterprise-D, and the mod's own ships' bridges). The user picks each spot in the
  bridge designer. The positions ship with the mod as default data, separate from players' own layout files, so
  a player's personal layout never moves or hides the science console.
- **Screen.** A screen surface on that piece, built from copied game console widgets plus mod-drawn textures for
  the mini games.
- **Using it while standing.** The game blocks console input while standing. The science console is the
  exception:
  - **VR:** reach out and poke or drag the screen, the same as at a seated station.
  - **Monitor:** within reach of the console, a prompt shows "E: use science console". Pressing E locks the view
    onto the screen and frees the mouse for the controls. Esc (or E) goes back to walking.
- **Standing only (my call).** The console takes input only from a player who's standing, even if a VR seat is
  within arm's reach of it. Leaving your station is the point.
- **Key clashes (builder's).** While standing, Esc already sits you down, and E is the look-right fallback in
  `BridgeWalk.cs`. The builder picks keys that don't clash.
- **One user at a time.** While someone is using it, the console shows who for everyone else, and it won't take
  input from anyone else.

### Screens

```
┌─ SCIENCE ───────────────────────────┐
│ In range:                           │
│  ▸ Unidentified reading   4,200 km  │
│    Klingon debris         1,100 km  │
│    Stellar reading        (site)    │
├─────────────────────────────────────┤
│ [ mini game 1 ]   [ mini game 2 ]   │
│ SCAN ████████░░░░░░  58%  LOCKED    │
│ Upgrades: Weapons +10%  Shields +5% │
└─────────────────────────────────────┘
```

1. **Target list.** Everything science-scannable within sensor range, with its distance. Tap one to start.
2. **Scan.** The two mini games picked for this scan, side by side, with one shared scan bar. The bar moves
   only while **both** games are at **70% match or better**. A closer match fills it faster. When either drops
   below 70%, it stops; it never goes backwards. Each game's target drifts slowly, so you have to keep tuning to
   hold it.
3. **Out of range.** If the target leaves sensor range, the scan pauses and keeps its progress, and the console
   says the helm needs to close in. The mini games picked for that target stay the same if the scan is resumed.
4. **Walking away mid-scan (decided with the user).** Leaving the console, sitting down or walking off keeps the
   scan's progress and its two mini games with that target. Any crewmate can pick the target later and carry on
   from there.
5. **Summary line.** The crew's current upgrades are always shown.

### The mini games (as in the playable mockups)

- **Match the signal.** Frequency, amplitude and phase sliders. Your waveform has to sit on top of a noisy
  reading.
- **Line up the sensor sweep.** Three rings, each with a gap, have to line up on a target mark. Turning a ring
  nudges its neighbour by about a third as much.
- **Find the right numbers.** Band, gain and polarity sliders. You can't see the answer, only a signal-strength
  meter and a noisy readout that gets clearer near the peak.

The mockups are in `/home/patrick/stbc/.superpowers/brainstorm/437533-1795043084/content/science-minigames-v4.html`
(ignored by git, in the main checkout only). Tuning (how much
targets drift, how fast the bar fills, the 70% threshold) is set by playtesting. The goal is about 15–25 seconds (doubled from 30–45 after playtesting)
for a competent player.

### What can be scanned (Patrol)

- **Readings at every assignment site.** When the ship arrives at a site, a few science-scannable objects are
  spawned nearby. They reuse the Explore props (`Prop - ScannableObjectA–D`) and their four kinds:
  - **Protomatter:** weapons
  - **Graviton stream:** shields
  - **Verteron pocket:** impulse and turning
  - **String fragment:** repair speed
- **Debris from destroyed enemies.** When an enemy ship is destroyed, a scannable debris prop is spawned where it
  died, tagged with its faction. It stays until it's scanned or the ship leaves the system. Scanning it improves
  **weapons against that faction**. The game removes the destroyed ship itself as it always has.
- A science-scannable object can be scanned once. It disappears from the list when its scan is finished.
- The Research assignment is unchanged. Its objects stay Tactical's scanning job.

### Upgrades and enemy steps

- **Crew:** each finished scan gives +5% to its area.
  - Weapon damage dealt
  - Shield strength
  - Impulse speed and turning
  - Repair speed
  - Weapons against one faction
  - Starting cap: +50% per area. The cap only exists to stop runaway values and has to stay above anything
    the enemy reaches in a realistic session (see below).
- **Enemies:** after each completed assignment, enemies get +3% in one area, rotating through damage dealt,
  shield strength and speed. This adds to Patrol's existing "more raiders as assignments are completed".
- **Balance target:** about one scan per assignment keeps the crew slightly ahead. With no scans, the crew falls
  behind slowly. All values are set by playtesting.
- **Announcements:** on the objectives panel, like Patrol's other short notices:
  - "Graviton stream analysed: shields +5%"
  - "Enemy shields +3%" when an assignment completes
- **On together or not at all (my call).** The host decides for the whole crew at the start of Patrol. The
  console, science scanning and enemy steps are all on only if the ship's bridge has a shipped console position.
  Shared or player-made bridges (`BridgeShare`, `CustomShip`) have none, so they play Patrol as it is today, and
  enemies never grow without a way to keep up. Bridge Walk being a per-player setting doesn't change this:
  any crewmate who has it on can use the console.
- **Scope:** only Patrol, only for this session. Everything resets when the Patrol mission ends or is restarted.

### Networking

- **The mini games run on the PC of the player at the console.** When a scan finishes, that player sends
  "scan complete: <object>" to the host. If two scans of the same object finish at once, only the first counts.
- **Scan progress is shared.** Each target's progress and its two chosen mini games are kept on the host, which
  sends them to the crew. The player at the console reports progress while they work (a few times a second is
  plenty) and when they leave. That way a crewmate can carry on, even if the first player has disconnected.
- **"In use" is shared** with a small mod event or room property, so every crewmate's console shows it.
- **The host owns the totals:** the crew's upgrade levels and the enemy step count. They're published as room
  properties the way `RestockSync` publishes restocks. A player who joins mid-Patrol reads them from the room.
- **Upgrades take effect on the host.** Damage is worked out there, the way `FleetBattleArmor` changes the damage
  a ship takes. Where a value has to be set on every copy of the ship (speed or turning, if the game keeps those
  on each player's copy), every game applies it when the room property changes.
- Everyone in a crew runs the same mod version, so no fallback is needed for players without the console.

## Goals

- A reason to leave your station that is felt in fights, and a real cost to doing it.
- The console looks and feels like part of the bridge, in VR and on a monitor.
- Every crewmate sees the same console in the same place and the same upgrade totals.

## Non-goals

- A new briefing-room role or seat, or an AI science officer at the console.
- Missions other than Patrol for now. Free Roam, Last Stand and Fleet Battle may come later.
- Upgrades that carry over between sessions.
- Changing Tactical's scan or the Research assignment.

## Anti-goals (failure even if it works)

- Scans that finish without anyone at the console.
- A crew that scans regularly still being overwhelmed eventually.
- A console that looks bolted on, or that works in only one of VR and monitor play.

## Left to the builder

- Which game console piece to borrow for the mesh, and which surface becomes the screen.
- How the default console positions are stored and loaded, alongside the existing `BridgeLayout` and
  `BridgeParts`, without mixing with players' own layout files.
- How each upgrade is applied: which damage, shield, impulse and repair values to patch, and on which machine.
- Which prop to use for debris, and the spawn details (how many readings per site, how far away).
- The networking details (event numbers, property keys), following the mod's existing patterns.
- How the console gets the "in range" list, using the game's sensor range or a fixed distance.
