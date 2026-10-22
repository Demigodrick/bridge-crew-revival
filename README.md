# Bridge Crew Revival (proof of concept)

A BepInEx 5 plugin for the **Steam PC** version of *Star Trek: Bridge Crew* that keeps
multiplayer working without Ubisoft's servers:

- **Skips Uplay / Ubisoft online services entirely** by switching on the game's own
  developer "no-platform" login mode.
- **Plays over Steam with no server (default, `Mode = Steam`):** each game runs a Photon-compatible
  server in-process. Public rooms are listed worldwide as Steam lobbies, friends join through Steam,
  and game traffic goes peer-to-peer through Steam's networking and relays.
- Alternatively redirects Photon to a dedicated server ([`server/`](server/README.md), Linux/Docker)
  or to Photon Cloud with your own App ID.

Status: tested in-game with the dedicated server (menus, matchmaking, a full mission, joining by
code, Steam launch-joins). Steam mode passes end-to-end tests with the game's real Photon client
library (relayed joins, events, host loss) but **hasn't run between two real players yet.**

## How it works

`Assembly-CSharp.dll` is obfuscated (scrambled names plus uncalled decoy methods), but the
developers' internal tooling is still in the retail build:

| Game code | Retail behaviour | What the plugin does |
|---|---|---|
| `NetworkCore.GetLoginMethod()` reads `BuildStamp.UbiLoginMethod` (`PCApp`) | Uplay start, Ubisoft ticket login, account sync before Photon | Returns `None`. `ConnectionManager` then queues only the Photon connect step, and `NetworkStatus` counts "connected to Photon" as online. |
| `SVR.ConfigFile` dev config (`server`, `serverport`, `protocol`, `region`, `name`, …) | File loader is compiled out, so nothing is ever set | Answers `GetValueString`/`ContainsKey` for those keys. The game's own connect step then switches `PhotonServerSettings` to `SelfHosted`. |
| `PhotonNetwork.ConnectUsingSettings` / `ConnectToRegion` | Ubisoft's Photon App ID, Steam ticket as custom auth | In `Cloud` mode, sets your App ID. Optionally clears the Steam auth type so servers without a Steam auth provider accept the client. |
| `NetworkingPeer.Connect(address, GameServer)` | Connects to the game server address the master hands out | `SelfHosted`: connects to `Server:Port` instead, so Docker/NAT setups work (`RewriteGameServerAddress`). |
| `PhotonNetwork.SwitchToProtocol` (UDP to TCP) | Disconnects the old, never-connected UDP peer, which still reports `Disconnect`, and the game shows `LostConnectionToPhoton` | Drops `Disconnect` statuses from peers that never connected. |
| `PhotonNetwork.SwitchToProtocol` (UDP to TCP) | The new peer starts with an empty actor table and PhotonView registry, so the local player spawns as "(remote)" with no camera and the main menu stays on LOADING | Copies the actors and PhotonViews from the old peer and re-registers the local actor (`ChangeLocalID`). |
| Photon's `SocketTcp` (Steam mode) | TCP sockets only | For `local:1` and `steam:<id>:1` addresses, routes the client's bytes to the in-game server or over Steam P2P (`shared/VirtualSockets.cs`). |
| `Uplay.OnApplicationQuit` → `UplayQuit()` | Native call into Uplay on exit | Sets the game's own `s_skipQuit` flag, because Uplay was never started. |

The plugin only patches PhotonNetwork after the first frame. Patching it from plugin load runs
PhotonNetwork's static constructor (`Resources.Load`) before Unity is ready, which crashes.

Player names come from `PlayerName`, then your Steam persona name, then the OS user name.
Under Proton the OS user name is `steamuser`, so set a name if Steam isn't ready yet.

## Build

Requires the .NET SDK (8+) and the game installed. Game DLLs are referenced, not shipped.

```bash
dotnet build src -c Release
```

If the game isn't in the default Steam library, add `-p:GameDir="/path/to/Star Trek Bridge Crew"`.
The output is `src/bin/Release/net35/StbcRevival.dll`.

To check patch targets against an install:

```bash
dotnet run --project tools/verify -- "/path/to/Star Trek Bridge Crew"
```

## Install

Website: **https://demigodrick.github.io/bridge-crew-revival/**

Windows (PowerShell):

```powershell
irm https://demigodrick.github.io/bridge-crew-revival/install.ps1 | iex
```

