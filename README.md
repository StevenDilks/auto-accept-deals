# AutoAcceptDeals

A [Schedule I](https://store.steampowered.com/app/3164500/) mod that automatically counter-offers and accepts incoming customer deals at the highest price the customer will accept.

## What it does

- **Counter-offers automatically** — responds to every customer deal with the highest price they'll accept at 100% probability, using the same deterministic formula the game uses internally (ported in `ProbabilityFormula.cs`).
- **Rounds requested quantity** — rounds the customer's requested quantity up to a configurable multiple. Set `roundingMultiple` to `0` to pass the quantity through unchanged.
- **Applies a delivery location** — stamps a delivery location on each accepted contract, either one global location for all customers or a per-region location.
- **Applies a delivery window** — assigns a delivery time window: a fixed slot, a randomly chosen slot from the four available windows, or defers to the player (lets the game's normal scheduling UI fire).

## Supported game version

Schedule I `0.4.5f2` · Unity `2022.3.62f2` · MelonLoader `0.7.1 Open-Beta`

If the mod detects a version mismatch (a required game symbol is missing or renamed), it disables itself with a single error line in `Latest.log` rather than crashing.

## Install

1. Install [MelonLoader](https://melonwiki.xyz/) `0.7.1 Open-Beta` into your Schedule I install directory.
2. Drop `AutoAcceptDeals.dll` into `<Schedule I>/Mods/`.
3. Launch the game.

## Hotkeys

| Key | Action |
|-----|--------|
| `O` | Toggle mod on/off (logged to console). Ignored while a text input is focused. |
| `F8` | Open/close the in-game settings panel. Suppresses camera, movement, and inventory input while open. |

## Settings

Stored at `<Schedule I>/UserData/AutoAcceptDeals/settings.json`. All fields can be configured from the F8 panel in-game.

| Field | Type | Description |
|-------|------|-------------|
| `roundingMultiple` | int | Round requested quantity up to the nearest multiple. `0` = pass through unchanged. |
| `locationMode` | `Global` / `PerRegion` | One location for all customers, or a separate location per map region. |
| `globalLocationGuid` | string | GUID of the delivery location used in Global mode. Set via the F8 panel — auto-discovered on the first deal. |
| `regionLocations` | object | Region name → location GUID map for PerRegion mode. Set via the F8 panel. |
| `timeMode` | `Fixed` / `Randomize` / `WaitForPlayer` | How the delivery window is assigned. |
| `fixedWindow` | `Morning` / `Afternoon` / `Night` / `LateNight` | Delivery window used when `timeMode` is `Fixed`. |

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

1. Tag the commit and push — the workflow creates a GitHub Release skeleton automatically.
2. Build locally and upload the artifact.

```
git tag v0.1.0
git push origin v0.1.0        # fires the workflow, creates the GH release
dotnet build -c Release
gh release upload v0.1.0 AutoAcceptDeals/bin/Release/AutoAcceptDeals.dll
```

## License

MIT — see [LICENSE](LICENSE).
