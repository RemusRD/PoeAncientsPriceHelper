// Not Alone, Exile — Electron main process.
// Owns: single-instance guard, control-panel window, transparent overlay window, tray,
// auto-updater, and the C# sidecar lifecycle. The sidecar does all capture/OCR/pricing and
// streams overlay events; this process only renders them.
const path = require('path');
const fs = require('fs');
const { app, BrowserWindow, Tray, Menu, ipcMain, Notification, nativeImage, screen, desktopCapturer, globalShortcut } = require('electron');
const { layoutFullDisplayOverlay } = require('./overlay');
const { overlayRegionKey, overlayRowsKey } = require('./overlay-state');
const { startSidecar } = require('./sidecar-client');

// Keep the GPU ON: transparent overlay windows composite via DWM + Chromium and can render solid
// black when HW acceleration is disabled (that was hiding the overlay). We only relax the sandbox —
// the renderer sandbox is what makes a bridge-launched Electron exit in ~1s. no-sandbox is fine for
// a trusted local app.
app.commandLine.appendSwitch('no-sandbox');
app.commandLine.appendSwitch('disable-gpu-sandbox');

// Boot diagnostics: the bridge can't capture Electron's stderr, so write milestones + uncaught
// errors to userData/nae-boot.log (fetchable over SFTP) until the launch crash is nailed down.
const BOOT_LOGS = [
  path.join(app.getPath('userData'), 'nae-boot.log'),
  path.join(app.isPackaged ? process.resourcesPath : __dirname, 'sidecar', 'overlay_shell_log.txt'),
];
function bootLog(msg) {
  for (const logPath of BOOT_LOGS) {
    try { fs.mkdirSync(path.dirname(logPath), { recursive: true }); fs.appendFileSync(logPath, `[${new Date().toISOString()}] ${msg}\n`); } catch {}
  }
}
process.on('uncaughtException', e => bootLog('UNCAUGHT ' + (e && e.stack || e)));
process.on('unhandledRejection', e => bootLog('UNHANDLED ' + (e && e.stack || e)));
bootLog('=== main loaded; packaged=' + app.isPackaged + ' argv=' + JSON.stringify(process.argv));

// Auto-update only matters in a packaged build; in dev electron-updater has no app-update.yml.
if (app.isPackaged) {
  try { const { autoUpdater } = require('electron-updater'); autoUpdater.autoDownload = true; module.exports.__autoUpdater = autoUpdater; } catch { /* optional */ }
}

const SIDECAR_EXE = app.isPackaged
  ? path.join(process.resourcesPath, 'sidecar', 'runeshape-sidecar.exe')
  : null;
const RUNTIME_DIR = path.join(app.isPackaged ? process.resourcesPath : __dirname, 'sidecar');
const BUG_CAPTURE_HOTKEY = 'PageDown';

let panel = null;
let overlay = null;
let tray = null;
let sidecar = null;
let quitting = false;
let balloonShown = false;
let overlayReady = false;
let lastOverlayRows = null;
let lastDebugRows = null;
let lastOverlayRegionKey = null;
let lastOverlayStateKey = null;
let debugLayoutEnabled = false;
let currentRegion = null;       // {x,y,w,h} capture region from the sidecar, in PHYSICAL screen px
let currentXOffset = 0;         // px the overlay is nudged right of the region's right edge (physical)
let currentDisplayId = null;    // id of the display the overlay window currently covers
let lastLayout = { localLeft: 0, localTops: [], displayBounds: null };  // window-local DIP cache
let sidecarBuild = 'starting';
let bugCaptureInFlight = false;

// Single instance: a second launch just focuses the already-running panel.
if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on('second-instance', () => { if (panel) { panel.show(); panel.focus(); } });
  app.whenReady().then(boot);
}

async function boot() {
  Menu.setApplicationMenu(null);   // drop the default File/Edit/View menu bar
  bootLog('boot start');
  createTray();
  createPanel();
  bootLog('panel created');
  createOverlay();
  bootLog('overlay created');
  startOverlayDebugWatcher();
  startEngine();
  registerBugCaptureShortcut();

  if (module.exports.__autoUpdater) {
    const u = module.exports.__autoUpdater;
    u.on('update-downloaded', () => {
      new Notification({ title: 'Not Alone, Exile', body: 'An update is ready — restart to apply.' }).show();
    });
    u.checkForUpdatesAndNotify().catch(() => { /* offline / no release yet */ });
  }
}

