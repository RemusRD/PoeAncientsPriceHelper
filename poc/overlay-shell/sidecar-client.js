// Event-stream client for the C# sidecar. The sidecar auto-starts the watch loop on launch and
// streams newline-delimited JSON events on stdout; the only command we send back is setLeague /
// shutdown. No request/response correlation — every line is a self-describing event.
const { spawn } = require('child_process');
const path = require('path');
const { EventEmitter } = require('events');

const DOTNET = process.env.DOTNET_BIN || 'dotnet';
const SIDECAR_PROJ = path.resolve(__dirname, '../../src/PoeAncientsSidecar/PoeAncientsSidecar.csproj');

function startSidecar({ exePath } = {}) {
  const child = exePath
    ? spawn(exePath, [], { stdio: ['pipe', 'pipe', 'inherit'], windowsHide: true })
    : spawn(DOTNET, ['run', '--project', SIDECAR_PROJ, '--nologo'], { stdio: ['pipe', 'pipe', 'inherit'] });

  const bus = new EventEmitter();
  let buf = '';
  let gotReady = false;
  const readyWaiters = [];

  function handleLine(line) {
    line = line.trim();
    if (!line) return;
    let msg;
    try { msg = JSON.parse(line); } catch { return; }
    if (msg.event === 'ready') {
      gotReady = true;
      readyWaiters.splice(0).forEach(r => r());
    }
    if (msg.event) bus.emit(msg.event, msg);
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
  child.on('exit', code => bus.emit('exit', code));
  child.on('error', err => bus.emit('error', err));

  const ready = new Promise(res => { gotReady ? res() : readyWaiters.push(res); });

  function send(cmd) {
    try { child.stdin.write(JSON.stringify(cmd) + '\n'); } catch { /* child gone */ }
  }

  return {
    on: (ev, cb) => bus.on(ev, cb),
    once: (ev, cb) => bus.once(ev, cb),
    ready,
    setLeague(league) { send({ cmd: 'setLeague', league }); },
    setPriceCheckCorpus(enabled) { send({ cmd: 'setPriceCheckCorpus', enabled }); },
    setDebugLayout(enabled) { send({ cmd: 'setDebugLayout', enabled }); },
    collectDiagnostics(request = {}) { send({ cmd: 'collectDiagnostics', ...request }); },
    shutdown() { send({ cmd: 'shutdown' }); },
    kill() { try { child.kill(); } catch { /* best effort */ } },
  };
}

module.exports = { startSidecar };
