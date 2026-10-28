# Bridge Crew Revival

**Online multiplayer for *Star Trek: Bridge Crew* on PC, back for good. No Uplay, no Ubisoft account, no server to run.**

Ubisoft's online services were what held Bridge Crew's multiplayer together. Bridge Crew Revival replaces
them with Steam: crews find each other, join each other and fly together exactly as before, in VR or on a
monitor. And while we were in there, we added new missions, voice commands that work again, and a pile of
quality-of-life fixes.

👉 **Website and one-line installer: https://demigodrick.github.io/bridge-crew-revival/**

## What you get

### Your crew, back together
- **Quick match** drops you into any public crew with a free seat, worldwide. If there isn't one, you host
  a crew the next player finds.
- **Play with friends over Steam.** Your Steam friends show up on the shuttle console in VR: join their crew
  or invite them to yours. Steam's Join Game and invites work too.
- **Share codes.** Give anyone your crew's code (like `YZZDK9-EU-1Q2W3E4`) and they can join, even a
  private crew, even if they're not on your friends list.
- **Nothing to host.** Whoever starts the crew hosts it on their own PC, and everyone connects through
  Steam's network. No ports to open, no accounts, no monthly server bill.
- **Host left? No problem.** If the host drops, the crew regroups in the briefing room under the next
  player instead of everyone landing back at the main menu.
- **Voice chat** between crewmates, smoothed out so it doesn't stutter on a shaky connection.
- **Passwords and blocking.** Lock a public crew with a password from the briefing room. Kick a troublemaker
  and you can block them from your crews; anyone you've blocked on Steam can't join either.

### New missions
A new **Revival Missions** category in the briefing room, next to Campaign and Ongoing Voyages:

- **Last Stand:** endless, growing waves of Klingons, pirates and Romulans, with a flagship every fifth
  wave. How long can your crew hold out?
- **Fleet Battle:** lead a battle-worn Starfleet fleet against a Klingon or Romulan armada, with
  reinforcements on both sides.
- **Patrol:** a mission that never ends. Hunt raiders, escort freighters, rescue survivors and restock at
  space stations as Starfleet sends you from system to system.
- **Stellar Evacuation:** a star is about to erupt. Beam people out of escape pods, ferry them to safety and
  get out before the blast.
- **Free Roam:** the developers' own exploration mission, left in the game. No enemies, just space.

### Voice commands are back
The captain's spoken orders used IBM Watson, which shut down years ago. They now run offline on your own PC.
Hold **V** (or the voice button) and say *"Red alert!"*, *"Fire at will"*, *"Engage"* or *"Beam the
survivors aboard"*. [Full list of commands](https://demigodrick.github.io/bridge-crew-revival/#voice).

### Everything else
- **Your avatar and settings are kept on your PC.** The game only ever saved them on Ubisoft's servers; the
  installer can copy them across once.
- **Skip the launch videos** and the mic check, get a chime when someone joins, and a heads-up on the
  shuttle when a new version is out.
- **A Romulan option** for female characters in the character creator.
- **F10** saves a bug report in one keypress.
- Most of it can be switched on and off from **REVIVAL SETTINGS** on the shuttle console.

## Install

Quit the game first.

**Windows**: paste this into PowerShell:

```powershell
irm https://demigodrick.github.io/bridge-crew-revival/install.ps1 | iex
```

**Linux (Proton)**: paste this into a terminal:

```bash
curl -fsSL https://demigodrick.github.io/bridge-crew-revival/install.sh | bash
```