function createPanel() {
  panel = new BrowserWindow({
    width: 380, height: 200,
    title: 'Not Alone, Exile',
    backgroundColor: '#0d0f12',
    resizable: false,
    maximizable: false,
    webPreferences: { preload: path.join(__dirname, 'preload.js'), contextIsolation: true, nodeIntegration: false },
  });
  panel.loadFile('index.html');
  // Close hides to tray rather than quitting — the overlay keeps running in the background.
  panel.on('close', e => { if (!quitting) { e.preventDefault(); panel.hide(); showTrayBalloon(); } });
}

function createOverlay() {
  overlay = new BrowserWindow({
    x: 0, y: 0, width: 260, height: 600,
    frame: false,
    transparent: true,
    alwaysOnTop: true,
    focusable: false,
    skipTaskbar: true,
    resizable: false,
    movable: false,
    minimizable: false,
    maximizable: false,
    show: false,
    webPreferences: { preload: path.join(__dirname, 'overlay-preload.js'), contextIsolation: true, nodeIntegration: false },
  });
  overlay.setAlwaysOnTop(true, 'screen-saver');
  overlay.setIgnoreMouseEvents(true);   // fully click-through so PoE 2 receives every click
  overlay.webContents.once('did-finish-load', () => { overlayReady = true; flushOverlayRows(); });
  overlay.loadFile('overlay.html');
}

let lastTopmostAt = 0;
const TOPMOST_REASSERT_MS = 1500;   // steady-state keepalive cadence (see reassertOverlayTop)

function forceOverlayTop() {
  if (!overlay) return;
  overlay.setAlwaysOnTop(true, 'screen-saver');
  try { overlay.moveTop(); } catch { /* best effort; not supported on every platform */ }
  lastTopmostAt = Date.now();
}

// The sidecar re-asserts topmost on every gate tick (~5x/sec) while a panel is stable or being
// hovered. Re-stacking a transparent always-on-top window that often produces a visible flicker and
// aggressively steals z-order from other windows. Collapse that steady-state keepalive to at most one
// re-assert per TOPMOST_REASSERT_MS. Reactive raises that must be immediate — the window first
// becoming visible (overlayShow) and a real row update (flushOverlayRows) — still call
// forceOverlayTop() directly; this throttle only governs the standalone overlayTopmost keepalive.
function reassertOverlayTop() {
  if (Date.now() - lastTopmostAt < TOPMOST_REASSERT_MS) return;
  forceOverlayTop();
}

// The sidecar is Per-Monitor-V2 DPI aware, so every coordinate it sends (region, row centerY) is in
// PHYSICAL screen pixels; Electron's window/screen APIs are DIP. Convert once, here, at that seam —
// this is the whole reason the overlay drifted on a scaled (e.g. 125%) display: physical px fed into
// setBounds get re-scaled by the display factor, worse the further from the origin. screenToDipPoint /
// dipToScreenPoint are Windows-only; off-Windows (mac dev, no real overlay) fall back to identity.
// These MUST stay lazy: the `screen` module throws if touched before the app 'ready' event, so we only
// reach into it when called (always post-ready, from overlay event handlers).
function screenToDip(p) { return typeof screen.screenToDipPoint === 'function' ? screen.screenToDipPoint(p) : p; }
function dipToScreen(p) { return typeof screen.dipToScreenPoint === 'function' ? screen.dipToScreenPoint(p) : p; }

function currentDisplay() {
  if (!currentRegion) return screen.getPrimaryDisplay();
  return screen.getDisplayNearestPoint(screenToDip({ x: currentRegion.x, y: currentRegion.y }));
}

