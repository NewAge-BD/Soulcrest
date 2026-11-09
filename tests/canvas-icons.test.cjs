const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
function fixture() {
    const images = [], draws = [], canvases = [], bitmapDraws = [];
    class Image { constructor() { images.push(this); this.complete = false; this.naturalWidth = 0; } }
    const ctx = new Proxy({}, { get: (_, name) => (...args) => draws.push([name, ...args]) });
    const bitmapContext = new Proxy({}, { get: (_, name) => (...args) => bitmapDraws.push([name, ...args]) });
    class Circle {
        constructor(ll, opts) { this.initialize(ll, opts); }
        initialize(ll, opts) { this.options = opts; this._point = { x: 40, y: 50 }; }
        setRadius(r) { this.radius = r; return this; }
        redraw() { this.redraws = (this.redraws || 0) + 1; }
        _empty() { return !!this.outside; }
        onAdd(map) { this._map = map; }
        onRemove() { this._map = null; }
        _update() { this.updates = (this.updates || 0) + 1; }
        static extend(methods) { class Sub extends Circle {} Object.assign(Sub.prototype, methods); return Sub; }
    }
    const window = {};
    vm.runInNewContext(fs.readFileSync('src/Soulcrest.App/wwwroot/js/soulcrest-canvas-icons.js', 'utf8'), { window, L: { CircleMarker: Circle, Canvas: Circle, bounds: (start, end) => ({ start, end }) }, Image, document: { createElement: () => { const value = { getContext: () => bitmapContext }; canvases.push(value); return value; } } });
    let state = { scale: 1 };
    const create = (url = 'same.webp') => {
        const marker = window.soulcrestCanvasIcons.create([0, 0], { url, appearance: () => state });
        marker._renderer = { _drawing: true, _ctx: ctx }; marker.onAdd({}); return marker;
    };
    const createGroup = (urls, more = 0) => {
        const marker = window.soulcrestCanvasIcons.createGroup([0, 0], { urls, more, appearance: () => state });
        marker._renderer = { _drawing: true, _ctx: ctx }; marker.onAdd({}); return marker;
    };
    return { images, draws, bitmapDraws, canvases, create, createGroup, api: window.soulcrestCanvasIcons, state: value => { state = value; } };
}
test('thousands of icons reuse a decoded image, retain event layers, and release pending listeners', () => {
    const f = fixture(), markers = Array.from({ length: 10000 }, () => f.create());
    assert.equal(f.images.length, 1);
    markers[0].onRemove({});
    f.images[0].complete = true; f.images[0].naturalWidth = 512; f.images[0].onload();
    assert.equal(markers[0].redraws, undefined);
    assert.equal(markers[1].redraws, 1);
    assert.equal(markers[1]._imageEntry.waiting.size, 0);
    markers[1]._updatePath();
    assert.equal(f.draws.filter(d => d[0] === 'drawImage').length, 1);
});
test('clipped icons do not draw; zoom updates hit bounds without allocating images', () => {
    const f = fixture(), marker = f.create();
    marker.outside = true; marker._updatePath(); assert.equal(f.draws.length, 0);
    marker.outside = false; marker._renderer._drawing = false; marker._updatePath(); assert.equal(f.draws.length, 0);
    f.state({ scale: 2.4 }); marker.refreshAppearance(); assert.equal(marker.radius, 36.8);
    assert.equal(f.images.length, 1);
});
test('exploration and group toggles are read on redraw and failed icons remain selectable', () => {
    const f = fixture(), marker = f.create();
    marker._updatePath(); assert.ok(f.draws.some(d => d[0] === 'arc')); // fallback
    f.images[0].complete = true; f.images[0].naturalWidth = 512;
    f.state({ scale: 1, done: true, color: '#123456' }); marker.setIcon(); marker._updatePath();
    assert.ok(f.draws.some(d => d[0] === 'fillText' && d[1] === '✓'));
    const before = f.draws.filter(d => d[0] === 'stroke').length;
    f.state({ scale: 1, kibelisk: true, done: false }); marker._updatePath();
    assert.equal(f.draws.filter(d => d[0] === 'stroke').length, before);
});

