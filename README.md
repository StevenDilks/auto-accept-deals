# AutoAcceptDeals

A [Schedule I](https://store.steampowered.com/app/3164500/) mod that automatically counter-offers and accepts incoming customer deals at the highest price the customer will accept.

## What it does

- **Counter-offers automatically** — responds to every customer deal with the highest price they'll accept at 100% probability, using the same deterministic formula the game uses internally (ported in `ProbabilityFormula.cs`).
- **Rounds requested quantity** — rounds the customer's requested quantity up to a configurable multiple, then climbs further multiples above that floor looking for a higher-revenue quantity that still clears 100% acceptance, stopping at the first infeasible step. Set `roundingMultiple` to `0` to pass the quantity through unchanged.
- **Applies a delivery location** — stamps a delivery location on each accepted contract, either one global location for all customers or a per-region location.
- **Applies a delivery window** — assigns a delivery time window: a fixed slot, a randomly chosen slot from the four available windows, or defers to the player (lets the game's normal scheduling UI fire).

## Supported game version

Schedule I `0.4.6f11` · Unity `2022.3.62f2` · MelonLoader `0.7.3 Open-Beta`

If the mod detects a version mismatch (a required game symbol is missing or renamed), it disables itself and logs all missing symbols to `Latest.log` rather than crashing.

## Install

### 1. Find your Schedule I install directory

The default Steam path is:

```
C:\Program Files (x86)\Steam\steamapps\common\Schedule I\
```

If you moved your Steam library, right-click the game in Steam → **Manage → Browse local files**.

### 2. Install MelonLoader

Download and run the [MelonLoader](https://melonwiki.xyz/) installer (`MelonLoader.Installer.exe`). Select `Schedule I.exe` from your install directory and choose version `0.7.3 Open-Beta`. The installer patches the game executable — you do not need to copy any files manually.

Launch the game once after installing MelonLoader and then close it. This lets MelonLoader finish its first-run setup and create the `Mods\` folder inside your install directory.

### 3. Download the mod

Go to the [Releases](../../releases) page and download `AutoAcceptDeals.dll`.

### 4. Drop the DLL into Mods

Copy `AutoAcceptDeals.dll` into:

```
<Schedule I>\Mods\AutoAcceptDeals.dll
```

### 5. Verify it loaded

Launch the game and load a save. Open:

```
<Schedule I>\MelonLoader\Latest.log
```

Look for this line:

```
[AutoAcceptDeals] AutoAcceptDeals loaded — enabled. Press O in-game to toggle, F8 to open settings.
```

If you see an error line mentioning `missing game symbols` instead, the mod has detected a version mismatch and disabled itself. Check that your game and MelonLoader versions match those listed above.

## Default behavior

Out of the box, with no configuration:

- Counter-offers every incoming deal at the maximum price the customer accepts.
- No quantity rounding (`roundingMultiple = 0`).
- No delivery location stamped — the customer's existing default location is used.
- **Delivery time is left to you** (`timeMode = WaitForPlayer`): the mod accepts the contract but does not schedule a delivery window, so the game's normal scheduling UI still fires.

To configure a delivery location or auto-schedule a time window, press **F8** to open the settings panel. Delivery locations are auto-discovered from the game's map data — they won't appear in the panel until you have received at least one customer deal text.

## Hotkeys

| Key | Action |
|-----|--------|
| `O` | Toggle mod on/off (logged to console). Ignored while a text input is focused. |
| `F8` | Open/close the in-game settings panel. Suppresses camera, movement, and inventory input while open. |

## Settings

Stored at `<Schedule I>\UserData\AutoAcceptDeals\settings.json`. All fields can be configured from the F8 panel in-game — you should not need to edit the file directly.

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `roundingMultiple` | int | `0` | Round requested quantity up to the nearest multiple. `0` = pass through unchanged. |
| `locationMode` | `Global` / `PerRegion` | `Global` | One location for all customers, or a separate location per map region. |
| `globalLocationGuid` | string | `null` | GUID of the delivery location used in Global mode. Set via the F8 panel — auto-discovered on the first deal. |
| `regionLocations` | object | all `null` | Region name → location GUID map for PerRegion mode. Set via the F8 panel. |
| `timeMode` | `Fixed` / `Randomize` / `WaitForPlayer` | `WaitForPlayer` | How the delivery window is assigned. |
| `fixedWindow` | `Morning` / `Afternoon` / `Night` / `LateNight` | `Morning` | Delivery window used when `timeMode` is `Fixed`. |

## Build

This project references DLLs from your local Schedule I install. By default it expects:

```
C:\Program Files (x86)\Steam\steamapps\common\Schedule I
```

If your install is elsewhere, set the `SCHEDULE1_PATH` environment variable or edit `Schedule1Path` in `Directory.Build.props`.

```
dotnet build -c Release
```

The built DLL is at `AutoAcceptDeals/bin/Release/AutoAcceptDeals.dll`.

## Release

CI cannot build the mod — the Schedule I IL2CPP DLLs are not available on hosted runners. To cut a release:

1. Tag the commit and push — the workflow creates a GitHub Release automatically.
2. Build locally and upload the artifact.

```
git tag v0.1.1
git push origin v0.1.1        # fires the workflow, creates the GH release
dotnet build -c Release
gh release upload v0.1.1 AutoAcceptDeals/bin/Release/AutoAcceptDeals.dll
```

Tags containing a hyphen (e.g. `v0.1.0-beta`) are automatically marked as pre-releases.

## License

MIT — see [LICENSE](LICENSE).
