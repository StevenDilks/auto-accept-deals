# AutoAcceptDeals

A [Schedule I](https://store.steampowered.com/app/3164500/) mod that automatically accepts incoming customer deals.

## Requirements

- Schedule I (IL2CPP build)
- [MelonLoader](https://melonwiki.xyz/) 0.6.x

## Install

1. Install MelonLoader into your Schedule I install directory.
2. Drop `AutoAcceptDeals.dll` into `<Schedule I>/Mods/`.
3. Launch the game.

## Build

This project references DLLs from your local Schedule I install. By default it expects:

```
C:\Program Files (x86)\Steam\steamapps\common\Schedule I
```

If your install is elsewhere, set the `SCHEDULE1_PATH` environment variable, or edit `Schedule1Path` in `Directory.Build.props`.

```
dotnet build -c Release
```

The built `.dll` is at `AutoAcceptDeals/bin/Release/AutoAcceptDeals.dll`.

## License

MIT — see [LICENSE](LICENSE).
