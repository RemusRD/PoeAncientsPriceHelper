const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs');
const path = require('node:path');

const { placeOverlayRows, layoutFullDisplayOverlay, priceStripWidth, priceTextX, rowKind, currencyIconSrc, markerText } = require('../overlay');

// ---- placeOverlayRows: window-local placement inside the full-display overlay (all DIP) ----

test('placeOverlayRows offsets the column + rows into window-local coords', () => {
  const display = { x: 0, y: 0, width: 2048, height: 1152 };
  const { localLeft, localTops } = placeOverlayRows({
    columnLeftDip: 600, rowTopsDip: [220, 400, 700], display,
  });
  assert.equal(localLeft, 600);                 // display.x is 0
  assert.deepEqual(localTops, [220, 400, 700]);
});

test('placeOverlayRows subtracts a negative display origin (second monitor)', () => {
  const display = { x: -2048, y: 0, width: 2048, height: 1152 };
  const { localLeft, localTops } = placeOverlayRows({
    columnLeftDip: -1500, rowTopsDip: [300, 600], display,
  });
  // columnLeftDip is screen-space DIP; window-local = columnLeftDip - display.x
  assert.equal(localLeft, -1500 - (-2048));     // 548
  assert.deepEqual(localTops, [300, 600]);
});

test('placeOverlayRows pulls the column left when it would overflow the display edge', () => {
  const display = { x: 0, y: 0, width: 1280, height: 720 };
  const { localLeft } = placeOverlayRows({
    columnLeftDip: 1200, rowTopsDip: [100], display, widestStrip: 260, margin: 8,
  });
  assert.equal(localLeft, 1280 - 260 - 8);      // 1012, clamped in
});

// ---- regression: the real 125% capture replays with ZERO on-screen drift ----
// Captured live on build 78: a 2560×1440 physical monitor at 125% (Electron DIP bounds 2048×1152),
// region {69,205,663,715} physical, 6 rows. Before the fix the overlay drew +69..+207 px too low
// because physical px were fed straight into the DIP window. layoutFullDisplayOverlay divides the
// 1.25× back out; converting the result back to physical must land exactly on each row.
test('full-display layout replays the real 125% capture with zero on-screen drift', () => {
  const scale = 1.25;
  const display = { x: 0, y: 0, width: 2048, height: 1152 };  // DIP; physical = 2560×1440
  const toDip = p => ({ x: p.x / scale, y: p.y / scale });    // stand-in for screen.screenToDipPoint
  const toScreen = p => ({ x: p.x * scale, y: p.y * scale });  // stand-in for screen.dipToScreenPoint
  const region = { x: 69, y: 205, w: 663, h: 715 };           // physical px from the sidecar
  const xOffset = 8;
  const rows = [71, 179, 292, 371, 508, 621].map(centerY => ({ centerY }));

  const { localLeft, localTops } = layoutFullDisplayOverlay({ region, xOffset, rows, display, toDip });

  rows.forEach((r, i) => {
    const drawnScreenY = toScreen({ x: display.x + localLeft, y: display.y + localTops[i] }).y;
    assert.ok(Math.abs(drawnScreenY - (region.y + r.centerY)) < 1e-6, `row ${i} drift ${drawnScreenY - (region.y + r.centerY)}`);
  });
  const drawnScreenX = toScreen({ x: display.x + localLeft, y: 0 }).x;
  assert.ok(Math.abs(drawnScreenX - (region.x + region.w + xOffset)) < 1e-6, 'column x drift');
});

