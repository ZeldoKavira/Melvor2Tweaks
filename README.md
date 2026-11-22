# Melvor2Tweaks

Quality-of-life mod for **Melvor Idle 2** (Steam): an action queue, offline time bank, speed control,
item automation and many small fixes. Works on Windows, Linux / Steam Deck and macOS, and keeps itself
up to date.

> Unofficial and not affiliated with Games By Malcs. The game is in alpha and the mod patches it, so a game
> update can break things - the mod re-applies its patch by itself, but back up your save if you're worried.

## Install

Close the game first, then run one line. It finds the game in your Steam libraries, downloads the latest
release and patches the game.

**Windows** (PowerShell):

```powershell
irm https://raw.githubusercontent.com/ZeldoKavira/Melvor2Tweaks/main/install/install.ps1 | iex
```

**Linux, Steam Deck (Desktop Mode → Konsole) and macOS** (Terminal):

```bash
curl -fsSL https://raw.githubusercontent.com/ZeldoKavira/Melvor2Tweaks/main/install/install.sh | bash
```

Start the game from Steam as usual. The mod's buttons appear in the top bar, and its settings are under
**Settings → Melvor2Tweaks**.

Game installed somewhere unusual? Add the folder: `-GameDir "D:\Games\Melvor Idle 2"` (Windows, run the
script as a file) or `| bash -s -- --game-dir "/path/to/Melvor Idle 2"`.

### Updates

- **The mod updates itself.** A little after the game starts (and every 6 hours) it checks this repo's
  releases and installs a newer version in the background; most updates apply immediately. Turn this off
  under **Settings → Melvor2Tweaks → Updates**.
- **Game updates don't remove it.** The mod hooks into two libraries the game ships (not the game's own
  code), which Steam leaves alone unless they change. If an update does replace one, the mod still loads
  through the other and puts the hook back for the next start.
- Running the install line again also updates to the latest version.

### Uninstall

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/ZeldoKavira/Melvor2Tweaks/main/install/install.ps1))) -Uninstall
```

```bash
curl -fsSL https://raw.githubusercontent.com/ZeldoKavira/Melvor2Tweaks/main/install/install.sh | bash -s -- --uninstall
```

This restores the original game files and removes the mod. Your mod settings (in the game's user-data folder,
`Melvor2Tweaks`) are kept. "Verify integrity of game files" in Steam also undoes the patch.

## Features

**Actions and queue** (top bar → Actions)
- Stop any action at a level, after N items, or (Astrology) once every node of the constellation can be unlocked.
- Action queue: the next action starts when the current one stops by itself. Loop, saved queues, drag and
  drop, time left per entry and for the whole queue.
- Queue mode: click any skill's normal button to queue it; a popup asks when it should stop, with the next
  milestone levels and what they unlock.
- Crafting entries gather or make their missing materials first (e.g. ore before bars).
- "Queue" buttons beside item costs (bank upgrades, shop, quests, recipe inputs) queue what you're short.
- Drop log: everything each action has produced, for all time, with rates.

**Time**
- Time bank: offline time is banked instead of simulated; spend it when you like.
- Game speed multiplier from the top bar.

**Items** (top bar → Items)
- Per-item and per-category rules: sell, open or bury automatically, keeping a number you choose.
- Double-click a bank item to equip it.

**Skills and combat**
- Flee before dying (out of food) or when out of ammo/runes; swap to other food when it runs out; don't
  pause on boss spawns; Agility and Thieving stop before an obstacle or pickpocket that would kill you.
- Farming: Harvest All / Plant All, retry planting; grain mill top-up; bonfire relight; ranching auto-pet and repair.
- Toolbelt warnings: a red dot on a skill's equipment button when a slot is empty but your bank has an item for it, yellow when the bank has a better item for what the skill is doing (the opened panel lists which).
- Rare and Astrology bonuses activate by themselves; side drops don't stop gathering when the bank is full.
- Footer shows how many crafts your materials allow; longer event log.

Most of it can be switched off under **Settings → Melvor2Tweaks**.

## How it works

The game has no mod support, so the patcher (`mods/patcher/`) adds a module initializer to two libraries the
game ships, `Serilog.dll` and `SmartFormat.dll` (backups kept as `.orig`). It loads `mods/Melvor2Loader.dll`,
which loads `mods/Melvor2Tweaks.dll` with [Harmony](https://github.com/pardeike/Harmony). The game's own
code is never modified.

## Building

Needs the .NET 8 SDK and the game installed (the projects compile against the game's own assemblies; set
`GameDir` in `Directory.Build.props` if it isn't in the default Steam folder).

```powershell
dotnet build src/Melvor2Tweaks -c Release -p:Deploy=true   # build and copy into the game's mods folder (hot-reloads)
powershell -File tools/release.ps1                         # package dist/Melvor2Tweaks-<version>-<platform>.zip
```

`docs/GameReference.md` is a map of the game's systems and the hooks the mod uses.

## License

MIT - see [LICENSE](LICENSE). Third-party notices: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
