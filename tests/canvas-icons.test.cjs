const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
function fixture() {
    const images = [], draws = [];
    class Image { constructor() { images.push(this); this.complete = false; this.naturalWidth = 0; } }
    const ctx = new Proxy({}, { get: (_, name) => (...args) => draws.push([name, ...args]) });
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
    vm.runInNewContext(fs.readFileSync('src/Soulcrest.App/wwwroot/js/soulcrest-canvas-icons.js', 'utf8'), { window, L: { CircleMarker: Circle, Canvas: Circle, bounds: (start, end) => ({ start, end }) }, Image, document: { createElement: () => ({ getContext: () => ({ drawImage() {} }) }) } });
    let state = { scale: 1 };
    const create = (url = 'same.webp') => {
        const marker = window.soulcrestCanvasIcons.create([0, 0], { url, appearance: () => state });
        marker._renderer = { _drawing: true, _ctx: ctx }; marker.onAdd({}); return marker;
    };
    return { images, draws, create, api: window.soulcrestCanvasIcons, state: value => { state = value; } };
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
