#!/usr/bin/env node

const fs = require('node:fs');
const path = require('node:path');

const defaultRoots = [
  path.resolve('src/PoeAncientsPriceHelper.Tests/Fixtures/Runeshape'),
  '/Users/richardydani/Documents/Codex/2026-06-14/poe-ancients-price-helper-automation/work/runeshape-diagnostics',
].filter(fs.existsSync);

const roots = process.argv.slice(2).map(p => path.resolve(p));
const scanRoots = roots.length > 0 ? roots : defaultRoots;

if (scanRoots.length === 0) {
  console.error('usage: tools/inspect-runeshape-fixtures.js <fixture-or-diagnostics-root> [...]');
  process.exit(2);
}

const captures = [];
for (const root of scanRoots) {
  for (const file of walk(root)) {
    if (path.basename(file) !== 'capture_region.png') continue;
    const folder = path.dirname(file);
    captures.push({
      root,
      folder,
      relativeFolder: path.relative(root, folder) || '.',
      capture: file,
      size: pngSize(file),
      rowDetection: jsonSummary(path.join(folder, 'row_detection.json')),
      ocrRows: arrayJsonSummary(path.join(folder, 'ocr_rows.json')),
      pricedRows: arrayJsonSummary(path.join(folder, 'priced_rows.json')),
    });
  }
}

captures.sort((a, b) => a.capture.localeCompare(b.capture));
process.stdout.write(JSON.stringify({
  roots: scanRoots,
  captureCount: captures.length,
  captures,
}, null, 2) + '\n');

function* walk(dir) {
  const entries = safeReaddir(dir);
  for (const entry of entries) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      yield* walk(full);
    } else if (entry.isFile()) {
      yield full;
    }
  }
}

function safeReaddir(dir) {
  try {
    return fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return [];
  }
}

function pngSize(file) {
  const buf = fs.readFileSync(file);
  if (buf.length < 24 || buf.toString('ascii', 1, 4) !== 'PNG') {
    return null;
  }

  return {
    width: buf.readUInt32BE(16),
    height: buf.readUInt32BE(20),
  };
}

function jsonSummary(file) {
  if (!fs.existsSync(file)) return null;
  const data = readJson(file);
  if (!data) return { file, valid: false };
  const rows = data.Rows || data.rows || [];
  return {
    file,
    valid: true,
    rowCount: Array.isArray(rows) ? rows.length : null,
    confidence: data.Confidence ?? data.confidence ?? null,
    rowPitch: data.RowPitch ?? data.rowPitch ?? null,
  };
}

function arrayJsonSummary(file) {
  if (!fs.existsSync(file)) return null;
  const data = readJson(file);
  if (!data) return { file, valid: false };
  const rows = Array.isArray(data) ? data : (data.Rows || data.rows || []);
  return {
    file,
    valid: true,
    rowCount: Array.isArray(rows) ? rows.length : null,
    sample: Array.isArray(rows) ? rows.slice(0, 3).map(row => row.Name || row.name || row.NormalizedName || row.normalizedName || row.OcrText || row.ocrText || null) : [],
  };
}

function readJson(file) {
  try {
    return JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch {
    return null;
  }
}
