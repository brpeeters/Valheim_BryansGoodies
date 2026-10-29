# Bryan's Goodies — Valheim mod (MelonLoader)

Current version: **1.1.0**. The number is `<ModVersion>` in `BryansGoodies.csproj`, to be bumped for every shared release; it is propagated to `Main.Version`, the F7 menu title, the log line at load time, the file properties and `version.txt`.

A single mod bundling several improvements, each one toggleable from an in-game menu (**F7**).

| Feature | File | In short |
|---|---|---|
| Nothing lost on death | `KeepItems.cs` | No tombstone: you keep your inventory and equipment. |
| No skill drain | `NoSkillDrain.cs` | Skills are not lowered on death. |
| Compass | `Compass.cs` | Skyrim-style bar at the top of the screen showing the map markers. **F8** to show or hide it. Filter by marker type (fire, house, portal, tombstone, players...) and an option to hide checked markers, in the F7 menu. |
| Player color | `PlayerColors.cs`, `Recolor.cs` | Chosen color (F7 menu or `/color blue`) visible on the name above the head, in the chat, on the map, on the compass and on the equipment. The hue is replaced, not multiplied. |
| Quick stack | `QuickStack.cs` | **K** stacks into nearby chests the items they already contain. **Left Alt + click** in the inventory marks a favorite (golden star), never stacked away. "Log" option (F7 menu, on by default): every press of K is detailed in `BepInEx\LogOutput.log` (chests found, requests, replies, summary). |
| Craft from chests | `CraftFromContainers.cs` | The workbench and the hammer also use the contents of nearby chests. The display counts every reachable chest; ownership of a chest is only requested when the workbench is opened, on the Craft / Upgrade click and in build mode, and items are only taken from owned chests. |
| Named chests | `ChestNames.cs` | **Left Shift + E** on a chest (key changeable in the F7 menu) gives it a name (e.g. "Wood"), shown instead of "Chest" on hover and as the title of the open chest. Stored in the chest itself, so every player with the mod sees it; an empty name restores the original one. Refused while another player has the chest open. |
| Craft quantity | `CraftQuantity.cs` | A number field to the left of the **Craft** button: type how many items to craft. It reuses the game's own multi-craft (Shift + Craft = x5): requirements and inventory space are checked for the whole batch, one progress bar (vanilla pace, 1.2 s per item), skill raised for each item. Hidden for upgrades. |
| Recipe search | `CraftSearch.cs` | A search field above the recipe list (Craft and Upgrade tabs): only the recipes whose name contains the typed text are listed, case and accents ignored. While typing, the game's keys are ignored (Tab or E no longer close the inventory). Cleared when the inventory closes. |
| `/call` command | `CallCommand.cs` | Calls a method, or reads/writes a game field through reflection, e.g. `/call me.SetHealth 50`. Runs on your machine only. |
| `/claude` command | `ClaudeBridge.cs` | `/claude <request>`: ask Claude for anything (spawn a creature, get an item, build something, change the weather). The request and its context (position, biome, time, inventory...) go to every client; the one set as **host** (menu F7, Bryan's machine) writes it to `UserData\BryansGoodies\claude\inbox.jsonl`, Claude answers with a script in `claude\outbox\<id>.txt`, the host trims it to the requester's policy (`Claude_Policy`, default `default=all`: everyone has the same rights; levels none / fun / spawn / give / build / call / all per name if ever needed) and sends it back; the requester's own game runs it. Script language (`ClaudeBridge.cs`, `ClaudeVerbs.cs`): messages `say` (whisper from Claude, local), `center`, `chat`; creation `spawn`, `give`, `place` (y = `g` for ground), `placerel`, `stand` (item on an item stand); a **selector + verb** language where every verb takes the same selector words (categories `pieces` / `items` / `creatures` / `players` / `nature` / `all`, zone `r` / `at` / `here` / `y` / `cone` / `box`, pick `like` / `nearest` / `look` / `id`): reading `scan`, `prefabs like`, `info`, `where`, `look` (results come back from the requester's game into `claude\info.jsonl`), acting `remove`, `kill`, `heal`, `tame`, `push ... force n`, and the generic `on <selector> <method> [args]` / `set <selector> <field> <value>` (reflection on the selected objects, so most new needs require no new code); terrain `terrain ops` / `terrain <op> x y z [times]` (the hoe's operations, capped at about 8 m by the game); player and world `weather`, `time`, `fly`, `god`, `tp`, `repair`, `call` (reflection, result reported back). Shortcuts kept: `destroy`, `shout`, `clearitems`. Rules enforced by the requester's game: pieces removed by Claude give no materials back (add `drop` to get them), and `give` / `place` / `stand` refuse anything the player has not unlocked yet (`known` lists what is unlocked; the host may append `anyway`). Rate limit per player (`Claude_RequestsPerMinute`). The request text is untrusted data for Claude; the host-side policy is the hard limit. |
| Configuration menu | `ConfigMenu.cs` | F7 menu (IMGUI window): the mouse is released and the character stops moving while it is open. |
| Chest access | `Containers.cs` | Shared part between quick stack and crafting (see below). |

## Multiplayer

- **Nothing to install on the server**, whether it is dedicated or the game is hosted by a player.
- **Colors** go through a network message sent to everyone (`BG_PlayerColor`), which the server forwards without needing the mod. The color is also stored in the character's network data. Players without the mod do not see the colors.
- **Chests**: the game only lets one player at a time modify a chest (its network "owner"). The mod requests access the same way the game's "Stack all" button does (`RPC_RequestStack`). The chest is refused if someone else has it open or if it is private, and nothing is modified until access has been granted.

## Two files: the loader and the core

| File | Role | Where |
|---|---|---|
| `BryansGoodies.dll` | **Loader** (`Loader\` project): minimal, stable MelonLoader mod. Loads the core from its bytes, swaps cores in the same frame with a state handoff, falls back to the previous core if the new one fails, reloads the core when its file changes, and as a rescue downloads the latest published core when none can be loaded. It is the only file friends install, and it updates itself for the next launch through the core. | `Mods\` |
| `BryansGoodies.Core.dll` | **Core**: everything else, including the whole update mechanics (`Updater.cs`: feed, channels, downloads, announcements, triggers, notices). Hot-reloadable. Not a MelonLoader mod: never put it in `Mods`. | `UserData\BryansGoodies\` (created and filled by the loader) |

The core receives at start a dictionary of loader services (`host`): file paths, `core.install` (swap to a downloaded core, done by the loader in its own update loop) and `loader.install` (put a new loader in place for the next launch).

## Installing on a friend's machine

1. MelonLoader installed (standalone, or via BepInEx with the *BepInEx.MelonLoader.Loader* plugin).
2. Copy `BryansGoodies.dll` (the loader, in [dist/](dist/) of this repository) into `Valheim\Mods\` or `Valheim\MLLoader\Mods\` depending on the install. Someone who had the old mod simply replaces their file.
3. Launch the game: the loader downloads `BryansGoodies.Core.dll` from GitHub and activates it right away. Nothing else to do.

## Automatic updates

- The loader follows a **feed**: a directory holding `version.txt`, `BryansGoodies.Core.dll`, `loader.txt` and `BryansGoodies.dll`. By default it is the `dist/` folder of this repository on the branch named by the `Channel` setting: `main` for everyone, another branch (for example `beta`) for volunteers testing ahead of time.
- It checks the feed at game launch, when another player on the same channel announces a newer version (network message `BG_Version`, `version|channel`, sent by each client that follows the GitHub feed when its core starts and then every minute, relayed by the server without installing anything; a client on a local feed never announces its own build, so a build under test stays private; after `publish.cmd` it announces the version just published instead, for 10 minutes, through the `announce` file the script drops into `UserData\BryansGoodies`), and as a fallback every 30 minutes. A failed check is retried 90 seconds later, up to three times.
- A newer core is downloaded in the background (message top-left), verified (right assembly, version matching `version.txt`), then swapped in right away: the old core hands its live state over (pending chest requests, quick stack in progress, open F7 menu, other players' colors, gift countdown), its patches are removed and the new core is started in the same frame, so nothing is ever rendered without the mod. In-game message "Bryan's Goodies updated to 1.3.7", followed by the release notes if `version.txt` has any. The previous version is kept as `BryansGoodies.Core.dll.previous`.
- If the new core fails to start, or keeps throwing for about five seconds, the previous one is restored automatically and the failed version is remembered (`BadVersion`) so it is not fetched again until a forced update.
- `version.txt` format: first line the version; a line `force` makes everyone install that version even if it is older (rollback of a bad release); any other line is shown in game as release notes.
- The loader updates itself too: when `loader.txt` announces a newer loader, the core downloads the new `BryansGoodies.dll` and the loader puts it in place for the next launch (the running DLL is renamed, Windows allows that). No manual redistribution.
- The state handoff is a dictionary of named entries holding only shared types (game objects, numbers, Unity structs), never a type from the mod itself. The new core reads the entries it knows, may convert older formats (the entry `from` carries the previous version), and skips an entry that fails rather than refusing to start.
- Local feed for testing without GitHub: set `Feed` to a local directory (for example `E:\claude\valheim\valheim-bryans-goodies\dist\`) in `MelonPreferences.cfg`, section `[BryansGoodiesLoader]`. The whole download / verify / install path then runs from those files, and a change to that `version.txt` is noticed within a second. On a local feed a rebuild with the same version number is installed too, as long as the DLL really changed (in-game event "updated to 1.4.3: local build"): no need to bump the version while iterating. The build never copies the core into `UserData` directly; the feed is the only install path, exactly like on the friends' machines. With any feed, dropping an empty file named `check-now` into `UserData\BryansGoodies` forces a check right away.
- Settings in `[BryansGoodiesLoader]`: `AutoUpdate`, `UpdateLoader`, `Channel`, `Feed`, `WatchLocalFile` (reload when the local core file changes, handy when developing), `CheckIntervalMinutes`, `BadVersion`. Trigger files in `UserData\BryansGoodies`: `check-now`, `announce` (dropped by `publish.cmd`), `install` (handled by the loader itself, independently of the core: its content is the path of a core DLL to install, a safety hatch if the core is stuck), `notice` (its text is shown as an event). Events (updated, published, loader ready...) appear in the game's center text; the banner at the bottom of the screen shows only what is going on right now (checking, downloading, installing, retry countdown, loader waiting for a restart, announcing) and stays empty otherwise.
- A loaded .NET assembly never unloads: the old one stays in memory, inert. Irrelevant in practice.

## Publishing a release

1. Change `<ModVersion>` in `BryansGoodies.csproj`.
2. Run `publish.cmd`: builds both projects, copies `BryansGoodies.Core.dll` into `UserData\BryansGoodies` of the local installs and the loader into `Mods`, updates `dist\` (`BryansGoodies.Core.dll`, `BryansGoodies.dll`, `version.txt`), then commits and pushes to GitHub. `build.cmd` does the same without publishing.
3. That's it: friends connected at the same time as you receive the version within a minute; the others at their next launch.

The loader itself (`Loader\Loader.cs`, version `Loader.Version`) does not hot-update: change it as little as possible, and redistribute it by hand if needed.

## Building

Prerequisites: the .NET SDK (6 or newer), Valheim and MelonLoader installed.

```
build.cmd
```

- Valheim is found through Steam (its install location for app 892970, whatever the library), see `Directory.Build.props`. To override: the `VALHEIM_DIR` environment variable or `-p:Valheim="...\Valheim"`.
- The build also creates `game\`, a junction (git-ignored) to that Valheim folder: `CLAUDE.md` and `publish.cmd` use paths relative to the repository through it (`game\MLLoader\UserData\BryansGoodies\...`), so nothing depends on the drive. Delete the junction with `rmdir game`, never a recursive delete (that would empty the game folder).
- MelonLoader is found automatically, whether installed standalone or via BepInEx. Otherwise: `-p:MelonDir="path\to\the\folder\of\MelonLoader.dll"`.

.NET Framework 4.8 projects built against the game's DLLs (`valheim_Data\Managed`). Changes to the game go through Harmony patches, which can be removed for hot reloading.