// Position the overlay as a full-display, click-through layer and compute each row's window-local DIP
// position. The window only moves when PoE 2 hops to another monitor (rare); per update we just
// convert a handful of points. setBounds is DIP, so display.bounds drops straight in.
function applyOverlayLayout() {
  if (!overlay || !currentRegion) return;
  const display = currentDisplay();
  if (display.id !== currentDisplayId) {
    overlay.setBounds(display.bounds);
    currentDisplayId = display.id;
  }
  const placed = layoutFullDisplayOverlay({
    region: currentRegion, xOffset: currentXOffset, rows: lastOverlayRows || [],
    display: display.bounds, toDip: screenToDip,
  });
  lastLayout = { localLeft: placed.localLeft, localTops: placed.localTops, displayBounds: display.bounds };
}

// Debug overlay geometry (green rune bands + region box), also converted to window-local DIP.
function buildDebugPayload() {
  if (!currentRegion || !lastLayout.displayBounds) return null;
  const d = lastLayout.displayBounds;
  const tl = screenToDip({ x: currentRegion.x, y: currentRegion.y });
  const br = screenToDip({ x: currentRegion.x + currentRegion.w, y: currentRegion.y + currentRegion.h });
  const bands = (lastDebugRows || []).map((r, i) => {
    const top = screenToDip({ x: currentRegion.x, y: currentRegion.y + r.top }).y - d.y;
    const bottom = screenToDip({ x: currentRegion.x, y: currentRegion.y + r.bottom }).y - d.y;
    const textTop = screenToDip({ x: currentRegion.x, y: currentRegion.y + r.textCenterY }).y - d.y;
    return { index: i + 1, top, height: Math.max(1, bottom - top), textTop };
  });
  return { region: { left: tl.x - d.x, top: tl.y - d.y, width: br.x - tl.x, height: br.y - tl.y }, bands };
}

// Rows carry their window-local DIP center (localTop); the renderer just drops each strip there.
function flushOverlayRows() {
  if (!overlay || !overlayReady || !lastOverlayRows) return;
  const rows = lastOverlayRows.map((r, i) => ({ ...r, localTop: lastLayout.localTops[i] ?? 0 }));
  overlay.webContents.send('overlay-rows', {
    rows,
    localLeft: lastLayout.localLeft,
    debugLayout: debugLayoutEnabled,
    debug: debugLayoutEnabled ? buildDebugPayload() : null,
    build: sidecarBuild,
  });
  forceOverlayTop();
}

function startOverlayDebugWatcher() {
  const requestPath = path.join(RUNTIME_DIR, 'overlay-debug-request.json');
  setInterval(() => {
    if (!fs.existsSync(requestPath)) return;
    let request = {};
    try {
      request = JSON.parse(fs.readFileSync(requestPath, 'utf8'));
    } catch (e) {
      bootLog('overlay debug request parse failed: ' + (e && e.message));
    }
    try { fs.unlinkSync(requestPath); } catch {}
    captureOverlayDebug(request).catch(e => bootLog('overlay debug capture failed: ' + (e && e.stack || e)));
  }, 500);
}