Linux, then set the game's Steam launch options to `WINEDLLOVERRIDES="winhttp=n,b" %command%`:

```bash
curl -fsSL https://demigodrick.github.io/bridge-crew-revival/install.sh | bash
```

The installers live in `docs/` (the GitHub Pages site) and download the latest GitHub release.

### Releases and the zip

`tools/release.sh` builds locally (the mod compiles against the game's own DLLs, so CI can't) and
publishes a GitHub release with `BridgeCrewRevival.zip` and its `.sha256`; keep those asset names,
the installers fetch `releases/latest/download/BridgeCrewRevival.zip`. `tools/package.sh` only
builds the zip: the plugin, `install.sh` (Linux), `install.cmd` / `install.ps1` (Windows) and a short
README. From the zip, players run:

- **Windows:** double-click `install.cmd`.
- **Linux:** `./install.sh`, then set the game's Steam launch options to
  `WINEDLLOVERRIDES="winhttp=n,b" %command%` (the script prints this; it doesn't edit Steam's config).

Both scripts find the game through Steam's `libraryfolders.vdf` (on Linux: native, Flatpak and Snap
Steam), or take the game folder as an argument. They download BepInEx 5.4.23.5 from its GitHub
release and check its SHA-256, set the `Type = MonoBehaviour` entrypoint (step 4 below), and copy
the plugin. They refuse to run while the game is running. Run again to update. `--uninstall`
(Linux) or `-Uninstall` (Windows) removes the plugin and BepInEx's `winhttp.dll`/doorstop files,
which disables BepInEx, and leaves `BepInEx/` and your config in place.

## Install by hand (Linux / Proton)

1. Download **BepInEx 5.4.x, Windows x64** from the BepInEx GitHub releases. Extract it into the
   game folder, so `winhttp.dll` and `BepInEx/` sit next to `stbc.exe`.
2. In Steam, open the game's Properties and set its launch options to:
   `WINEDLLOVERRIDES="winhttp=n,b" %command%`
3. Launch once so BepInEx creates its folders, then quit.
4. **Required for this game:** in `BepInEx/config/BepInEx.cfg`, under `[Preloader.Entrypoint]`,
   change `Type = Application` to `Type = MonoBehaviour` and leave `Assembly = UnityEngine.dll`
   and `Method = .cctor` as they are. With the default entrypoint, BepInEx starts in the middle
   of Unity 5.4's script-domain reload. The plugin then loads `Assembly-CSharp` too early, every
   game script fails to bind ("The referenced script on this Behaviour is missing!"), and you
   get an endless black screen.
5. Copy `StbcRevival.dll` and `StbcCore.dll` into `BepInEx/plugins/`.
6. Launch again. This creates `BepInEx/config/stbc.revival.cfg`. Edit it, then restart.
7. Logs are in `BepInEx/LogOutput.log`. Look for `Bridge Crew Revival` and the
   `ConnectUsingSettings: host=...` line.

On Windows, skip step 2.

## Rooms: browser, join by code, short codes

Uplay invites are gone, so the mod replaces them:

