# HellForge for HD2 Mod Quick Deploy

A plugin for [HD2 Mod Quick Deploy](https://github.com/pat-pat-patrick/hd2-mod-cli) that launches
[HellForge](https://github.com/SkyeShade/HellForge), the offline Helldivers 2, and manages its mods.

## Features

| | |
|---|---|
| Lobbies | While a session is running: every public HellForge lobby on any planet and difficulty (the game's Quick Play searches only the same planet and difficulty), the mods each lobby requires, quick join, and the mods that would make more lobbies joinable |
| Sessions | Starting a session, each stage of it (preparing, waiting for Steam, running, how it ended), and ending it |
| Mods | HellForge's mod list in its three co-op groups, with the modlist code: enabling, ordering and pack options |
| Deploy target | In a Quick Deploy with deploy targets, HellForge can be selected above the library. The main list then shows HellForge's own enabled mods, order and options, and Deploy places each enabled mod in HellForge's `mods` folder as a separate folder (hard links on the same drive; the library is never modified), in the list's order and enabled. Disabled mods are removed |
| Mods made for the official game | Each resource a library mod replaces is compared between the official game and HellForge's older version. Resources that are identical in both, or that belong to the mod, are used unchanged. Text tables: the mod's changed lines are merged into HellForge's table. Render configuration, shader libraries and replaced game Lua scripts are not deployed. Other differing resources are deployed unchanged and listed |
| This player only | Marks a code mod `"coop": "per-player"` in HellForge's copy, and marks it again when an update arrives without the mark. "Set all gameplay mods to this player only" (on by default) marks every shared gameplay code mod except HellForge's own, including mods added later; while it is on, each mod's own switch follows it and is locked. Cosmetic mods have no switch: HellForge never compares them |
| Warnings | A note on a library mod that replaces the render configuration or game text tables made for the official game, or that calls Boot Mod Loader's own API |
| Profile | The profile in use, new profiles (with what each template starts with), account sync, and editing the profile between sessions: level, currencies (medals, requisition, samples, Forge Credits) within their caps, weapon levels, the pages of the warbonds the profile owns, and items by category (unlock a whole category, or add a named item). It changes the profile as HellForge itself does (its launcher's weapon levels, its backend's warbond purchases), adds and never removes, and backs the profile up first into its `backups` folder, where HellForge's Account sync page can restore it |
| Settings | Galactic war, co-op, local PlayFab, play with strangers (or HellForge's default), sandbox, changing mods without restarting, the game's launch options, engine log, full crash dump, Forge Credits share, language packs |
| Mod sync | Sharing your gameplay mods with players who join, listing your public lobbies on the Galactic Map, showing lobbies with host-enforced stat changes, the blocked mods (unblock), and the mods downloaded from hosts (count, size, delete) |
| Logs | Each session's logs, export and crash report (nothing is uploaded) |
| Title-bar icon | HellForge's own icon, read from the player's `HellForge.exe` at run time; the plugin does not include it. An anvil is shown when HellForge is not found |

## Method

The plugin uses only what HellForge documents for external launchers
(`app\sdk\docs\external-launchers.md`): `HellForge.Cli.exe`, the `mods` folder and its
`hellforge-mods.json`. It never writes
to HellForge's game copy, `app` folder, backend or network block, and, like HellForge, refuses mod
changes while a session is running.

## Tasks that require HellForge.exe

- The first setup and repairs: copying the game, Steam's ownership check, the network block.
- Trusted mods of mod sync (Untrust).
- Account sync (Sync Inventory).
- Premium warbonds a profile does not own: HellForge serves a paid item only to a player who owns it on
  the live game, whatever the profile holds, and the editor does not try to change that. The plugin opens HellForge for it, and warns when the Windows
  system proxy is enabled, which causes the sync to fail.

## Build

`build.bat` runs the tests and writes `release\HD2QD.HellForge.dll` (requires the .NET SDK 8 or
later; set `DOTNET` to its `dotnet.exe` when it is not on PATH). Add the DLL on Quick Deploy's
Plugins page.

## License

MIT. HellForge is by SkyeShade (GPL-3.0); this plugin only runs its command line and reads its
icon, its item catalogue (from `app\backend\HellForge.LocalBackend.exe`) and `app\progression-data.json`
from the installed program, at run time.

Item names: the game's own text, read from HellForge's game copy at run time, looked up by the English
names of [helldivers-2/json](https://github.com/helldivers-2/json) (`src\Data\community-names.json`:
item names and each warbond's items; MIT License, Copyright (c) 2024 Helldivers 2 Community).
