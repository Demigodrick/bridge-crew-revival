# stbc-server

A small, Linux/Docker-friendly multiplayer server for *Star Trek: Bridge Crew*. It speaks the
Photon LoadBalancing protocol over TCP, matching the Photon client the game ships with (PUN 1.71,
Photon3Unity3D 4.1.0.5). Use it with the Bridge Crew Revival mod in `SelfHosted` mode.

It implements what the game uses, and nothing else:

- TCP transport: framing, ping and server time.
- Photon's encryption handshake (Diffie-Hellman + AES) and encrypted authentication, with tokens
  issued for reconnects.
- Master server: lobbies, default-lobby room lists with live updates, create/join by name, and
  SQL-lobby quick join. Bridge Crew's `C0 = … AND C7 NOT IN (…)` filters are evaluated server-side.
- Game server: rooms, actors, room and player properties (including compare-and-swap), event
  relay with receiver groups, target actors and interest groups, and the room event cache, so
  late joiners get cached instantiations and buffered RPCs. Also leave/disconnect handling and
  FindFriends.
- One port serves both roles. PUN reconnects between master and game server, and each request
  shows which role it's for.

Game payloads (PUN's RPCs, Vector3/Quaternion custom types, …) are relayed byte-for-byte without
the server needing to understand them.

## Room browser

`http://<server>:8080/` lists public rooms: room code, mode, phase, language, players. It
refreshes every 5 seconds. Private matches aren't listed (only counted). `/api/rooms` and
`/api/status` return the same as JSON.

## Run with Docker

```bash
docker compose up -d --build
```

This listens on TCP 4530 (game) and 8080 (room browser web page). In the mod config (`BepInEx/config/stbc.revival.cfg`):

```ini
[Photon]
Mode = SelfHosted
Server = <host running the container>
Port = 4530
Protocol = Tcp
RewriteGameServerAddress = true
```

All players use the same `Server`, and everyone needs the mod. Open/forward TCP 4530 if players
connect over the internet.

## Run without Docker

```bash
dotnet run --project StbcServer -c Release
```

Settings come from environment variables, or `--key=value` arguments:

| Variable | Default | |
|---|---|---|
| `PORT` | `4530` | TCP port |
| `BIND` | `0.0.0.0` | Listen address |
| `PUBLIC_ADDRESS` | (connection's local address) | Game server address given to clients. Only needed for clients without the mod's address rewrite. |
| `HTTP_PORT` | `8080` | Room browser web page and JSON API (`/api/rooms`, `/api/status`); `0` turns it off |
| `LIST_ROOMS` | `true` | Also send room lists in Bridge Crew's SQL lobby (Photon itself doesn't), for the mod's in-game browser |
| `LOG_LEVEL` | `info` | `debug`, `info`, `warn`, `error` |

## Tests

```bash
dotnet test
```

With the game installed (or `-p:GameDir=...`), the tests load the game's real `Photon3Unity3D.dll`.
They check that the serializer is byte-exact against Photon's own, and run end-to-end sessions
(key exchange, auth, create/join, quick join, events, cache, properties, leaving) through the real
client library. Without the game, only the SQL filter tests build.

To test a running server, such as the container, instead of an in-process one:

```bash
STBC_TEST_SERVER=127.0.0.1:4530 dotnet test
```

## Not implemented

UDP (the mod switches the game to TCP), player TTL / rejoining inactive slots, WebRPC, plugins,
and room-list slices. None are used by Bridge Crew as far as the game code shows.
