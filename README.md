# Runeshape Price Helper

> Beta: this fork is being rewritten around a new Runeshape-first UX and cleaner ownership
> boundaries. Expect active changes while OCR/pricing diagnostics are hardened.

A lightweight screen overlay for **Path of Exile 2**. Press one hotkey while the Runeshape
panel is visible; the app screenshots the panel, reads visible reward rows with OCR, looks up
cached prices from [poe.ninja](https://poe.ninja/poe2), and draws a small topmost price overlay
next to each item.

## Features

- **On-demand prices** next to each visible list row, sourced from poe.ninja (auto-refreshed every 30 minutes).
- **League selector** for Aldur SC, Aldur HC, Standard SC, and Standard HC.
- **Stack-aware** — shows the total and the per-item price, e.g. `2 (0.5 each)`.
- **Uncut gems** (skill / spirit / support) priced by exact type **and level** — a row shows `?`
  rather than a guessed price if the gem type or level can't be read cleanly (neighbouring levels
  can differ several-fold, so a wrong-level price would be misleading).
- **Click-through topmost overlay** placed beside the detected Runeshape rows.
- **Auto-detects the Path of Exile 2 monitor/window** and uses a Runeshape panel profile; no manual calibration in the normal flow.
- **Diagnostics bundle** button that captures version, monitor/window binding, screenshots, row probes, OCR probes, and logs for testing.
- **Hotkey:** `PageUp` checks the visible Runeshape panel by default, and can be changed in the app. `Esc` / `Ctrl+Click` hide the overlay.

## Usage

1. Launch the app and wait for prices to load.
2. Open the Runeshape panel in Path of Exile 2.
3. Press the configured hotkey, or click **Check now** in the app.
4. If no price appears, click **Collect support bundle** and share the zip.

## Download & run

Grab the latest `RuneshapePriceHelper-vX.Y.Z-win-x64.zip` from the
[**Releases**](../../releases) page, unzip it anywhere, and double-click **`Start.cmd`**.
No install and no .NET runtime required — it's a self-contained Windows x64 build.

Full usage instructions (with screenshots) are in the `README.html` included in the download.

> Windows SmartScreen may warn that the app is unsigned — click **More info → Run anyway**.

## Build from source

Requires the .NET 8 SDK.

```sh
# run tests
dotnet test src/PoeAncientsPriceHelper.Tests/

# build a self-contained release
dotnet publish src/PoeAncientsPriceHelper/ -c Release -r win-x64 --self-contained true -o publish
```

## Tech

WinForms control panel + WPF overlay renderer, Tesseract OCR, .NET 8 (`net8.0-windows`).

## Credits

This fork builds on and learns from:

- [pedro-quiterio/PoeAncientsPriceHelper](https://github.com/pedro-quiterio/PoeAncientsPriceHelper), the original app/fork base.
- [Barragek0/RuneshapePriceChecker](https://github.com/Barragek0/RuneshapePriceChecker), useful prior art for resolution profiles and OCR preprocessing.

This fork removes upstream donation branding from the app UI because the Runeshape auto-detection and diagnostics work are now maintained separately here.

## License status

The project is in a clean-room rewrite phase. See [docs/license-status.md](docs/license-status.md)
for the current upstream and dependency license notes before redistributing builds.
