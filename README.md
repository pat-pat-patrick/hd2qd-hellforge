# HellForge for HD2 Mod Quick Deploy

A plugin of [HD2 Mod Quick Deploy](https://github.com/pat-pat-patrick/hd2-mod-cli) that launches
[HellForge](https://github.com/SkyeShade/HellForge), the offline Helldivers 2, and manages its mods.

## What it does

| | |
|---|---|
| Lobbies | While a session runs: every public HellForge lobby on any planet and difficulty (the game's own Quick Play keeps only the same planet and difficulty), what each needs, quick join, and the mods that would let you into more lobbies |
| Sessions | Start a session, see each step (preparing, waiting for Steam, running, how it ended), end it |
| Mods | HellForge's own list in its three co-op groups, with the modlist code: switch, reorder, pack options |
| From the library | Put library mods into HellForge's `mods` folder: hard links on the same drive, so nothing is copied and the library is never written |
| Made for the official game | Each resource a library mod replaces is compared between the official game and HellForge's older one. The same in both, or the mod's own: used as it is. Text tables: the mod's changed lines carried onto HellForge's table. Render configuration, shader libraries and replaced game Lua scripts: left out. Anything else that differs: used as it is, and named |
| Only affects me | Mark a code mod `"coop": "per-player"` in HellForge's copy; marked again when an update comes without it |
| Warnings | A note on a library mod that replaces the render configuration or game text tables built for the official game, or calls Boot Mod Loader's own API |
| Profile and settings | Profile (and new profiles), galactic war, co-op, play with strangers, sandbox, Forge Credits share, language packs |
| Logs | Each session's logs, export, crash report (nothing is uploaded) |

## How

Only through what HellForge documents for launchers of their own
(`app\sdk\docs\external-launchers.md`): `HellForge.Cli.exe` and the `mods` folder. It never writes
HellForge's game copy, `app` folder, backend or network block, and refuses mod changes while a
session runs, as HellForge does.

## What still needs HellForge.exe

- The first setup and repairs: copying the game, Steam's ownership check, the network block.
- Syncing the account (Sync Inventory). The plugin opens HellForge for it, and warns when the
  Windows system proxy is on, which makes the sync fail.

## Build

`build.bat` runs the tests and writes `release\HD2QD.HellForge.dll` (.NET SDK 8 or later; set
`DOTNET` to its `dotnet.exe` when it is not on PATH). Add the DLL on Quick Deploy's Plugins page.

## License

MIT. HellForge is SkyeShade's (GPL-3.0); this plugin only runs its command line.
