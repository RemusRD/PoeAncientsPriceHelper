// Shared overlay geometry + renderer. The overlay window now covers the ENTIRE display that PoE 2
// is on, so positioning collapses to a single physical→DIP conversion done once in the main process
// (screen.screenToDipPoint). The sidecar emits physical screen pixels (it is Per-Monitor-V2 DPI
// aware); Electron's window/screen APIs are DIP. Converting at one boundary is what keeps the overlay
// aligned on scaled displays (e.g. 125%) — feeding physical px straight into setBounds drifts the
// overlay by the display's scale factor, worse the further from the origin.
//
// This module only turns already-DIP screen coordinates into window-local positions and draws rows.
// The pure helper (placeOverlayRows) is unit-tested directly.

const OVERLAY_WIDTH = 260;
const OVERLAY_MARGIN = 8;

// Place price strips inside the full-display overlay window. Every input is DIP (the main process
// converts physical→DIP before calling):
//   columnLeftDip — screen-space DIP x of the price column (region right edge + xOffset)
//   rowTopsDip    — screen-space DIP y of each row center
//   display       — the overlay window bounds {x,y,width,height} in DIP (the whole monitor)
// Returns window-local positions. Every strip shares one x (a tidy column); only y varies per row.
// If the column would overflow the display's right edge, it is pulled left just like the old WPF
// overlay clamped its window.
function placeOverlayRows({ columnLeftDip, rowTopsDip, display, widestStrip = OVERLAY_WIDTH, margin = OVERLAY_MARGIN }) {
  let localLeft = columnLeftDip - display.x;
  const maxLeft = display.width - widestStrip - margin;
  if (localLeft > maxLeft) localLeft = maxLeft;
  if (localLeft < margin) localLeft = margin;
  return { localLeft, localTops: (rowTopsDip || []).map(y => y - display.y) };
}

// Full pipeline, testable without Electron: given the sidecar's PHYSICAL region/rows, a physical→DIP
// converter (main.js passes screen.screenToDipPoint), and the target display (DIP bounds), produce the
// window-local DIP layout. This is the one place the 1.25× scale gets divided back out.
function layoutFullDisplayOverlay({ region, xOffset = 0, rows, display, toDip }) {
  const colX = region.x + region.w + xOffset;
  const columnLeftDip = toDip({ x: colX, y: region.y }).x;
  const rowTopsDip = (rows || []).map(r => toDip({ x: colX, y: region.y + r.centerY }).y);
  return placeOverlayRows({ columnLeftDip, rowTopsDip, display });
}

function priceStripWidth(row) {
  const label = row.label || '';
  const textX = row.meme === 'Headhunter' ? 70 : 38;
  return Math.min(258, Math.max(104, textX + label.length * 11.5 + 10));
}

function priceTextX(row) {
  return row.meme === 'Headhunter' ? 70 : 38;
}

// ---- renderer ----

const root = typeof document !== 'undefined' ? document.getElementById('rows') : null;
const buildBadge = typeof document !== 'undefined' ? document.getElementById('build') : null;

function currencyIconSrc(row) {
  if (row.meme === 'Mirror') return 'assets/mirror.png';
  if (row.meme === 'Headhunter') return 'assets/headhunter.png';
  return row.divineValue >= 1 ? 'assets/divine.png' : 'assets/exalted.png';
}

function rowKind(row) {
  if (row.meme === 'Mirror') return 'mirror';
  if (row.meme === 'Headhunter') return 'headhunter';
  if (!row.hasPrice) return 'warning';
  return row.divineValue >= 1 ? 'divine' : 'exalted';
}

function markerText(reason) {
  if (reason === 'Loading') return '…';
  if (reason === 'NeedsGemLevel') return 'Lv?';
  return 'n/a';
}

function renderRows(data) {
  if (!root) return;
  root.replaceChildren();
  renderBuild(data.build);
  const left = data.localLeft || 0;
  if (data.debugLayout) renderDebug(data.debug, data.rows || [], left);
  for (const r of (data.rows || [])) {
    const top = r.localTop || 0;
    root.appendChild(r.hasPrice ? buildPriceRow(r, top, left) : buildWarningRow(r, top, left));
  }
}

