# .NET hosts

Run the kit's database, central, login and map-spawn servers as a plain .NET 10 program instead of a
Unity build, and optionally the database tier as a web API. Map servers still run as your Unity
server build, because they load scenes, physics and navmesh.

Unity skips folders whose name ends in `~`, and so does the Asset Store package builder, so nothing
here reaches a Unity project. The projects compile the kit's own `Server/`, `Database/` and
`SharedData/` straight from this checkout: the hosts and your Unity servers always run the same code.

Reach for these when you host the game on a VPS or another machine without Unity, when you want
PostgreSQL (only the hosts support it), or when a website or tool needs the database. For making the
game and testing in the editor, the Unity servers are all you need. The documentation's
[Running Servers Without Unity](https://open-mmorpg.github.io/documentation/mmo/servers-without-unity/)
page is the step-by-step version of this README.

| Folder | What it is |
| --- | --- |
| `MMO.NET/` | Console server: database, central, login and map-spawn servers, any mix of them, in one process |
| `DatabaseManager/` | ASP.NET Core web API over the database. Servers reach it through `RestDatabaseClient` |
| `SmokeClient/` | Registers, logs in and creates a character against a running login server, then reports each step |
| `ThirdParty/` | The Unity-free code the hosts need that the kit does not ship (git subtrees, see below) |

## Add it to your project

The Asset Store package and the release `.unitypackage` leave `Hosts~` out. If your kit came from
either, copy `Hosts~` from this repository, at the release tag that matches your kit, into your
project's `Assets/OpenMMORPG/`. Unity ignores it there, and the hosts compile the kit code your game
is actually built from, including your own edits to `Server/`, `Database/` and `SharedData/`. If you
work in a git checkout of the kit, it is already there.

- **Keep the versions together.** The hosts, the Unity server build and the game client all have to
  come from the same kit code. Update `Hosts~` whenever you update the kit.
- **Custom server code elsewhere is not included.** The hosts only compile those three folders. If
  you extended the database or server code in a folder of your own, add it to
  `MMO.NET/OpenMMORPG.MMO.NET.csproj` (and `DatabaseManager/OpenMMORPG.DatabaseManager.csproj`) with
  another `<Compile Include="..." />` line, like the existing ones. Code that needs `UnityEngine`
  cannot be compiled here.

## Build

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Run every command in
this README from the `Hosts~` folder.

```
dotnet build OpenMMORPG.Hosts.sln
```

## Run the servers

The host reads `./Config/` from the folder it runs in, so from `Hosts~` it uses `Hosts~/Config/`:

```
dotnet run --project MMO.NET -- -startDatabaseServer -startCentralServer -startLoginServer -startMapSpawnServer
```

| Flag | Starts |
| --- | --- |
| `-startDatabaseServer` | The LiteNetLib database server |
| `-startCentralServer` | Central and its cluster server |
| `-startLoginServer` | The login server. It needs `-startCentralServer` in the same process, because it reads central's channels and cluster server directly |
| `-startMapSpawnServer` | The map spawner, which launches `spawnExePath` (your Unity server build) for each map in `spawnMaps` |
| `useCustomDatabaseClient` | Use the web API (`dbApiUrl`, `dbSecretKey`) instead of the LiteNetLib database server. No leading dash; it can also be set in `serverConfig.json` |

Settings live in `Config/serverConfig.json`. Its defaults match the demo's `MMOServerInstance` prefab
(central 7000, cluster 6010, login 7500, database 6100, map spawn 6001, maps from port 8000). The
command-line arguments are the same as a Unity server's, for example `-loginPort 7500`, and win over
the file. `databaseOptionIndex` here is 0 = SQLite, 1 = MySQL, 2 = PostgreSQL, set up in the matching
`Config/*Config.json`. In Unity it is the position in the prefab's database options list instead.

The login server's flood limits (`loginMaxConnectionsPerAddress`, `loginFailuresPerUsername`,
`registerMaxAccountsPerAddress` and the rest, listed with their defaults in `serverConfig.json`) are
the same keys and arguments as a Unity server's; `0` turns a limit off. One key is the host's own:
`maxCharactersPerAccount` (5), the number of characters an account may have, which a Unity server
takes from the Game Instance's **Max Character Saves**. `loginMaxConnections` is how many clients
can be on the login and character screens at once.

