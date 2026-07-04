# Not Alone, Exile

_You are not alone, exile._

> Beta: this fork is being rewritten around a new Runeshape-first UX and cleaner ownership
> boundaries. Expect active changes while OCR/pricing diagnostics are hardened.

A lightweight screen overlay for **Path of Exile 2**. While the Runeshape panel is visible, the
app screenshots the panel, reads visible reward rows with OCR, looks up cached prices from
[poe.ninja](https://poe.ninja/poe2), and draws a small topmost price overlay next to each item.

## Features

- **Live prices** next to each visible list row, sourced from poe.ninja (auto-refreshed every 30 minutes).
- **League selector** for Aldur SC, Aldur HC, Standard SC, and Standard HC.
- **Stack-aware** — shows the total and the per-item price, e.g. `2 (0.5 each)`.
- **Uncut gems** (skill / spirit / support) priced by exact type **and level** — a row shows `?`
  rather than a guessed price if the gem type or level can't be read cleanly (neighbouring levels
  can differ several-fold, so a wrong-level price would be misleading).
- **Click-through topmost overlay** placed beside the detected Runeshape rows.
- **Auto-detects the Path of Exile 2 monitor/window** and uses a Runeshape panel profile; no manual calibration in the normal flow.
- **Diagnostics bundle** command that captures version, monitor/window binding, screenshots, row probes, OCR probes, and logs for testing.
- **Simple first-version controls:** choose a league and let live watch handle the overlay.

## Usage

1. Launch the app and wait for prices to load.
2. Open the Runeshape panel in Path of Exile 2.
3. Keep the game focused; the overlay updates while the panel is visible.
4. If a weird row, price, or overlay placement bug appears, press **PageDown** once. The app saves
   an overlay screenshot/replay plus a sidecar diagnostics bundle under `resources/sidecar`.

## Download & run

This fork is currently tested through local Windows builds rather than polished public releases.
For the shared Windows test box, use the source snapshot under:

```text
C:\Users\richa\Documents\codex\ssh-debug\runeshape-current\source\PoeAncientsPriceHelper-current
```

See [docs/windows-source-workflow.md](docs/windows-source-workflow.md) for the exact Windows build,
run, and debug-capture workflow.

> Windows SmartScreen may warn that the app is unsigned — click **More info → Run anyway**.

## Build from source

Requires the .NET 8 SDK and Node.js.

```sh
# restore Electron dependencies
cd poc/overlay-shell
npm install

# run the dev app; it starts the C# sidecar with dotnet run
npm start
```

For packaged Windows builds and the shared bridge workflow, see
[docs/windows-source-workflow.md](docs/windows-source-workflow.md).

## Tech

Electron control panel + click-through overlay, C#/.NET 8 Windows sidecar, Tesseract OCR, and
cached poe.ninja prices.

## Credits

This fork builds on and learns from:

- [pedro-quiterio/PoeAncientsPriceHelper](https://github.com/pedro-quiterio/PoeAncientsPriceHelper), the original app/fork base.
- [Barragek0/RuneshapePriceChecker](https://github.com/Barragek0/RuneshapePriceChecker), useful prior art for resolution profiles and OCR preprocessing.

This fork removes upstream donation branding from the app UI because the Runeshape auto-detection and diagnostics work are now maintained separately here.

## License status

The project is in a clean-room rewrite phase. See [docs/license-status.md](docs/license-status.md)
for the current upstream and dependency license notes before redistributing builds.