// ---- regression: replay a real on-hardware capture, modelling Electron's actual rounding ----
// Fixture `nae-81-single-row-125pct.state.json` was pulled live from build 81 (2026-06-20) over the
// debug bridge: a single priced row ("3x Perfect Regal Orb", 58.5ex) on the 2560×1440@125% monitor.
// The case the earlier 6-row test does NOT cover: (1) a single row, (2) the real screen.* rounding.
// The existing 125% test divides by an ideal scale (`/1.25`, float64), but Electron's
// screen.screenToDipPoint returns a float32 and dipToScreenPoint rounds to an integer — that is why
// the device reported localTop=205.60000610351562 yet errorPx=0. We model both here and assert the
// layout reproduces the captured localLeft/localTop AND round-trips back to the exact physical row
// (errorPx 0) — proving the placement survives Electron's lossy DIP↔physical conversion, not just
// ideal arithmetic. See test/fixtures/nae-81-single-row-125pct.overlay.png for the matching screenshot.
test('full-display layout replays the live build-81 capture through Electron rounding (errorPx 0)', () => {
  const cap = JSON.parse(fs.readFileSync(
    path.join(__dirname, 'fixtures', 'nae-81-single-row-125pct.state.json'), 'utf8'));
  const scale = cap.display.scaleFactor;                       // 1.25
  const display = cap.display.bounds;                          // DIP {0,0,2048,1152}
  const region = cap.region;                                   // physical {69,205,663,715}
  const xOffset = cap.xOffset;                                 // 8

  // Model Electron on Windows: screenToDipPoint yields a float32; dipToScreenPoint rounds to int px.
  const toDip = p => ({ x: Math.fround(p.x / scale), y: Math.fround(p.y / scale) });
  const toScreen = p => ({ x: Math.round(p.x * scale), y: Math.round(p.y * scale) });

  const { localLeft, localTops } = layoutFullDisplayOverlay({ region, xOffset, rows: cap.rows, display, toDip });

  assert.equal(localLeft, cap.overlayLayout.localLeft, 'column left must match the captured value');
  cap.rows.forEach((row, i) => {
    assert.equal(localTops[i], row.localTop, `row ${i} localTop must match capture`);
    const drawnScreenY = toScreen({ x: display.x + localLeft, y: display.y + localTops[i] }).y;
    assert.equal(drawnScreenY, row.screenY, `row ${i} must draw on its physical row`);
    assert.equal(drawnScreenY - row.screenY, row.errorPx, `row ${i} errorPx must stay ${row.errorPx}`);
  });
});

test('full-display layout is the identity at 100% scaling (no regression on unscaled displays)', () => {
  const display = { x: 0, y: 0, width: 1920, height: 1080 };
  const toDip = p => p;   // scaleFactor 1.0
  const region = { x: 500, y: 400, w: 300, h: 400 };
  const rows = [{ centerY: 100 }, { centerY: 300 }];
  const { localLeft, localTops } = layoutFullDisplayOverlay({ region, xOffset: 8, rows, display, toDip });
  assert.equal(localLeft, 808);                 // region.x + region.w + xOffset
  assert.deepEqual(localTops, [500, 700]);      // region.y + centerY
});

// ---- renderer helpers ----

test('rowKind classifies by meme and currency', () => {
  assert.equal(rowKind({ meme: 'Mirror' }), 'mirror');
  assert.equal(rowKind({ meme: 'Headhunter' }), 'headhunter');
  assert.equal(rowKind({ meme: 'None', hasPrice: false }), 'warning');
  assert.equal(rowKind({ meme: 'None', hasPrice: true, divineValue: 1 }), 'divine');
  assert.equal(rowKind({ meme: 'None', hasPrice: true, divineValue: 0, exaltedValue: 2 }), 'exalted');
});

test('currencyIconSrc picks the right asset for divine/exalted/mirror/headhunter', () => {
  assert.equal(currencyIconSrc({ meme: 'Mirror' }), 'assets/mirror.png');
  assert.equal(currencyIconSrc({ meme: 'Headhunter' }), 'assets/headhunter.png');
  assert.equal(currencyIconSrc({ meme: 'None', divineValue: 1 }), 'assets/divine.png');
  assert.equal(currencyIconSrc({ meme: 'None', divineValue: 0, exaltedValue: 2 }), 'assets/exalted.png');
});

test('price strip width mirrors the WPF label estimate and clamps', () => {
  assert.equal(priceTextX({ meme: 'None' }), 38);
  assert.equal(priceTextX({ meme: 'Headhunter' }), 70);
  assert.equal(priceStripWidth({ meme: 'None', label: '3.2d' }), 104);
  assert.equal(priceStripWidth({ meme: 'None', label: '123456789012345678901234567890' }), 258);
});

test('markerText renders compact warning markers', () => {
  assert.equal(markerText('Loading'), '\u2026');
  assert.equal(markerText('NeedsGemLevel'), 'Lv?');
  assert.equal(markerText('Unknown'), 'n/a');
});