async function captureOverlayDebug(request = {}) {
  const root = path.join(RUNTIME_DIR, 'overlay-debug');
  fs.mkdirSync(root, { recursive: true });
  const id = safeFilePart(request.id || new Date().toISOString().replace(/[:.]/g, '-'));
  const folder = path.join(root, id);
  fs.mkdirSync(folder, { recursive: true });

  const display = currentDisplay();
  const overlayBounds = overlay ? overlay.getBounds() : null;
  // Report the TRUE rendered position: convert each row's window-local DIP back to physical screen px
  // (dipToScreen) and compare to where the row SHOULD be (region.y + centerY, physical). errorPx is the
  // real on-screen drift and should be ~0 once the physical↔DIP conversion is correct (it was +69..+207
  // px at 125% before the fix).
  const rows = (lastOverlayRows || []).map((row, i) => {
    const screenY = currentRegion ? currentRegion.y + row.centerY : null;
    const localTop = lastLayout.localTops[i];
    const drawnScreenY = overlayBounds && localTop != null
      ? Math.round(dipToScreen({ x: overlayBounds.x, y: overlayBounds.y + localTop }).y)
      : null;
    return {
      ...row,
      screenY,
      localTop: localTop ?? null,
      drawnScreenY,
      errorPx: screenY != null && drawnScreenY != null ? drawnScreenY - screenY : null,
    };
  });

  const state = {
    capturedAt: new Date().toISOString(),
    reason: request.reason || 'manual',
    display: { id: display.id, scaleFactor: display.scaleFactor, bounds: display.bounds, workArea: display.workArea },
    region: currentRegion,
    xOffset: currentXOffset,
    overlayReady,
    overlayVisible: overlay ? overlay.isVisible() : false,
    overlayBounds,
    rows,
    debugRows: lastDebugRows || [],
    debugLayout: debugLayoutEnabled,
    overlayLayout: { localLeft: lastLayout.localLeft },
    build: sidecarBuild,
    hotkey: request.hotkey || null,
    sidecarDiagnosticRequested: !!request.sidecarDiagnosticRequested,
  };

  fs.writeFileSync(path.join(folder, 'state.json'), JSON.stringify(state, null, 2));
  await captureMonitor(display, path.join(folder, 'monitor.png'));
  await captureOverlayWindow(path.join(folder, 'overlay.png'));
  writeReplay(folder, state);
  fs.writeFileSync(path.join(root, 'latest.json'), JSON.stringify({
    id,
    folderName: id,
    files: ['state.json', 'monitor.png', 'overlay.png', 'replay.html'],
    capturedAt: state.capturedAt,
  }, null, 2));
  bootLog('overlay debug captured folder=' + folder + ' rows=' + rows.length + ' overlay=' + JSON.stringify(overlayBounds));
  return { id, folder };
}

function registerBugCaptureShortcut() {
  const ok = globalShortcut.register(BUG_CAPTURE_HOTKEY, () => {
    captureBugReport('hotkey').catch(e => bootLog('bug capture failed: ' + (e && e.stack || e)));
  });
  if (ok) {
    bootLog('bug capture hotkey registered ' + BUG_CAPTURE_HOTKEY);
  } else {
    bootLog('bug capture hotkey registration failed ' + BUG_CAPTURE_HOTKEY);
    if (panel) panel.webContents.send('status', { text: `Bug capture hotkey unavailable: ${BUG_CAPTURE_HOTKEY}`, error: true });
  }
}

async function captureBugReport(reason) {
  if (bugCaptureInFlight) {
    bootLog('bug capture skipped; previous capture still running');
    return;
  }
  bugCaptureInFlight = true;
  const id = 'bug-' + new Date().toISOString().replace(/[:.]/g, '-');
  try {
    if (panel) panel.webContents.send('status', { text: 'Capturing bug screenshot...' });
    const overlayCapture = await captureOverlayDebug({
      id,
      reason,
      hotkey: BUG_CAPTURE_HOTKEY,
      sidecarDiagnosticRequested: !!sidecar,
    });
    if (sidecar) sidecar.collectDiagnostics({ id, reason, hotkey: BUG_CAPTURE_HOTKEY });
    bootLog('bug capture saved id=' + id + ' overlayFolder=' + overlayCapture.folder);
    if (panel) panel.webContents.send('bugCapture', { id, folder: overlayCapture.folder, hotkey: BUG_CAPTURE_HOTKEY });
    if (Notification.isSupported()) {
      new Notification({
        title: 'Not Alone, Exile',
        body: `Bug capture saved: ${id}`,
      }).show();
    }
  } catch (e) {
    bootLog('bug capture failed id=' + id + ' ' + (e && e.stack || e));
    if (panel) panel.webContents.send('status', { text: 'Bug capture failed.', error: true });
  } finally {
    bugCaptureInFlight = false;
  }
}

async function captureMonitor(display, outputPath) {
  const sources = await desktopCapturer.getSources({
    types: ['screen'],
    thumbnailSize: {
      width: Math.max(1, Math.round(display.bounds.width)),
      height: Math.max(1, Math.round(display.bounds.height)),
    },
  });
  const source = sources.find(s => String(s.display_id) === String(display.id)) || sources[0];
  if (!source || source.thumbnail.isEmpty()) return;
  fs.writeFileSync(outputPath, source.thumbnail.toPNG());
}

async function captureOverlayWindow(outputPath) {
  if (!overlay) return;
  const image = await overlay.capturePage();
  fs.writeFileSync(outputPath, image.toPNG());
}

