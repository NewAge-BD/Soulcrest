// Ported from overlay-editor-kit 1.15.0; original test scenarios preserved.
// Run with: node --test tests/overlay-kit-alignment.test.cjs
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const window = {};
vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../src/Soulcrest.App/wwwroot/overlay-kit/overlay-alignment.js'), 'utf8'), { window });
const snap = (rect, peers = [], options = {}) => window.soulcrestOverlayAlignment.snap({
    rect, peers, mode: 'move', tolerance: 6, bounds: { width: 1600, height: 1200 }, grid: 0, ...options
});
const card = (x, y, width = 101, height = 53) => ({ x, y, width, height });
const gaps = result => result.guides.filter(guide => guide.kind === 'gap').map(guide => guide.value);

test('repeats exact horizontal gaps between unequal widths, overriding the grid', () => {
    const result = snap(card(250, 21, 73, 53), [card(13, 21, 101), card(127, 21, 107)], { grid: 8 });
    assert.equal(result.x, 247);
    assert.deepEqual(Array.from(gaps(result)), [13, 13]);
});

test('repeats vertical gaps between unequal heights', () => {
    const result = snap(card(21, 167, 100, 79), [card(21, 11, 100, 53), card(21, 77, 100, 81)]);
    assert.equal(result.y, 171);
    assert.deepEqual(Array.from(gaps(result)), [13, 13]);
});

test('can prepend an unequal sized widget with the existing gap', () => {
    const result = snap(card(6, 20, 71, 53), [card(95, 20, 101), card(209, 20, 107)]);
    assert.equal(result.x, 11);
    assert.deepEqual(Array.from(gaps(result)), [13, 13]);
});

test('centers an inserted widget between neighbors with exact equal gaps', () => {
    const result = snap(card(118, 20, 69, 53), [card(10, 20, 90), card(212, 20, 105)]);
    assert.equal(result.x, 121.5);
    assert.deepEqual(Array.from(gaps(result)), [21.5, 21.5]);
});

test('centers insertion vertically as well', () => {
    const result = snap(card(20, 107, 90, 63), [card(20, 10, 90, 81), card(20, 200, 90, 70)], { tolerance: 10 });
    assert.equal(result.y, 114);
    assert.deepEqual(Array.from(gaps(result)), [23, 23]);
});

test('aligns corresponding edges of cards with different dimensions', () => {
    const result = snap(card(77, 150, 101, 53), [card(20, 20, 160, 50)], { grid: 8 });
    assert.equal(result.x, 79);
    assert.equal(result.y, 152);
    assert.ok(result.guides.some(guide => guide.kind === 'align' && guide.x1 === 180 && guide.x2 === 180));
});

test('aligns centers precisely with a grid fallback on the other axis', () => {
    const result = snap(card(43, 153, 101, 53), [card(20, 20, 150, 50)], { grid: 8 });
    assert.equal(result.x, 44.5);
    assert.equal(result.y, 152);
});

test('falls back to the grid and respects the snap threshold', () => {
    const result = snap(card(38, 155), [card(20, 20)], { grid: 8 });
    assert.equal(result.x, 40);
    assert.equal(result.y, 152);
    assert.equal(result.guides.length, 0);
});

test('preserves raw positions when both snapping mechanisms have no target', () => {
    const result = snap(card(38.2, 155.3));
    assert.equal(result.x, 38.2);
    assert.equal(result.y, 155.3);
    assert.equal(result.guides.length, 0);
});

test('resize matches dimensions without changing the fixed corner', () => {
    const result = snap(card(20, 150, 103, 57), [card(300, 20, 107, 53)], { mode: 'resize', grid: 8 });
    assert.equal(result.x, 20);
    assert.equal(result.y, 150);
    assert.equal(result.width, 107);
    assert.equal(result.height, 53);
    assert.ok(result.guides.length >= 2);
});

