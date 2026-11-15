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
| This player only | Marks a code mod (prebuilt or built from its sources) `"coop": "per-player"` in HellForge's copy, and marks it again when an update arrives without the mark. "Set all gameplay mods to this player only" (on by default) marks every shared gameplay code mod except HellForge's own, including mods added later; while it is on, each mod's own switch follows it and is locked. A gameplay mod it leaves shared (no code, or refused by HellForge, such as a mod with a data spec) states why in its row, and a deploy reports it as deployed and still shared. Cosmetic mods have no switch: HellForge never compares them |
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

## Changing HellForge versions

Each HellForge version (a numbered release or a nightly) builds its own files for the mods: the compiled code mods,
the resources made from data specs, and its own copies of the bundled mods. These files do not work in another
version. Mixing versions can crash the game or cause resource errors.

**Never:**

| Operation | Why |
|---|---|
| Replacing only HellForge's program files (`HellForge.exe`, `app`) to change versions | The mods, profiles and game copy remain those of the other version |
| Copying a `mods` folder, or single mods from it, from an installation of another version | Its code mods were compiled, and its resources numbered, by that version. This includes HellForge's own mods (`hellforge-editor`, `hell-forge`, `vanilla-plus`) |
| Copying profiles or saves that a newer version has opened into an older version | A newer version can change profiles in ways an older one cannot read |
| Copying the game copy (`game`) from another installation by hand | Setup copies the game and checks every file against Steam's hashes |
| Installing a nightly over the installation you play on | A nightly is untested and is replaced by the next one; HellForge recommends a folder of its own |
| Running two installations at the same time | HellForge supports it only in a test mode |

**Changing versions correctly:**

1. To a newer release: run its `HellForge-Setup.exe` and pick the existing installation (Update).
2. To an older release, or to a nightly: install it into a new, empty folder with its own `HellForge-Setup.exe`.
3. In the plugin, choose that installation with **Select HellForge.exe…**.
4. Deploy from Quick Deploy. Each library mod is placed afresh, and HellForge compiles the code mods for its own
   version.
5. Install mods from outside the library from their original download, never from another installation.
6. Use profiles and saves from a backup made before the newer version opened them, or start a new profile.
7. For co-op, every player uses the same HellForge version, the same nightly included.

### 更换 HellForge 版本

每个 HellForge 版本（正式版或每夜版）都会为 MOD 生成自己的文件：编译后的代码 MOD、按数据规格生成的资源，以及自带
MOD 的副本。这些文件不能在其他版本中使用，混用会导致游戏闪退或资源错误。

**请勿进行以下操作：**

| 操作 | 原因 |
|---|---|
| 只替换 HellForge 的程序文件（`HellForge.exe`、`app`）来更换版本 | MOD、档案和游戏副本仍是原版本的 |
| 从其他版本的安装中复制 `mods` 文件夹或其中的 MOD | 其中的代码 MOD 由那个版本编译，资源由那个版本编号。HellForge 自带的 MOD（`hellforge-editor`、`hell-forge`、`vanilla-plus`）同样如此 |
| 把被新版本打开过的档案或存档复制到旧版本 | 新版本可能更改档案格式，旧版本无法读取 |
| 手动从其他安装复制游戏副本（`game`） | 安装程序会复制游戏，并按 Steam 的哈希逐一校验 |
| 把每夜版安装在日常使用的安装上 | 每夜版未经测试，次日即被替换；HellForge 建议单独安装 |
| 同时运行两个安装 | HellForge 仅在测试模式下支持 |

**正确的做法：**

1. 升级到更新的正式版：运行其 `HellForge-Setup.exe`，选择现有安装（更新）。
2. 换回较旧的正式版，或改用每夜版：用该版本的 `HellForge-Setup.exe` 安装到一个新的空文件夹。
3. 在插件中点击**选择 HellForge.exe…**，选择该安装。
4. 在 Quick Deploy 中部署：库中的 MOD 会重新放入，HellForge 按自己的版本编译代码 MOD。
5. 不在库中的 MOD，从原始下载重新安装，不要从其他安装复制。
6. 档案和存档使用新版本打开之前的备份，或新建档案。
7. 联机时所有玩家使用同一 HellForge 版本，每夜版也须为同一天的版本。

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