function writeReplay(folder, state) {
  const displayLeft = state.display.bounds.x;
  const displayTop = state.display.bounds.y;
  const overlayBounds = state.overlayBounds;
  const overlayStyle = overlayBounds
    ? `left:${overlayBounds.x - displayLeft}px;top:${overlayBounds.y - displayTop}px;width:${overlayBounds.width}px;height:${overlayBounds.height}px;`
    : 'display:none;';
  fs.writeFileSync(path.join(folder, 'replay.html'), `<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<style>
body { margin: 0; background: #111; color: #ddd; font: 12px Segoe UI, sans-serif; }
#stage { position: relative; width: ${state.display.bounds.width}px; height: ${state.display.bounds.height}px; }
#monitor { position: absolute; left: 0; top: 0; width: 100%; height: 100%; }
#overlay { position: absolute; ${overlayStyle} outline: 1px solid rgba(255,196,90,.9); }
.row-line { position: absolute; left: 0; right: 0; height: 1px; background: rgba(0,255,255,.85); }
.label { position: absolute; left: 4px; transform: translateY(-50%); color: cyan; text-shadow: 0 1px 2px #000; }
#meta { padding: 8px; white-space: pre-wrap; }
</style>
</head>
<body>
<div id="stage">
  <img id="monitor" src="monitor.png">
  <img id="overlay" src="overlay.png">
  ${state.rows.map((row, i) => {
    const y = (row.localTop == null || !overlayBounds) ? -1000 : overlayBounds.y + row.localTop - displayTop;
    return `<div class="row-line" style="top:${y}px"></div><div class="label" style="top:${y}px">#${i + 1} ${escapeHtml(row.label || row.reason || '')}</div>`;
  }).join('\n  ')}
