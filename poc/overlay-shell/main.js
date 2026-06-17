// Electron main entry. Two modes:
//   node main.js --probe <fixture.png>   headless: spawn sidecar, one detect, print JSON, exit
//   electron .                            GUI: overlay window, pick a fixture, render detected rows
const path = require('path');
const fs = require('fs');
const { startSidecar } = require('./sidecar-client');

const probeIdx = process.argv.indexOf('--probe');
if (probeIdx >= 0) {
  const fixture = process.argv[probeIdx + 1];
  if (!fixture) { console.error('usage: node main.js --probe <fixture.png>'); process.exit(2); }
  runProbe(fixture);
} else {
  startGui();
}

async function runProbe(fixture) {
  const client = startSidecar();
  try {
    await client.ready;
    const res = await client.detect(path.resolve(fixture));
    process.stdout.write(JSON.stringify(res) + '\n');
  } catch (e) {
    console.error('probe failed:', e.message);
    process.exit(1);
  } finally {
    client.kill();
  }
  process.exit(0);
}

function startGui() {
  // Lazy-require so the headless --probe path never touches Electron.
  const { app, BrowserWindow, ipcMain, dialog } = require('electron');
  let client = null;
  let win = null;

  async function ensureClient() {
    if (!client) {
      // Packaged Windows build: spawn the sidecar .exe bundled in resources/sidecar.
      // Dev: spawn via `dotnet run` (exePath = null).
      const exePath = app.isPackaged
        ? path.join(process.resourcesPath, 'sidecar', 'runeshape-sidecar.exe')
        : null;
      client = startSidecar({ exePath });
      await client.ready;
    }
    return client;
  }

  app.whenReady().then(() => {
    win = new BrowserWindow({
      width: 940,
      height: 820,
      title: 'Runeshape overlay shell (POC)',
      backgroundColor: '#1e1e1e',
      webPreferences: {
        preload: path.join(__dirname, 'preload.js'),
        contextIsolation: true,
        nodeIntegration: false,
      },
    });
    win.loadFile('index.html');
  });

  ipcMain.handle('pick-fixture', async () => {
    const r = await dialog.showOpenDialog(win, {
      title: 'Pick a runeshape capture',
      filters: [{ name: 'PNG', extensions: ['png'] }],
      properties: ['openFile'],
    });
    if (r.canceled || !r.filePaths.length) return null;
    const p = r.filePaths[0];
    const c = await ensureClient();
    const detection = await c.detect(p);
    const dataUrl = 'data:image/png;base64,' + fs.readFileSync(p).toString('base64');
    return { imagePath: p, dataUrl, detection };
  });

  app.on('window-all-closed', () => {
    if (client) client.kill();
    app.quit();
  });
}
