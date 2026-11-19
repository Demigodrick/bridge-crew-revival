# Science console: your checklist

Nothing has run in the game yet. Work through this top to bottom. Tick a box when it behaves; write down
anything that doesn't.

## 1. Place the consoles (bridge designer)

Run the designer from the mod's source: `dotnet run --project tools/bridge-designer`.

**Use a piece of a game bridge for the console, not a custom model.** Other players don't have your model, so
they would see no console. The designer refuses a model for **Use picked part**, and the mod leaves one out.

Do these bridges: Aegis (`Bridge_scene_02`), Enterprise (`Retro_Bridge_Scene`), Enterprise-D
(`Bridge_scene_1701D`), Holodeck (`Bridge_scene_Holodeck`). For each:

- [ ] Pick the bridge in the drop-down.
- [ ] **Add parts**: choose the bridge to take a console piece from, click **Add**, drag it into place and turn it.
- [ ] With the new part picked, open **Science console** and click **Use picked part**. Its tag changes to
      "science console".
- [ ] Line up the green plane with the console's face (the bright side is the side the screen is read from).
      **Screen here** puts it where the view looks; fine-tune with X/Y/Z, Turn, Tilt, Width and Height.
- [ ] Point the view at the floor where a player should stand and click **Stand here** (a green ring marks it).
- [ ] Click **Save science placement**. If the toast says your layout lost the part, save the layout too.

Then the mod's ship classes: Nova, Sovereign, AbramsEnterprise, Defiant, Voyager.

- [ ] Each one reuses the Aegis spot. Open it in the designer and check the console isn't blocked by that
      class's layout.
- [ ] Where it is blocked, place it again and save a `Bridge_scene_02-<Class>` placement.

- [ ] Commit the new files in `assets/bridges/science/` (and any layout files the designer changed).

## 2. Before testing

- [ ] Build: `dotnet build src -c Release`.
- [ ] Copy the DLL **only with the game closed**. Swapping it while the game runs freezes the mod.
- [ ] To test before any placement exists, set these in the config (host a Patrol):
  - `[Debug] ScienceOn = true`: the console counts as available on every bridge.
  - `[Debug] SciencePlacement`: path to a placement file put on whatever bridge is loaded. A test one (for the Aegis)
    is in the repo at `specs/science-test-placement-Bridge_scene_02.json` (a dev sample; it doesn't ship).
  - `[Debug] ScienceLedger`: starting upgrades, e.g. `SCL1;1;0.5,0,0,0;;28` = weapons +50%, enemy damage +30%.
    Use `SCL1;1;0,0.5,0.5,0.5;;0` to feel shields, speed and turning, and repairs.

## 3. Solo checks (one player, Patrol)

**The console on the bridge**
- [ ] The console piece sits where you placed it, on every bridge you did.
- [ ] The screen is on its face, the right way round, readable, no flicker.
- [ ] You can walk through the console (it has no collider yet). Note it, nothing more.
- [ ] The log says the console is on for this Patrol.

**In VR**
- [ ] Hold B to stand, walk to the console with the left stick. Controls respond when you are in reach.
- [ ] Poke a target row: the scan opens. Sliders grab, drag and stay where released.
- [ ] Step more than about 1.2 m away: controls stop; the console frees up. Come back: works again.
- [ ] Hold B to sit mid-scan: the console frees and progress is kept.
- [ ] If your own seat is within arm's reach of the stand point, its controls can be poked while standing. Note it.
- [ ] Use the console after standing up from each station (captain, helm, tactical, engineering).

**On a monitor**
- [ ] F6 to stand. Near the console, "F: use science console" shows; away from it, it hides.
- [ ] Press F: the view eases to face the screen, the cursor is free, WASD and mouse no longer move you.
- [ ] Mouse clicks land on what is under the pointer (rows, BACK, sliders follow the mouse).
- [ ] Press F again: the view eases back with no jump; cursor hidden and walking works.
- [ ] F then Esc: you are walking and still standing; a second Esc sits you down; the pause menu does not open
      on the first.
- [ ] While using it, walk off (press F first, then walk away): the console frees up; coming back resumes progress.
- [ ] F6 while using it: you sit normally and the scan is released.

**Readings and debris**
- [ ] After a Patrol site is chosen, three readings appear around it. The log shows 3 distinct ids.
- [ ] Kill a hostile ship: debris appears where it died.
- [ ] The debris looks like wreckage (if not, tell me).

**Scanning**
- [ ] Each target opens two mini games; the pictures animate smoothly.
- [ ] The bar moves only while both games are at 70% or better.
- [ ] A good scan takes about 30 to 45 seconds.
- [ ] At 100%: a notice shows on the objectives panel for about 6 s (e.g. "Graviton stream analysed: shields +5%")
      and as a console message; the target leaves the list, the screen goes back to the list.
- [ ] Leave a scan at 100% before it completes (walk off), then start it again: it completes at once, once.
- [ ] More than 20 km from a target: "out of range", paused; coming back resumes.
- [ ] Menu in German: every line on the screen is German.

**Upgrades felt in fights**
- [ ] Weapons: phasers and torpedoes hit harder (torpedo hits get the upgrade too).
- [ ] Shields: your shields last longer.
- [ ] Speed and turning: faster top speed and a quicker turn. The engineering and impulse readouts still show the
      normal top speed; that is expected.
- [ ] Repairs: subsystems mend faster.

**Enemy steps**
- [ ] After each assignment enemies are a little tougher (more damage, shields, speed). Compare a Patrol with no
      scans: the enemy steps are still felt.

**Nothing else changes**
- [ ] Fleet Battle, Last Stand and an Ongoing Voyage play as before, with no science lines in the log.
- [ ] The helm's own throttle and IMPULSE/WARP buttons are unaffected by the science screen.

## 4. Two-player checks (host plus one crewmate)

- [ ] Join mid-Patrol: the crewmate has the same upgrades.
- [ ] Join mid-scan: they see the same progress and "In use by <name>" on that target.
- [ ] Notice lines (scan done, upgrade gained) show on the crewmate's objectives panel and console too.
- [ ] End the Patrol and start another at once: the console works for the crewmate in the new one.
- [ ] The player at the console quits: "in use" clears within a few seconds, progress is kept, and the crewmate
      can continue the scan.
- [ ] The host leaves: the scan carries on under the new host (old targets may be lost; note it).
- [ ] Warp out mid-scan: the targets go and the console is back on the list.
- [ ] A Patrol on a shared or player-made bridge plays as it does today (no console, nothing different).

## 5. Tuning

If scans feel too slow or fast, or enemies too strong or weak, tell me which, in one line, and I'll adjust.