Unlike the Unity spawner, the .NET map spawner cannot fall back to every map in `GameInstance`,
because it has no game data: list the map ids in `spawnMaps`.

Stop the program with Ctrl+C, or with SIGTERM (what `systemctl stop` and `docker stop` send). The
servers then stop in order, and the database server first writes the character saves it is still
holding: it saves a character at most once every 20 seconds, so the newest state can be waiting in
memory. Killing the process outright loses those.

### Your game's data

`Config/items.json`, `Config/characterCreationData.json` and `Config/socialSystemSetting.json` are
the demo's data, so the hosts serve the demo as they are. For your own game, export yours from
Unity: select your `GameInstance`, and under **Data Exporting** use the export buttons for minimal
items, character creation data and the social system setting. Copy the files into
`Hosts~/Config/`. The servers, the web API and the smoke client all use that one folder. Export again whenever you change items, starting characters or guild settings, as you
would rebuild the Unity server.

### Map servers

Map servers are still your Unity server build, made the same way as for the all-Unity setup (see
the kit README's "Running the demo"). Point `spawnExePath` at it; an absolute path is safest. The
spawner gives each map server its map, channel, ports and central's address. Everything else, such
as where the database is, the map server reads from `./Config/serverConfig.json`. It starts in the
host's folder, so that is the host's own `serverConfig.json`, which uses the same keys as a Unity
server's. One file configures both.

## Run the database web API

From `Hosts~`:

```
dotnet run --project DatabaseManager
```

It listens on `http://localhost:5757`, uses SQLite by default and checks every call for the
`ApiSecretKey` bearer token. Both are in `appsettings.json`, with the database settings in `Config/`.
Point servers at it with `useCustomDatabaseClient` plus `dbApiUrl` / `dbSecretKey`, or with the
`RestDatabaseClient` on a Unity server. It accepts both `http://host:5757/api/<route>` (the client's
default) and `http://host:5757/<route>`. In the Development environment, `dotnet run`'s default,
Swagger UI is at `/__dev/api`.

Before exposing it, change `ApiSecretKey` and put it behind TLS: a reverse proxy, or an `https://`
URL in `Urls` with a certificate configured for Kestrel.

## Deploy to a server

`dotnet run` is for working on the hosts. To install them on a server, publish them:

```
dotnet publish MMO.NET -c Release -o publish/servers
dotnet publish DatabaseManager -c Release -o publish/database-api
```

Each output folder is complete, with a copy of `Hosts~/Config/`. Copy it to the server and start the
program from inside the folder, so it finds `./Config/`:

```
cd publish/servers
dotnet OpenMMORPG.MMO.NET.dll -startDatabaseServer -startCentralServer -startLoginServer -startMapSpawnServer
```

```
cd publish/database-api
dotnet OpenMMORPG.DatabaseManager.dll
```

That needs the .NET 10 runtime on the server (the ASP.NET Core runtime for the web API). To avoid
installing it, publish self-contained for the server's platform instead, for example
`-r linux-x64 --self-contained` (about 90 MB for the servers, 110 MB for the web API), and run
`./OpenMMORPG.MMO.NET` or `./OpenMMORPG.DatabaseManager` directly.

## Several machines

The login server always runs in the same process as central. Everything else can move:

- **One machine for the core:** `-startDatabaseServer -startCentralServer -startLoginServer`.
- **One or more map machines:** `-startMapSpawnServer` next to a copy of the Unity server build, with
  `centralAddress` and `databaseManagerAddress` in `serverConfig.json` pointing at the core machine
  and `publicAddress` set to the address players reach that machine on.
- **Optionally, the web API on its own machine**, with every server using `useCustomDatabaseClient`
  and `dbApiUrl` pointing at it. Its cache can be Redis instead of in-process (`CacheManager` in
  `appsettings.json`, `Config/redisConfig.json`).

The game client only connects to the login server and to map servers, so players only need to reach
the login port (7500) and the map servers' ports (8000 and up). Keep everything else private to your
servers: central (7000), cluster (6010), database (6100), map spawn (6001) and the web API (5757).

## Smoke test

With a login server running:

```
dotnet run --project SmokeClient -- 127.0.0.1 7500
```

It exits 0 only if every step passes. The character it creates is the first one in
`Config/characterCreationData.json`, which is built beside the client, so it follows the
data you export. A third argument names another file.

A second mode checks the login server's connection limit. Against a login server started with
`-loginMaxConnections 2`, this opens four connections and passes only if two are let in, two are
refused, and a newcomer gets in once one of the two has left:

```
dotnet run --project SmokeClient -- connections 127.0.0.1 7500 4 2
```

`Tools~/hosts_smoke.py` does the whole round on its own: it starts the built hosts on a fresh
database, runs the smoke client, stops the hosts and checks they stopped cleanly, once with the
LiteNetLib database server, once with the web API, and once for the connection limit. The
repository's checks workflow builds the hosts and runs it on every push.

```
dotnet build OpenMMORPG.Hosts.sln -c Release
python ../Tools~/hosts_smoke.py ..
```

## Known gaps

- Map servers call five "user unlock content" database operations that nothing implements, neither
  the LiteNetLib database server nor the web API. This is inherited from upstream and affects the
  Unity servers too.
- Tested on Windows with SQLite, in both database setups, with the smoke client, from `dotnet run`
  and from published builds. The Linux self-contained publish builds but has not been run. Not yet
  tested: MySQL, PostgreSQL, Redis, and Unity map servers or the game client against these hosts.

## Where this comes from

The hosts are adapted from SURIYUN's `UnityMultiplayerARPG_MMO.NET` and
`UnityMultiplayerARPG_DatabaseManager` (MIT, Ittipon Teerapruettikulchai). Changes from upstream:

- `MMO.NET` starts our separate login server and reads its settings.
- `RestDatabaseClient` gets `dbApiUrl` and `dbSecretKey` from `serverConfig.json`. Upstream reads
  them in `Awake`, which only Unity calls.
- A missing `serverConfig.json` is written to `serverConfig.json`. Upstream wrote it over
  `socialSystemSetting.json`.
- The web API accepts `/api/` paths. Upstream only served the root, while its client defaults to `/api/`.
- The web API's settings folder is `Config/`, the name its code reads. Upstream ships `config/`,
  which only works on case-insensitive file systems such as Windows.
- Default ports and paths follow the kit. Packages are updated past the NuGet audit advisories.
- `MMO.NET` stops on Ctrl+C and SIGTERM, and writes the character saves the database server still
  holds before it ends. Upstream ends the process where it stands.
- The login server's character creation check follows the Unity one: a game without factions
  creates characters with faction 0. Upstream's check refuses every character in such a game.
- The shipped `Config/` data is the demo's, not upstream's sample game.

## Third-party code

| Folder | Source | Licence |
| --- | --- | --- |
| `ThirdParty/LiteNetLibManager` | [insthync/LiteNetLibManager](https://github.com/insthync/LiteNetLibManager), branch `minimal` (includes LiteNetLib and websocket-sharp) | MIT |
| `ThirdParty/RestClient` | [insthync/dotnet-rest-client](https://github.com/insthync/dotnet-rest-client), branch `main` | MIT |

`DevExtension` comes from the kit's own `ThirdParty/DevExtension`. Both folders are git subtrees.
To update one:

```
git subtree pull --prefix="Hosts~/ThirdParty/LiteNetLibManager" https://github.com/insthync/LiteNetLibManager.git minimal
git subtree pull --prefix="Hosts~/ThirdParty/RestClient" https://github.com/insthync/dotnet-rest-client.git main
```
