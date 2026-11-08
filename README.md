# SummerSync

Multiplayer for My Summer Car: 2 to 4 players in one shared world, over Steam. A mod for
[MSCLoader](https://github.com/piotrulos/MSCModLoader). **Beta.**

- Download, features and how to install: **[mysummersync.com](https://mysummersync.com)**
- Bugs and ideas: [Discord](https://discord.gg/uTs8ufj2bF), or an issue here.

## How it works

- The host's game is the world: the time, the weather, the traffic, the people, the money and the save are the host's.
  Guests play on a copy of the host's saved world, which the mod downloads by itself; their own save is never touched.
- Each vehicle and loose object is simulated by one player's game (whoever drives it or holds it) and shown by the
  others. State machines of the game that decide things for the world run in the host's game and are followed by the
  others, through the game's own states.
- Everything goes through a friends-only Steam lobby and Steam's peer-to-peer networking: no ports, no IP addresses.
- Every player needs the same build. Two different builds refuse to connect and say which build each one runs.

## Building

You need Windows, the .NET SDK 10 and My Summer Car with MSCLoader installed (tested with 1.4.2). The mod compiles
against the game's own assemblies (Unity, PlayMaker, MSCLoader, Harmony…), read from your installation: none of them
are in this repository.

1. Copy `GamePaths.user.props.example` to `GamePaths.user.props` and point `GamePath` at your game folder.
2. Run `build.cmd`: it builds, runs the tests, checks every reference against the libraries the game ships and copies
   `SummerSync.dll` into the game's `Mods` folder. `build.cmd -NoDeploy` leaves the game alone; `build.cmd -Public`
   builds the edition that is released.
3. `package.cmd` makes the zip in `dist\`.

The network and game-state logic has its tests in `tests/SummerSync.Core.Tests`, which run without the game:
`dotnet test tests/SummerSync.Core.Tests`.

A build from source carries its own commit as its build id, so it only connects to the same build.

## Layout

| Folder | What is in it |
|---|---|
| `src/SummerSync.Core` | The logic without Unity: the session, the protocol, and the replication of players, objects, vehicles and world state. Built for .NET 3.5 (the game) and .NET 10 (the tests). |
| `src/SummerSync.Mod` | The mod itself (`SummerSync.dll`): the Unity and game side. |
| `src/SummerSync.Dumper` | A separate mod that writes the game's state to JSON, for development. |
| `data` | What the mod knows about vehicles, people, jobs and the Satsuma's parts, as data files embedded in the DLL. |
| `tools` | `asminspect` (checks references against the game), `dumpdiff` (compares dumps), `netprobe` (a network peer without the game, for testing). |
| `tests` | xUnit tests. |

## License

GPL-3.0: see [LICENSE](LICENSE).

My Summer Car is by Amistech Games; this is an unofficial mod, not affiliated with them. MSCLoader is by piotrulos
(GPL-3.0) and is installed separately. This repository contains no game files and no decompiled code.