test('panning reuses the cached canvas until its edge; zoom and uncovered viewports redraw', () => {
    const f = fixture(), renderer = f.api.renderer({});
    let contains = true, zoom = 3;
    renderer._zoom = 3;
    renderer._bounds = { contains: () => contains };
    renderer._map = { getZoom: () => zoom, getSize: () => ({}), containerPointToLayerPoint: () => ({ add: () => ({}) }) };
    renderer._update(); assert.equal(renderer.updates, undefined);
    contains = false; renderer._update(); assert.equal(renderer.updates, 1);
    contains = true; zoom = 4; renderer._update(); assert.equal(renderer.updates, 2);
    zoom = 3; renderer._container = {}; renderer._update(); assert.equal(renderer.updates, 3); // reattached after map switch
});


test('material rows share decoded images and composite sprites across thousands of exact spawns', () => {
    const f = fixture(), rows = Array.from({ length: 4000 }, () => f.createGroup(['meat.png', 'fruit.png', 'leather.png'], 2));
    assert.equal(f.images.length, 3);
    for (const image of f.images) { image.complete = true; image.naturalWidth = 512; image.onload(); }
    for (const row of rows) row._updatePath();
    assert.equal(f.canvases.length, 1); // one composite bitmap, reused for every spawn
    assert(f.bitmapDraws.some(draw => draw[0] === 'fillText' && draw[1] === '+2'));
    assert.equal(f.draws.filter(draw => draw[0] === 'drawImage').length, 4000);
    assert.equal(f.bitmapDraws.filter(draw => draw[0] === 'drawImage').length, 3);
    assert(rows.every(row => row.redraws === 3));
});

test('material row bounds and hit testing include the last icon and badge without a large circular click area', () => {
    const f = fixture(), row = f.createGroup(['a.png', 'b.png', 'c.png'], 12);
    assert.equal(row._groupSize.width, 94);
    assert.equal(row._groupSize.height, 24);
    assert.deepEqual(Array.from(row._getTooltipAnchor()), [35, 0]);
    assert.equal(row._containsPoint({ x: 86, y: 50 }), true);
    assert.equal(row._containsPoint({ x: 88, y: 50 }), false);
    assert.equal(row._containsPoint({ x: 40, y: 63 }), false);
    assert(row.radius >= Math.hypot(94, 24) / 2);
    f.state({ scale: 2 }); row.refreshAppearance();
    assert.equal(row._groupSize.width, 188);
    assert.equal(row._containsPoint({ x: 133, y: 50 }), true);
    assert.equal(f.images.length, 3);
});

test('material rows release pending source listeners on filter changes and removal, and skip clipped draws', () => {
    const f = fixture(), row = f.createGroup(['pending.png', 'shared.png']);
    assert.equal(f.images[0].complete, false);
    row.setItems(['shared.png'], 0);
    assert.equal(f.images[0].src, 'pending.png');
    row.onRemove({});
    for (const image of f.images) { image.complete = true; image.naturalWidth = 32; image.onload(); }
    assert.equal(row.redraws, undefined);
    row.outside = true; row._updatePath();
    assert.equal(f.draws.length, 0);
    assert.equal(f.canvases.length, 0);
    row.outside = false; row.onAdd({}); row._updatePath();
    assert.equal(f.canvases.length, 1);
    assert.equal(row._groupSize.width, 24);
});


test('loaded material icons and the extra-count label preserve transparent spacing without a background frame', () => {
    const f = fixture(), row = f.createGroup(['transparent-meat.png', 'transparent-fruit.png', 'transparent-leather.png'], 2);
    for (const image of f.images) { image.complete = true; image.naturalWidth = 32; image.onload(); }
    row._updatePath();
    assert.equal(f.bitmapDraws.filter(draw => draw[0] === 'drawImage').length, 3);
    assert(f.bitmapDraws.some(draw => draw[0] === 'fillText' && draw[1] === '+2'));
    assert.equal(f.bitmapDraws.filter(draw => draw[0] === 'fillRect').length, 0);
    assert.equal(f.bitmapDraws.filter(draw => draw[0] === 'strokeRect').length, 0);
});