Then launch the game from Steam as normal. The installer finds the game, sets up the BepInEx mod loader,
installs the latest release and downloads the small offline speech model for voice commands. Run the same
command again any time to update or uninstall. Prefer a download? Grab the zip from the
[releases page](https://github.com/Demigodrick/bridge-crew-revival/releases/latest) and run `install.cmd`
(Windows) or `install.sh` (Linux).

**Everyone in a crew needs the mod, on the same version.** The game will tell you when a crew is on a
different one.

## Questions

**Do I need Ubisoft Connect or a Ubisoft account?** No. The mod skips Ubisoft's login completely.

**Does it work in VR?** Yes. Quick match, the friends panel, invites, joining by code and passwords all
work from inside the headset. The F7 room browser is a desktop window, handy if you're playing on a monitor.

**Can I play with PlayStation VR or Quest players?** No, consoles and standalone headsets can't load mods.
It's PC with PC.

**Can I get banned on Steam?** Bridge Crew has no anti-cheat, and the mod only uses Steam's ordinary
friends, lobbies and networking.

**Something broke.** [Open an issue](https://github.com/Demigodrick/bridge-crew-revival/issues) and attach
`BepInEx/LogOutput.log` from the game folder (or press F10 in game to save a report).

More answers on the [website](https://demigodrick.github.io/bridge-crew-revival/).

## Settings

Everything lives in `BepInEx/config/stbc.revival.cfg` in the game folder (created on first launch), and the
everyday options are in REVIVAL SETTINGS in game. A few you might want:

```ini
[Login]
PlayerName = Picard      # otherwise your Steam name

[Startup]
SkipIntro = true         # straight to the shuttle

[Voice]
VoiceCommands = true

[Rooms]
BrowserKey = F7
```

---

## For communities and developers

<details>
<summary><b>Running an always-on dedicated server</b></summary>

Steam mode needs no server, but a community can run one in Docker instead:

```bash
cd server
docker compose up -d --build
```

Players then set, in `stbc.revival.cfg`:

```ini
[Photon]
Mode = SelfHosted
Server = your.server.address
Port = 4530
Protocol = Tcp
```

See [server/README.md](server/README.md). Steam-mode players and dedicated-server players can't see each
other. Photon Cloud with your own PUN App ID also works (`Mode = Cloud`, `AppId = ...`).

</details>

<details>
<summary><b>How it works</b></summary>

It's a [BepInEx 5](https://github.com/BepInEx/BepInEx) plugin that patches the game at runtime with
HarmonyX. The retail build still contains the developers' internal "no platform" login and self-hosted
Photon settings, so the mod switches those on instead of Ubisoft's login. In Steam mode each game also runs
a small Photon-compatible server in-process ([`server/StbcCore`](server/StbcCore)); the host's game serves
the crew, and Photon's traffic is carried over Steam's peer-to-peer networking and relays
([`shared/VirtualSockets.cs`](shared/VirtualSockets.cs)). Crews are listed as Steam lobbies.

Save data that used to live on Ubisoft's servers is kept in `BepInEx/config/stbc.revival.save.json`.
Voice commands use [Vosk](https://alphacephei.com/vosk/) for offline speech recognition.

</details>

<details>
<summary><b>Building from source</b></summary>

Needs the .NET SDK (8 or later) and the game installed; the game's DLLs are referenced, never shipped.

```bash
dotnet build src -c Release
```

If the game isn't in the default Steam library, add `-p:GameDir="/path/to/Star Trek Bridge Crew"`. The
plugin lands in `src/bin/Release/net35/`. Check the patch targets against an install with
`dotnet run --project tools/verify -- "/path/to/Star Trek Bridge Crew"`, and run the server tests with
`dotnet test server/StbcServer.Tests`.

Releases are built locally with `tools/release.sh` (the mod compiles against the game's own DLLs, so CI
can't), which publishes `BridgeCrewRevival.zip` and its `.sha256`. The installers fetch
`releases/latest/download/BridgeCrewRevival.zip`, so keep those names.

</details>

<details>
<summary><b>Installing by hand</b></summary>

1. Extract **BepInEx 5.4.x, Windows x64** into the game folder, next to `stbc.exe`.
2. Linux only: set the game's Steam launch options to `WINEDLLOVERRIDES="winhttp=n,b" %command%`.
3. Launch once so BepInEx creates its folders, then quit.
4. **Required:** in `BepInEx/config/BepInEx.cfg`, under `[Preloader.Entrypoint]`, change
   `Type = Application` to `Type = MonoBehaviour`. Without it the game hangs on a black screen.
5. Copy `StbcRevival.dll` and `StbcCore.dll` into `BepInEx/plugins/` and launch.

The log is `BepInEx/LogOutput.log`.

</details>

## Legal

Bridge Crew Revival is an unofficial fan project, not affiliated with or endorsed by Ubisoft, Red Storm
Entertainment, CBS Studios or Paramount. Star Trek is a trademark of CBS Studios. You need your own copy of
the game on Steam; this repository contains no game code or assets, only patches applied at runtime.
