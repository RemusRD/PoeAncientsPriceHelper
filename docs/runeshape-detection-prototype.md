# Runeshape Detection Prototype Notes

## Goal

Move the app toward calibration-free Runeshape pricing without making the detector more fragile than the manual calibration it replaces.

## TOS-safe design boundary

Keep the tool read-only and bounded to visible UI:

- Use normal screenshots, OCR, public/cached price data, foreground-window checks, live watch, and an explicit manual check button.
- Do not read or patch game process memory.
- Do not inject DLLs, hook the game renderer, or inspect client data files.
- Do not sniff traffic or connect to game servers through anything other than the official client.
- Do not click, scroll, move the mouse, press game keys, or automate gameplay decisions.
- Do not query price APIs per scan; keep local cache refreshes aligned with upstream cache headers.

This does not make the tool guaranteed-approved by GGG, but it avoids the categories their terms and forum guidance explicitly warn about: client modification, bots/automation, unauthorized server connections, extraction/reverse engineering, and one-key multi-action automation.

## Approaches considered

1. Manual calibration plus current brightness gate
   - Lowest implementation risk, but it preserves the main user friction.
   - Brightness is not a domain signal; bright game backgrounds can look like an open panel.

2. Resolution profile only
   - Simple and validated by another Runeshape tool.
   - Works best when the PoE2 client rect is known.
   - Still needs validation because screenshots can be cropped, windowed, scaled, or UI-brightness-dependent.

3. Resolution profile plus row-frame validation
   - Best first slice.
   - Profile gives a left-panel search box; row-frame detection proves the Runeshape list is actually visible and gives row centers.
   - Lets OCR run on row name strips instead of a broad calibrated rectangle.

4. Full CV auto-location
   - Attractive end goal, but currently too broad for v1.
   - Generic parchment/panel matching catches unrelated UI and background.
   - Rune-template matching needs cleaner templates and can overmatch parchment/text.

5. Clipboard parsing
   - TOS-friendly when the game itself exposes item text through Ctrl+C.
   - Not sufficient for Runeshape visible-row pricing because the list is not ordinary item hover text.

## Prototype

Script:

```sh
python3 tools/prototypes/detect_runeshape_rows.py IMAGE.png --out-dir /tmp/poe-ancients-row-detector
```

Optional explicit region:

```sh
python3 tools/prototypes/detect_runeshape_rows.py IMAGE.png --region 0,0,500,570
```

The output JSON includes:

- `boundary_y`: horizontal row-frame candidates.
- `row_center_y`: geometric centers between row boundaries.
- `text_center_y`: best-effort dark-text centers inside each row band.
- `confidence`: prototype confidence from regular row spacing.
- `annotated_path`: image with blue boundaries, green row centers, and magenta text centers.

## Current sample results

`issue8-overlay.png`:

- Finds 7 rows.
- Confidence is low because the image is cropped and already contains the price overlay.
- Geometric centers are pulled upward on several rows; text centers align closer to the rendered names/prices.

Spanish 1600x785 screenshot:

- Finds 11 visible rows with strong spacing confidence.
- Geometry works without relying on Spanish OCR text, which supports keeping name matching English-only.
- The broad default search box includes some item-tooltip signal; a real client/profile crop should be tighter.

## Recommendation

Implement the first fork slice as:

1. Detect PoE2 foreground window and client rect on Windows.
2. Resolve a profile-based Runeshape search region from the client resolution.
3. Validate/refine rows with horizontal row-frame detection.
4. OCR only the row name strips.
5. Resolve OCR names against cached prices locally.
6. Draw overlay rows from the detected row centers.

Keep scrolling out of v1. It can be added later if visible rows are not enough in practice.

## Implemented first slice

The fork now has the first app integration:

- `PoeWindowLocator` finds the foreground Path of Exile 2 client rect on Windows.
- `RuneshapeRegionProfiles` resolves a profile-guided left-panel search region, expanded upward/leftward so top visible rows are not skipped.
- `RuneshapeRowDetector` validates regular row frames and returns row bands/centers.
- `OcrScanner.ScanRows` OCRs bounded row strips with Tesseract single-line mode.
- `ScanEngine` defaults to auto-detection and only uses manual calibration as a fallback.

Remaining proof needs a Windows smoke test with PoE2 running, because this Mac cannot run the WPF app or capture a real PoE2 client window.
