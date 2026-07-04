const assert = require('node:assert/strict');
const test = require('node:test');

const { overlayRegionKey, overlayRowsKey } = require('../overlay-state');

test('overlayRegionKey changes only when region geometry or offset changes', () => {
  assert.equal(
    overlayRegionKey({ x: 69, y: 205, w: 663, h: 715 }, 8),
    overlayRegionKey({ x: 69, y: 205, w: 663, h: 715 }, 8),
  );
  assert.notEqual(
    overlayRegionKey({ x: 69, y: 205, w: 663, h: 715 }, 8),
    overlayRegionKey({ x: 70, y: 205, w: 663, h: 715 }, 8),
  );
  assert.notEqual(
    overlayRegionKey({ x: 69, y: 205, w: 663, h: 715 }, 8),
    overlayRegionKey({ x: 69, y: 205, w: 663, h: 715 }, 12),
  );
});

test('overlayRowsKey ignores debug-only movement', () => {
  const first = overlayRowsKey({
    build: 'not-alone-beta 78',
    debugLayout: true,
    rows: [{ centerY: 109, name: 'breath of aldur', label: '1.5ex', hasPrice: true, divineValue: 0.01, exaltedValue: 1.5, multiplier: 1, meme: 'None' }],
    debugRows: [{ index: 1, top: 42, bottom: 96, textCenterY: 56 }],
  });
  const second = overlayRowsKey({
    build: 'not-alone-beta 78',
    debugLayout: true,
    rows: [{ centerY: 109, name: 'breath of aldur', label: '1.5ex', hasPrice: true, divineValue: 0.01, exaltedValue: 1.5, multiplier: 1, meme: 'None' }],
    debugRows: [{ index: 1, top: 44, bottom: 98, textCenterY: 58 }],
  });

  assert.equal(first, second);
});

test('overlayRowsKey changes when visible row state changes', () => {
  const stable = {
    build: 'not-alone-beta 78',
    debugLayout: true,
    rows: [{ centerY: 109, name: 'breath of aldur', label: '1.5ex', hasPrice: true, divineValue: 0.01, exaltedValue: 1.5, multiplier: 1, meme: 'None' }],
  };

  assert.notEqual(
    overlayRowsKey(stable),
    overlayRowsKey({ ...stable, rows: [{ ...stable.rows[0], centerY: 118 }] }),
  );
  assert.notEqual(
    overlayRowsKey(stable),
    overlayRowsKey({ ...stable, rows: [{ ...stable.rows[0], label: '2.0ex' }] }),
  );
  assert.notEqual(
    overlayRowsKey(stable),
    overlayRowsKey({ ...stable, debugLayout: false }),
  );
});
