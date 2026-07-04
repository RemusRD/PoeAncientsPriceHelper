// Control-panel page logic. Mirrors the old WinForms MainWindow: a league dropdown, a status line,
// and a version label. All data comes from sidecar events forwarded by the main process.
const league = document.getElementById('league');
const priceCorpus = document.getElementById('priceCorpus');
const debugLayout = document.getElementById('debugLayout');
const status = document.getElementById('status');
const version = document.getElementById('version');

const PRETTY = {
  'Runes of Aldur': 'Aldur SC',
  'HC Runes of Aldur': 'Aldur HC',
  'Standard': 'Standard SC',
  'Hardcore': 'Standard HC',
};

window.api.onConfig((_e, cfg) => {
  league.innerHTML = '';
  for (const name of cfg.availableLeagues) {
    const opt = document.createElement('option');
    opt.value = name;
    opt.textContent = PRETTY[name] || name;
    if (name === cfg.league) opt.selected = true;
    league.appendChild(opt);
  }
  priceCorpus.checked = !!cfg.priceCheckCorpusEnabled;
  debugLayout.checked = !!cfg.debugLayoutEnabled;
  if (cfg.build) version.textContent = cfg.build;
});

league.addEventListener('change', () => {
  status.textContent = 'Switching league\u2026';
  window.api.setLeague(league.value);
});

priceCorpus.addEventListener('change', () => {
  status.textContent = priceCorpus.checked
    ? 'Enabling corpus capture\u2026'
    : 'Disabling corpus capture\u2026';
  window.api.setPriceCheckCorpus(priceCorpus.checked);
});

debugLayout.addEventListener('change', () => {
  status.textContent = debugLayout.checked
    ? 'Enabling debug layout\u2026'
    : 'Disabling debug layout\u2026';
  window.api.setDebugLayout(debugLayout.checked);
});

window.api.onStatus((_e, s) => {
  status.textContent = s.text;
  status.classList.toggle('error', !!s.error);
});

window.api.version().then(v => { version.textContent = v; });
