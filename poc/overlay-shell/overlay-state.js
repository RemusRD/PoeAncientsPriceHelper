function overlayRegionKey(region, xOffset = 0) {
  if (!region) return `hidden|${xOffset}`;
  return [region.x, region.y, region.w, region.h, xOffset].join(',');
}

function overlayRowsKey(message = {}) {
  return JSON.stringify({
    build: message.build || '',
    debugLayout: !!message.debugLayout,
    rows: (message.rows || []).map(normalizeRow),
  });
}

function normalizeRow(row = {}) {
  return {
    centerY: row.centerY ?? 0,
    name: row.name || '',
    label: row.label || '',
    divineValue: row.divineValue ?? 0,
    exaltedValue: row.exaltedValue ?? 0,
    hasPrice: !!row.hasPrice,
    multiplier: row.multiplier ?? 1,
    meme: row.meme || '',
    reason: row.reason || null,
  };
}

module.exports = { overlayRegionKey, overlayRowsKey };
