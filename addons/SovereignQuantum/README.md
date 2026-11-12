# Sovereign quantum torpedoes (experimental add-on)

An optional BepInEx plugin for Bridge Crew Revival. Sovereign player ships fire blue quantum torpedoes through their existing launchers, with a subtle star-shaped flare and a configurable damage multiplier (default 1.5). Other ships retain their normal torpedoes. This prototype is separate from Revival and is not included in the release/package scripts.

## Behavior and implementation

- Identify the **firing ship's instantiated body**, not a local configuration or room-wide selection. Only `PlayerSpacecraft` with a `Body - Sovereign (...)` body qualifies; merely naming a ship Sovereign does not.
- Mark each projectile after `LauncherPayload.LMOOIPLMILG(string)` resolves its network source. Only `LauncherTorpedo` qualifies, excluding mines and depth charges.
- Scale damage once in `BaseCombatManager.Server_Explosion`, before the existing explosion-result RPC distributes it. The projectile must still match the original source. No separate damage messages or shared launcher-data edits.
- Tint particle and material instances blue. Restore them when the projectile is disabled or reused from its pool. Add a depth-tested, camera-facing additive star at the projectile; it stops when the flight particles stop. Shader absence disables the flare only.
- Load optional PCM8/PCM16 WAV audio. Play a 2D salvo clip once per source per clip duration; stock Wwise audio remains audible.
- Defer patching and audio initialization to the first `Update`. In this game BepInEx enters `Awake` through MonoBehaviour's static constructor: initializing audio there caused main-thread errors and prevented the main menu from loading.

Ammo consumption, guidance, range, speed and shield calculations remain unchanged. There is currently no separate photon/quantum inventory or selector. The 1.5 damage multiplier is a provisional balance choice, **not a canonical Star Trek value**. The visual reference is the Defiant/Enterprise-E quantum torpedo; photon torpedoes also remain in use in Star Trek ([reference](https://memory-alpha.fandom.com/wiki/Quantum_torpedo)).

## Build and test (Windows)

Requires the installed game's managed assemblies, BepInEx 5 and the Windows .NET Framework compiler. No proprietary assemblies or compiled binaries are committed.

```powershell
.\addons\SovereignQuantum\build.ps1 -GameDir 'C:\Games\Star Trek Bridge Crew'
# Optional: also validate/copy a locally prepared 44.1 kHz mono WAV:
.\addons\SovereignQuantum\build.ps1 -GameDir 'C:\Games\Star Trek Bridge Crew' -AudioPath 'C:\Audio\quantum.wav'
```

Tests use a generated PCM fixture and cover ship-name isolation, invalid multiplier values, WAV format/range and malformed/truncated audio. The optional clip is separately validated. Output is under `bin/` (gitignored).

With the game closed, copy `bin/SovereignQuantum.dll` and optional `bin/quantum.wav` into `BepInEx/plugins/SovereignQuantum/`. Without audio the plugin logs a warning and still provides the visual/damage changes. Delete that plugin folder to uninstall; Revival itself is untouched.

## Audio source

The locally tested clip was selected by the user: [TrekCore quantumtorpeodoes2.mp3](https://www.trekcore.com/audio/weapons/quantumtorpeodoes2.mp3). It is **not included** here; no redistribution license was established. An independently licensed replacement clip can be used instead.

To prepare a downloaded clip locally with FFmpeg:

```powershell
ffmpeg -i quantumtorpeodoes2.mp3 -ac 1 -ar 44100 -c:a pcm_s16le quantum.wav
```

## Configuration and multiplayer

`BepInEx/config/stbc.sovereign.quantum.cfg`:

```ini
[Quantum]
Enabled = true
DamageMultiplier = 1.5

[Audio]
SalvoVolume = 0.6
```

Restart after editing. DamageMultiplier is clamped to 1–3; volume to 0–1. The host needs this plugin for authoritative damage. Each client needs it for matching visuals/audio. Mixed-client compatibility, actual damage balance and PvP behavior are not yet verified in a multiplayer session.

## Validation and remaining review

- Compiled against the installed Unity 5.4/BepInEx/game assemblies; source field, initialization hook, explosion signature and RPC path inspected locally.
- Policy/audio tests pass, including the selected 1.7-second clip.
- Version 0.1.1 startup reached `GamePhase_MainMenu`, joined the lobby and loaded audio without the earlier main-thread/MonoBehaviour errors.
- Versions 0.1.2/0.1.3 add stronger blue tint and the flare. Compilation passes; the flare has **not been visually verified in combat or VR**.
- Review/test: Sovereign versus stock ships and Nova; host/client and different PvP ship classes; repeated volleys and pooled reuse; shield/hull damage; mission transitions; skybox scale, occlusion, VR billboarding and sound overlap.

This draft deliberately leaves release integration to the maintainer after the prototype's combat behavior is verified.