</div>
<div id="meta">${escapeHtml(JSON.stringify(state, null, 2))}</div>
</body>
</html>`);
}

function safeFilePart(value) {
  return String(value).replace(/[^a-zA-Z0-9_.-]/g, '-').slice(0, 80) || 'capture';
}

function escapeHtml(value) {
  return String(value)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

function startEngine() {
  sidecar = startSidecar({ exePath: SIDECAR_EXE });
  bootLog('startEngine: exePath=' + SIDECAR_EXE);

  sidecar.on('ready', msg => {
    sidecarBuild = msg.build || sidecarBuild;
    bootLog('sidecar ready build=' + sidecarBuild);
    if (overlay && overlayReady) overlay.webContents.send('overlay-build', sidecarBuild);
  });
  sidecar.on('config', msg => {
    debugLayoutEnabled = !!msg.debugLayoutEnabled;
    bootLog('sidecar config league=' + msg.league + ' debugLayout=' + debugLayoutEnabled);
    if (panel) panel.webContents.send('config', msg);
  });
  sidecar.on('status', msg => { bootLog('sidecar status: ' + msg.text); if (panel) panel.webContents.send('status', msg); });
  sidecar.on('diagnosticBundle', msg => {
    bootLog('sidecar diagnostic bundle captureId=' + (msg.captureId || '') + ' zip=' + (msg.zipPath || ''));
    if (panel) panel.webContents.send('status', { text: `Bug diagnostics saved: ${msg.captureId || path.basename(msg.zipPath || '')}` });
  });

  sidecar.on('overlayShow', msg => {
    if (!overlay) return;
    const nextRegionKey = overlayRegionKey(msg.region, msg.xOffset || 0);
    currentRegion = msg.region;
    currentXOffset = msg.xOffset || 0;
    const regionChanged = nextRegionKey !== lastOverlayRegionKey;
    lastOverlayRegionKey = nextRegionKey;
    bootLog('overlayShow region=' + JSON.stringify(currentRegion) + ' xOffset=' + currentXOffset);
    if (!regionChanged && overlay.isVisible()) return;
    applyOverlayLayout();
    if (!overlay.isVisible()) overlay.showInactive();
    forceOverlayTop();
    bootLog('overlay visible=' + overlay.isVisible() + ' bounds=' + JSON.stringify(overlay.getBounds()));
    flushOverlayRows();
  });
  sidecar.on('overlayRows', msg => {
    const rows = msg.rows || [];
    lastDebugRows = msg.debugRows || [];
    debugLayoutEnabled = !!msg.debugLayout;
    if (msg.build) sidecarBuild = msg.build;
    const stateKey = overlayRowsKey(msg);
    if (stateKey === lastOverlayStateKey) return;
    lastOverlayStateKey = stateKey;
    const n = rows.length;
    const sample = rows.slice(0, 3).map(r => ({ y: r.centerY, label: r.label || r.reason }));
    bootLog('overlayRows n=' + n + ' ready=' + overlayReady + ' visible=' + (overlay ? overlay.isVisible() : false) + ' sample=' + JSON.stringify(sample));
    lastOverlayRows = rows;
    applyOverlayLayout();
    flushOverlayRows();
  });
  sidecar.on('overlayHide', () => {
    bootLog('overlayHide');
    lastOverlayRows = null;
    lastDebugRows = null;
    lastOverlayRegionKey = null;
    lastOverlayStateKey = null;
    if (overlay && overlayReady) overlay.webContents.send('overlay-hide');
    if (overlay && overlay.isVisible()) overlay.hide();
  });
  sidecar.on('overlayTopmost', () => reassertOverlayTop());

  sidecar.on('error', err => {
    bootLog('sidecar error ' + (err && err.message));
    if (panel) panel.webContents.send('status', { text: 'Engine offline.', error: true });
  });
  sidecar.on('exit', code => {
    bootLog('sidecar exit code=' + code);
    if (!quitting && panel) panel.webContents.send('status', { text: 'Engine stopped.', error: true });
  });
}

function createTray() {
  const iconPath = path.join(__dirname, 'build', 'icon.png');
  const image = fs.existsSync(iconPath) ? nativeImage.createFromPath(iconPath) : nativeImage.createEmpty();
  tray = new Tray(image.isEmpty() ? nativeImage.createFromBuffer(placeholderIcon()) : image);
  tray.setToolTip('Not Alone, Exile');
  tray.setContextMenu(Menu.buildFromTemplate([
    { label: 'Show', click: () => { panel.show(); panel.focus(); } },
    { type: 'separator' },
    { label: 'Quit', click: () => quit() },
  ]));
  tray.on('double-click', () => { panel.show(); panel.focus(); });
}

function showTrayBalloon() {
  if (balloonShown) return;
  balloonShown = true;
  tray.displayBalloon({ title: 'Not Alone, Exile', content: 'Still running. Double-click the tray icon to restore.' });
}

function quit() {
  quitting = true;
  if (sidecar) { try { sidecar.shutdown(); } catch { /* */ } sidecar.kill(); }
  tray?.destroy();
  app.quit();
}

// Prevent the app from quitting when the panel is hidden to tray; only quit via tray.
app.on('window-all-closed', () => { /* keep running in the background */ });
app.on('before-quit', () => { quitting = true; if (sidecar) sidecar.kill(); });
app.on('will-quit', () => { globalShortcut.unregisterAll(); });

// ---- control-panel IPC ----
ipcMain.handle('version', () => {
  const pkg = JSON.parse(fs.readFileSync(path.join(__dirname, 'package.json'), 'utf8'));
  return `v${pkg.version}${app.isPackaged ? '' : ' (dev)'}`;
});
ipcMain.on('setLeague', (_e, league) => { sidecar && sidecar.setLeague(league); });
ipcMain.on('setPriceCheckCorpus', (_e, enabled) => { sidecar && sidecar.setPriceCheckCorpus(!!enabled); });
ipcMain.on('setDebugLayout', (_e, enabled) => { sidecar && sidecar.setDebugLayout(!!enabled); });

// 16x16 dark PNG so the tray is never invisible even before a real icon is shipped.
function placeholderIcon() {
  return Buffer.from('iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAfElEQVR4AcXOMQqDQBCF4d/S9N9iNlYWlkLwV0KSlkIIIYQQQgghhBBCCCGEEEIIIYQQQgghhBBCCCGEEEIIIYQQQgghhBBCCCGEEEIIIYQQQgghhBBCCCGEEEIIIYQQ4j/0D1nBH4oVk0N6AAAAAElFTkSuQmCC', 'base64');
}