test('resize aligns right and bottom edges', () => {
    const result = snap(card(20, 200, 158, 60), [card(100, 20, 80, 50)], { mode: 'resize' });
    assert.equal(result.width, 160);
    assert.equal(result.height, 60);
    const vertical = snap(card(200, 20, 60, 158), [card(20, 100, 50, 80)], { mode: 'resize' });
    assert.equal(vertical.width, 60);
    assert.equal(vertical.height, 160);
});

test('resize repeats the gap of following neighbors', () => {
    const result = snap(card(10, 20, 75, 53), [card(100, 20, 91), card(204, 20, 104)], { mode: 'resize' });
    assert.equal(result.width, 77);
    assert.deepEqual(Array.from(gaps(result)), [13, 13]);
});

test('resize balances the leading and trailing gap between neighbors', () => {
    const result = snap(card(113, 20, 85, 53), [card(10, 20, 90), card(211, 20, 91)], { mode: 'resize' });
    assert.equal(result.width, 85);
    assert.deepEqual(Array.from(gaps(result)), [13, 13]);
});

test('resize does not report snaps below its minimum size', () => {
    const result = snap(card(20, 150, 78, 37), [card(300, 20, 77, 36)], {
        mode: 'resize', grid: 8, minimum: { width: 80, height: 40 }
    });
    assert.equal(result.width, 80);
    assert.equal(result.height, 40);
    assert.equal(result.guides.length, 0);
});

test('bounds clamp moves and resizes and reject out of bounds smart candidates', () => {
    const moved = snap(card(1510, 1160, 100, 50), [card(1504, 20, 96, 50)]);
    assert.equal(moved.x, 1500);
    assert.equal(moved.y, 1150);
    const resized = snap(card(1530, 1160, 100, 60), [], { mode: 'resize' });
    assert.equal(resized.width, 70);
    assert.equal(resized.height, 40);
});

test('cards in other rows cannot supply repeated horizontal spacing', () => {
    const result = snap(card(247, 200, 73), [card(13, 20, 101), card(127, 20, 107)]);
    assert.equal(gaps(result).length, 0);
});

test('large empty regions do not become repeated spacing targets', () => {
    const result = snap(card(700, 20, 73), [card(0, 20, 100), card(350, 20, 100)]);
    assert.equal(result.x, 700);
    assert.equal(gaps(result).length, 0);
});

test('smart snapping does not pull a widget into an overlapping peer', () => {
    const result = snap(card(14, 14, 101, 53), [card(10, 10, 101, 53)]);
    assert.equal(result.x, 14);
    assert.equal(result.y, 14);
    assert.equal(result.guides.length, 0);
});

test('spacing ignores blocked non-neighbor pairs', () => {
    const result = snap(card(402, 20, 71), [card(10, 20, 90), card(113, 20, 91), card(250, 20, 90)]);
    assert.equal(gaps(result).length, 0);
});

test('choice is deterministic when peers arrive in another order', () => {
    const peers = [card(13, 21, 101), card(127, 21, 107), card(22, 150, 100)];
    assert.equal(JSON.stringify(snap(card(250, 21, 73), peers)), JSON.stringify(snap(card(250, 21, 73), peers.slice().reverse())));
});

test('equal gaps win a near tie while substantially closer alignment wins', () => {
    const peers = [card(13, 21, 101), card(127, 21, 107), card(246, 200, 73)];
    const nearTie = snap(card(246.5, 21, 73), peers);
    assert.equal(nearTie.x, 247);
    assert.ok(gaps(nearTie).length > 0);
    const closerEdge = snap(card(245, 21, 73), peers);
    assert.equal(closerEdge.x, 246);
    assert.equal(gaps(closerEdge).length, 0);
});

test('peer geometry is never mutated', () => {
    const peers = [card(13, 21, 101), card(127, 21, 107)];
    const before = JSON.stringify(peers);
    snap(card(250, 21, 73), peers);
    assert.equal(JSON.stringify(peers), before);
});
