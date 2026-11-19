# .NET hosts

Run the kit's database, central, login and map-spawn servers as a plain .NET 9 program instead of a
Unity build, and optionally the database tier as a web API. Map servers still run as your Unity
server build, because they load scenes, physics and navmesh.

Unity skips folders whose name ends in `~`, and so does the Asset Store package builder, so nothing
here reaches a Unity project. The projects compile the kit's own `Server/`, `Database/` and
`SharedData/` straight from this checkout: the hosts and your Unity servers always run the same code.

| Folder | What it is |
| --- | --- |
| `MMO.NET/` | Console server: database, central, login and map-spawn servers, any mix of them, in one process |
| `DatabaseManager/` | ASP.NET Core web API over the database. Servers reach it through `RestDatabaseClient` |
| `SmokeClient/` | Registers, logs in and creates a character against a running login server, then reports each step |
| `ThirdParty/` | The Unity-free code the hosts need that the kit does not ship (git subtrees, see below) |

## Build

Needs the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```
dotnet build "Hosts~/OpenMMORPG.Hosts.sln"
```

## Run the servers

From `Hosts~/MMO.NET` (the host reads `./Config/` from the folder it runs in):

```
dotnet run -- -startDatabaseServer -startCentralServer -startLoginServer -startMapSpawnServer
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

Unlike the Unity spawner, the .NET map spawner cannot fall back to every map in `GameInstance`,
because it has no game data: list the map ids in `spawnMaps`.

### Your game's data

`Config/items.json`, `Config/characterCreationData.json` and `Config/socialSystemSetting.json` are
the upstream sample data. Export your own from Unity: select your `GameInstance`, and under
**Data Exporting** use the export buttons for minimal items, character creation data and the social
system setting. Copy the files into `MMO.NET/Config/`, and `socialSystemSetting.json` into
`DatabaseManager/Config/` too if you use the web API.

## Run the database web API

From `Hosts~/DatabaseManager`:

```
dotnet run
```

It listens on `http://localhost:5757`, uses SQLite by default and checks every call for the
`ApiSecretKey` bearer token. Both are in `appsettings.json`, with the database settings in `Config/`.
Point servers at it with `useCustomDatabaseClient` plus `dbApiUrl` / `dbSecretKey`, or with the
`RestDatabaseClient` on a Unity server. It accepts both `http://host:5757/api/<route>` (the client's
default) and `http://host:5757/<route>`. In the Development environment, `dotnet run`'s default,
Swagger UI is at `/__dev/api`.

Before exposing it, change `ApiSecretKey` and put it behind TLS: a reverse proxy, or an `https://`
URL in `Urls` with a certificate configured for Kestrel.

## Smoke test

With a login server running:

```
dotnet run --project "Hosts~/SmokeClient" -- 127.0.0.1 7500
```

It exits 0 only if every step passes. The character it creates comes from the sample
`characterCreationData.json`; change the ids in `SmokeClient/Program.cs` if you export your own.

## Known gaps

- Map servers call five "user unlock content" database operations that nothing implements, neither
  the LiteNetLib database server nor the web API. This is inherited from upstream and affects the
  Unity servers too.
- Tested on Windows with SQLite, in both database setups, with the smoke client. Not yet tested:
  MySQL, PostgreSQL, Redis, Linux, and Unity map servers or the game client against these hosts.

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
