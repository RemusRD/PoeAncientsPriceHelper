# Windows Source Workflow

This repo is currently easiest to tweak from the source snapshot copied to the shared Windows box.
The app is two pieces:

- `poc/overlay-shell`: Electron control panel, tray app, and click-through price overlay.
- `src/PoeAncientsSidecar`: C#/.NET 8 capture, row detection, OCR, pricing, and diagnostics engine.

The Electron app starts the sidecar in dev mode with `dotnet run`, so normal iteration does not
need packaging.

## Shared Windows Location

The current editable source snapshot is copied here:

```text
C:\Users\richa\Documents\codex\ssh-debug\runeshape-current\source\PoeAncientsPriceHelper-current
```

The snapshot intentionally excludes generated folders such as `.git`, `dist`, `node_modules`,
`bin`, and `obj`. Run restores locally on Windows after copying.

## Prerequisites

- Windows 10/11.
- .NET 8 SDK.
- Node.js 20+ or 22+.
- Path of Exile 2 running in English for OCR/pricing names.

## First Run From Source

Open PowerShell in the snapshot folder:

```powershell
cd C:\Users\richa\Documents\codex\ssh-debug\runeshape-current\source\PoeAncientsPriceHelper-current
dotnet test .\src\PoeAncientsPriceHelper.Core.Tests\PoeAncientsPriceHelper.Core.Tests.csproj
dotnet build .\src\PoeAncientsSidecar\PoeAncientsSidecar.csproj
cd .\poc\overlay-shell
npm install
npm start
```

Then focus Path of Exile 2 and open the Runeshape panel. The app watches the foreground PoE2
window, screenshots the Runeshape panel area, OCRs visible rows, and draws prices beside the panel.

## Debug Captures

Press `PageDown` when a visible bug happens. The app writes:

- `resources\sidecar\overlay-debug\<capture-id>\monitor.png`
- `resources\sidecar\overlay-debug\<capture-id>\overlay.png`
- `resources\sidecar\overlay-debug\<capture-id>\state.json`
- `resources\sidecar\overlay-debug\<capture-id>\replay.html`
- `resources\sidecar\diagnostics\<timestamp>-<capture-id>-<build>.zip`

Those files are enough to inspect row positions, overlay drift, OCR output, price matching, and logs
without needing to reproduce the exact moment manually.

## Packaged Local Build

Use this when the dev app works and you want a folder that behaves like the bridge-deployed app:

```powershell
cd C:\Users\richa\Documents\codex\ssh-debug\runeshape-current\source\PoeAncientsPriceHelper-current
dotnet publish .\src\PoeAncientsSidecar\PoeAncientsSidecar.csproj -c Release -r win-x64 --self-contained false -p:BuildStamp=local-dev -o .\build\sidecar
cd .\poc\overlay-shell
npx electron-builder --win nsis
```

The bridge deploy helper on macOS uses `@electron/packager` plus a sidecar publish into
`resources\sidecar`; this document keeps the Windows-side loop focused on editing and debugging.

## Data And Fixture Policy

Keep the large 65-frame capture corpus out of git. The repo contains only selected `663x715` PNG
fixtures that are small enough for durable tests. Use the shared capture folder for full-resolution
recordings and temporary review sets:

```text
C:\Users\richa\Documents\codex\ssh-debug\runeshape-current\captures\
```
