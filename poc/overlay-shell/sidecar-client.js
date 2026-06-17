// Pure-Node client for the C# sidecar. No Electron dependency, so it can be used from both
// the headless `--probe` mode (plain node) and the GUI mode (Electron main process).
//
// The sidecar is a long-running stdio NDJSON process: it emits {"event":"ready"} on start,
// then for each {"path":...} request line on stdin it writes one JSON response line on stdout.
const { spawn } = require('child_process');
const path = require('path');

const DOTNET = process.env.DOTNET_BIN || 'dotnet';
const SIDECAR_PROJ = path.resolve(__dirname, '../../src/PoeAncientsSidecar/PoeAncientsSidecar.csproj');

// In packaged (Windows) mode pass { exePath } to spawn the bundled sidecar binary directly,
// so the target machine needs no .NET SDK and no Node install. In dev, omit it and we
// `dotnet run` the sidecar project instead.
function startSidecar({ exePath } = {}) {
  const child = exePath
    ? spawn(exePath, [], { stdio: ['pipe', 'pipe', 'inherit'], windowsHide: true })
    : spawn(DOTNET, ['run', '--project', SIDECAR_PROJ, '--nologo'], { stdio: ['pipe', 'pipe', 'inherit'] });

  let buf = '';
  const readyResolvers = [];
  let pending = null; // { resolve, reject } — one request in flight at a time (POC contract)

  function handleLine(line) {
    line = line.trim();
    if (!line) return;
    let msg;
    try { msg = JSON.parse(line); } catch { return; }
    if (msg.event === 'ready') {
      readyResolvers.forEach(r => r());
      readyResolvers.length = 0;
    } else if (msg.event === 'error' && pending) {
      const p = pending; pending = null;
      p.reject(new Error(msg.message));
    } else if (pending) {
      const p = pending; pending = null;
      p.resolve(msg);
    }
  }

  child.stdout.setEncoding('utf8');
  child.stdout.on('data', chunk => {
    buf += chunk;
    let i;
    while ((i = buf.indexOf('\n')) >= 0) {
      handleLine(buf.slice(0, i));
      buf = buf.slice(i + 1);
    }
  });
  child.on('exit', code => {
    if (pending) { pending.reject(new Error('sidecar exited code=' + code)); pending = null; }
  });
  child.on('error', err => {
    if (pending) { pending.reject(err); pending = null; }
  });

  const ready = new Promise(res => readyResolvers.push(res));
  return {
    ready,
    detect(filePath) {
      if (pending) return Promise.reject(new Error('a request is already in flight'));
      return new Promise((resolve, reject) => {
        pending = { resolve, reject };
        child.stdin.write(JSON.stringify({ path: filePath }) + '\n');
      });
    },
    kill() { try { child.kill(); } catch { /* best effort */ } },
  };
}

module.exports = { startSidecar };
