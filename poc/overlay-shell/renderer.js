const btn = document.getElementById('pick');
const img = document.getElementById('fixture');
const overlay = document.getElementById('overlay');
const meta = document.getElementById('meta');

btn.addEventListener('click', async () => {
  btn.disabled = true;
  btn.textContent = 'detecting...';
  meta.textContent = '';
  try {
    const res = await window.api.pickFixture();
    if (!res) { return; }
    img.onload = () => drawOverlay(res.detection);
    img.src = res.dataUrl;
    meta.textContent = `rowCount=${res.detection.rowCount}  confidence=${res.detection.confidence}  pitch=${res.detection.pitch}  hasUsableRows=${res.detection.hasUsableRows}`;
  } catch (e) {
    meta.textContent = 'error: ' + e.message;
  } finally {
    btn.disabled = false;
    btn.textContent = 'Pick fixture PNG';
  }
});

function drawOverlay(detection) {
  overlay.innerHTML = '';
  if (!detection || !detection.rows) return;
  const scale = img.clientWidth / img.naturalWidth;
  for (const r of detection.rows) {
    const box = document.createElement('div');
    box.className = 'row-box' + (r.visibility !== 'Full' ? ' partial' : '');
    box.style.top = (r.top * scale) + 'px';
    box.style.height = ((r.bottom - r.top) * scale) + 'px';
    const lbl = document.createElement('span');
    lbl.className = 'row-label';
    lbl.textContent = `${r.kind} · ${r.visibility} · cy=${r.centerY}`;
    box.appendChild(lbl);
    overlay.appendChild(box);
  }
}
