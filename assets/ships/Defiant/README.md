# Defiant integration

Adds the selectable Defiant with its complete OBJ/MTL model, diffuse/emission atlases,
animated collectors, weapon hardpoints, pulse sound (source MP3 and runtime PCM WAV),
and dynamic dorsal name/bow registry. See CREDITS.txt for the original model attribution.
The original raised dorsal name and bow registry were removed to avoid overlapping text;
other baked markings still retain the original NX-74205 identity.

## Required quantum-torpedo dependency

The complete Defiant weapon system REQUIRES the quantum-torpedo implementation from
commit 8d26d63d7dcc3e2ba623a26f8b95dde8f6e5c542 (official repository PR #30:
https://github.com/Demigodrick/bridge-crew-revival/pull/30).
That commit must be pushed/merged together with this integration (or already exist in
the destination). This branch includes a cherry-pick of that commit, followed by the
Defiant ship-selection and paired-launch extension. Do not cherry-pick only the main
plugin/model commit and expect quantum torpedoes to work.

The add-on remains a separate assembly: build addons/SovereignQuantum/build.ps1 with
-GameDir pointing to the installed game. Install its bin/SovereignQuantum.dll beside
StbcRevival.dll in BepInEx/plugins. Optional quantum.wav installation/conversion is
documented in the add-on README. The main release packager copies the Defiant assets,
but does not build or install this optional add-on automatically.

## Behavior

- One manual command triggers one short burst, with three visual pulses from both
  sides simultaneously. Upper/lower cannon pairs alternate between commands.
- Only the first side uses the game's damage callback; the second side is visual-only.
  Two visible cannons therefore do not double the phaser damage or energy cost.
- Warm gold/orange pulses with an ivory core, stock-width effects and shortened tails.
  Source effects/materials are restored when pooled objects are reused by other ships.
- Phaser bank cost is approximately one eighth per command; forward firing cone is 25 degrees.
- Yaw speed x3, yaw acceleration x4, forward/reverse acceleration x1.6; stock top speed,
  mass, hull and shields are retained. These are provisional gameplay tuning values.
- In single-torpedo mode both ready forward tubes fire through the normal launcher APIs,
  consuming one torpedo per tube. If the sister tube is unavailable, only one launches.
- Quantum damage uses the existing add-on multiplier (default 1.5), not extra phaser damage.

## Validation and remaining review

Built main plugin against the installed game/Unity/BepInEx assemblies on the current
upstream source; four pre-existing compiler warnings. Quantum build and generated/real
PCM tests pass. Local mission testing before the upstream rebase confirmed separate
left/right muzzle origins, one phaser damage path, double quantum launches and smooth
mission performance after fixing Kelvin text initialization/retry allocations.

The latest shortened gold pulse shape and new hull lettering still require visual
confirmation in game. Hull support surfaces were checked against the modified mesh.
The rebased build, VR, Linux and mixed-client multiplayer require runtime review.
No freecam, proprietary game assemblies or compiled mod DLLs are included.

The pulse sound was supplied by the contributor; the included WAV is its PCM conversion.
No independent third-party audio license is asserted here.