function renderBuild(build) {
  if (!buildBadge) return;
  buildBadge.textContent = build || '';
}

// Debug overlay: one green band per detected rune row (spanning the capture region) plus one yellow
// line per priced row. All geometry is window-local DIP, precomputed by the main process.
function renderDebug(debug, rows, left) {
  if (!debug) return;
  const region = debug.region || { left: 0, top: 0, width: 0, height: 0 };
  for (const band of (debug.bands || [])) {
    appendDebugBand({
      label: `d${band.index}`,
      left: region.left,
      top: band.top,
      width: region.width,
      height: band.height,
      textLocalY: band.textTop,
    });
  }
  rows.forEach((r, i) => appendDebugLine('price', r.localTop || 0, `p${i + 1}`, left, 180));
}

function appendDebugLine(kind, localY, labelText, left = 0, width = 0) {
  const line = document.createElement('div');
  line.className = 'debug-line ' + kind;
  line.style.top = localY + 'px';
  line.style.left = left + 'px';
  if (width > 0) {
    line.style.right = 'auto';
    line.style.width = width + 'px';
  }
  const label = document.createElement('div');
  label.className = 'debug-label';
  label.style.top = localY + 'px';
  label.style.left = left + 2 + 'px';
  label.textContent = labelText;
  root.append(line, label);
}

function appendDebugBand(visual) {
  const band = document.createElement('div');
  band.className = 'debug-band';
  band.style.left = visual.left + 'px';
  band.style.top = visual.top + 'px';
  band.style.width = visual.width + 'px';
  band.style.height = visual.height + 'px';

  const tick = document.createElement('div');
  tick.className = 'debug-text-tick';
  tick.style.left = visual.left + 2 + 'px';
  tick.style.top = visual.textLocalY + 'px';

  const label = document.createElement('div');
  label.className = 'debug-band-label';
  label.style.left = visual.left + 2 + 'px';
  label.style.top = visual.top + 2 + 'px';
  label.textContent = visual.label;

  root.append(band, tick, label);
}

function buildPriceRow(row, top, left = 0) {
  const wrap = document.createElement('div');
  wrap.className = 'row';
  wrap.style.top = top + 'px';
  wrap.style.left = left + 'px';

  const strip = document.createElement('div');
  strip.className = 'strip price ' + rowKind(row);
  strip.style.width = priceStripWidth(row) + 'px';

  // Icon is a CSS background (not an <img>): a fresh <img> element re-enters the async decode
  // pipeline on every rebuild and pops in a frame late (a visible flash); a background-image keyed by
  // a stable class paints synchronously from cache. currencyIconSrc stays the source of truth — the
  // icon-<kind> class maps 1:1 to the same asset (see overlay.html).
  const icon = document.createElement('div');
  icon.className = 'strip-icon icon-' + rowKind(row) + (row.meme === 'Headhunter' ? ' wide' : '');

  const label = document.createElement('span');
  label.className = 'strip-label ' + rowKind(row) + (row.divineValue >= 1 ? ' glow' : '');
  label.style.left = priceTextX(row) + 'px';
  label.textContent = row.label;

  strip.append(icon, label);
  wrap.appendChild(strip);
  return wrap;
}

function buildWarningRow(row, top, left = 0) {
  const wrap = document.createElement('div');
  wrap.className = 'row';
  wrap.style.top = top + 'px';
  wrap.style.left = left + 'px';

  const strip = document.createElement('div');
  strip.className = 'strip warning' + (row.reason === 'Loading' ? ' loading' : '');
  strip.textContent = markerText(row.reason);

  wrap.appendChild(strip);
  return wrap;
}

if (typeof window !== 'undefined' && window.overlay) {
  window.overlay.onRows((_e, data) => renderRows(data));
  window.overlay.onBuild((_e, build) => renderBuild(build));
  window.overlay.onHide(() => { if (root) root.replaceChildren(); });
}

if (typeof module !== 'undefined') {
  module.exports = { placeOverlayRows, layoutFullDisplayOverlay, priceStripWidth, priceTextX, rowKind, currencyIconSrc, markerText, OVERLAY_WIDTH, OVERLAY_MARGIN };
}