- **Room browser (F7):** a desktop window listing public rooms (Steam lobbies in Steam mode, the
  server's rooms otherwise: code, mode, phase, players), with Join buttons. It shows your own room's code with a copy button, and has a
  code field with Paste/Join. In Steam mode it also says whether you're hosting, and whether your room
  is private (invite only) or public and listed on Steam; your own listed room appears at the top as
  "(you)". It's drawn with Unity IMGUI on the desktop window, so it isn't visible
  inside a VR headset; use it from the monitor, or the web page below.
- **Join by code:** type or paste a code like `K7QX2M` (or a full room name such as `eu|K7QX2M`).
  It sends the same game event Uplay invites used, so the game's own join flow handles it.
- **Short room codes:** new rooms get 6-character codes instead of GUIDs (`ShortRoomCodes`).
  For a private match, the host opens F7 and shares the code.
- **Web page (dedicated server):** stbc-server serves its list at `http://<server>:8080/`.
- **Steam mode:** a public room you host is published as a Steam lobby automatically; private
  matches aren't. A code typed into F7 is looked up among public lobbies, then among your Steam
  friends' rooms (via their rich presence).

Private matches stay hidden from both lists; only the code lets people join.

### Steam friends

Bridge Crew never used Steam for multiplayer (friends and invites were Uplay). The mod adds it
using the game's bundled Steamworks.NET:

- **Shuttle friends panel (VR):** the console's "Ubisoft Club friends" panel lists your online Steam
  friends instead, with Steam avatars. Friends in a joinable crew get the game's "playing" icon and a
  working **Join**; **Invite** sends a Steam game invite for your current crew (Steam gives friends
  without the game running a launch link).
- **Rich presence:** while you're in a crew, the mod sets a `connect` string, so friends get **Join
  Game** on you in their Steam friends list, plus Steam's party keys, which group crewmates together.
  Steam only displays status text the publisher registered, and Bridge Crew has none, so friends see
  "Playing Star Trek Bridge Crew"; the mod's "In crew K7QX2M · Briefing room · 2/4" status shows in
  the shuttle friends panel.
- **Invites:** F7 → **Invite Steam friends** opens the Steam overlay; right-click a friend →
  **Invite to Game**.
- **Joining:** accepting an invite or clicking Join Game joins the room by code. If the game isn't
  running, Steam starts it with `+stbc_join <room> +stbc_host <steamid>` and the mod joins once you
  reach the shuttle. In Steam mode, F7's invite button opens Steam's lobby invite dialog when you
  host a public room.
- Everyone needs the mod. With a dedicated server everyone also needs the same server: set
  `[Steam] PublicServerAddress` so friends on a different server get told which one to use.
- Friends can see and join your room code this way, private matches included.
  `[Steam] Presence = false` turns it all off.

## Configure

`BepInEx/config/stbc.revival.cfg`:

```ini
[Photon]
Mode = Steam             # Steam | SelfHosted | Cloud | Default
Server = 192.168.1.10    # SelfHosted
Port = 4530
Protocol = Tcp           # Tcp for stbc-server; Udp | Tcp for Photon Server
RewriteGameServerAddress = true
AppId =                  # Cloud: your PUN App ID
Region =                 # Cloud: eu | us | asia | jp | au
StripSteamAuth = true
MatchmakingTag =         # optional; only players with the same tag see each other

[Login]
SkipUplay = true
PlayerName = Picard

[Game]
ExtraDevConfig =         # e.g. maxnumplayers=5;mutevoip=false;hideNameTag=true

[Rooms]
BrowserKey = F7
ShortRoomCodes = true

[Steam]
Presence = true
PublicServerAddress =    # e.g. 203.0.113.10:4530
```

### Steam (default, nothing to host)

`Mode = Steam`. That's it: host from the shuttle as usual (Quick or Private match), and others
find public rooms in F7 / Quick match, or join you through Steam. The host's game is the server,
so the session ends when the host quits. Steam's relays handle NAT; no ports to open.

### Option A: stbc-server in Docker (always-on community server)

```bash
cd server
docker compose up -d --build
```

Set `Mode = SelfHosted`, `Server` = the machine running the container, `Port = 4530`,
`Protocol = Tcp`. See [server/README.md](server/README.md).

### Option B: Photon Cloud

Create a free account at dashboard.photonengine.com and create a **PUN** application. Put its
App ID in `AppId`, set `Mode = Cloud`, and give every player the same config. The free tier
caps concurrent users, which is plenty for a group of friends. Note that an App ID shipped in
a mod can be read by anyone who has the mod.

### Option C: Official Photon Server (Windows)

Exit Games' Photon Server SDK, **LoadBalancing** instance; v4 is the closest match for PUN 1.71.

## Known gaps / next steps

- **Steam mode untested between two real players.** Next test: two Steam accounts with the mod;
  one hosts a Quick match, the other finds it in F7 / Quick match or joins through Steam.
- Modes don't mix: Steam-mode players and dedicated-server players can't see each other.
- Steam P2P adds the Unity frame time (~10-15 ms) to relayed traffic (packets are sent and read
  once per frame).
- Ubisoft's native services library is still initialised at startup (`Conveyor.StartUbiServices`).
  It's expected to fail quietly offline. If it hangs or errors, stub it next.
- Uplay friends and invites don't work; the shuttle friends panel, room codes and Steam invites replace them.
- The room browser (public lobbies, join by code) is a desktop overlay, not a VR-console UI; in VR,
  use Quick match, the shuttle friends panel or the Steam overlay.
- Photon `UserId` falls back to the player name, so give each player a unique `PlayerName`.
- Progress saves and anything synced to Ubisoft's servers stay local.
- PC only. PSVR and Quest can't load mods.

## Legal

This repository contains no game code or assets, only patches applied at runtime to a copy
you own. Don't redistribute the game's DLLs.
