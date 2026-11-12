const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '../src/Soulcrest.App/wwwroot/js/dungeon-pet-map.js'), 'utf8');
const plain = value => JSON.parse(JSON.stringify(value));

function harness(page = 'https://app.soulcrest/index.html') {
    const elements = new Map();
    const maps = [], images = [], markers = [], observers = [], callbacks = [];
    let document;
    class Element {
        constructor(tag = 'div') {
            this.tagName = tag;
            this.children = [];
            this.attributes = {};
            this.listeners = new Map();
            this.isConnected = true;
            this.open = false;
            this.ownText = '';
        }
        set textContent(value) { this.ownText = String(value); this.children = []; }
        get textContent() { return this.ownText + this.children.map(child => child.textContent).join(''); }
        set innerHTML(_) { throw new Error('Tooltips must use textContent, not innerHTML'); }
        appendChild(child) { this.children.push(child); return child; }
        setAttribute(name, value) { this.attributes[name] = String(value); }
        addEventListener(name, callback) {
            const handlers = this.listeners.get(name) || new Set();
            handlers.add(callback);
            this.listeners.set(name, handlers);
        }
        removeEventListener(name, callback) { this.listeners.get(name)?.delete(callback); }
        emit(name) { for (const callback of [...(this.listeners.get(name) || [])]) callback(); }
        focus() { document.activeElement = this; this.focused = true; }
        showModal() { this.modalCalls = (this.modalCalls || 0) + 1; this.open = true; }
        close() { if (this.open) { this.open = false; this.emit('close'); } }
        escape() { this.emit('cancel'); this.close(); }
    }
    document = {
        baseURI: page,
        activeElement: new Element('button'),
        getElementById: id => elements.get(id) || null,
        createElement: tag => new Element(tag)
    };
    const addElement = (id, tag) => { const element = new Element(tag); elements.set(id, element); return element; };
    addElement('map-one'); addElement('map-two');
    const latLngBounds = points => ({ points: plain(points), pad(amount) { return { points: this.points, padding: amount }; } });
    const L = {
        CRS: { Simple: { simple: true } },
        latLngBounds,
        map(id, options) {
            const map = {
                id, options, panes: {}, fits: [], invalidations: 0, removed: false,
                createPane(name) { return this.panes[name] = { style: {} }; },
                setMaxBounds(bounds) { this.maxBounds = bounds; return this; },
                fitBounds(bounds, settings) { this.fits.push({ bounds, settings }); this.hasView = true; return this; },
                invalidateSize() { this.invalidations++; return this; },
                remove() { this.removed = true; }
            };
            maps.push(map);
            return map;
        },
        layerGroup() {
            return { layers: [], addTo(map) { this.map = map; return this; }, clearLayers() { this.layers = []; } };
        },
        imageOverlay(url, bounds, options) {
            const image = {
                url, bounds, options, handlers: new Map(), removed: false,
                on(name, callback) { this.handlers.set(name, callback); return this; },
                off(name, callback) { if (this.handlers.get(name) === callback) this.handlers.delete(name); return this; },
                addTo(map) {
                    assert.equal(map.hasView, true, 'Leaflet must have a view before image initialization');
                    this.map = map; return this;
                },
                remove() { this.removed = true; },
                fire(name) { this.handlers.get(name)?.(); }
            };
            images.push(image);
            return image;
        },
        circleMarker(point, options) {
            const marker = {
                point: plain(point), options, element: new Element('path'),
                bindTooltip(content, settings) { this.tooltip = content; this.tooltipOptions = settings; return this; },
                addTo(group) { group.layers.push(this); this.group = group; return this; },
                bringToFront() { this.front = true; },
                getElement() { return this.element; },
                openTooltip() { this.tooltipOpen = true; },
                closeTooltip() { this.tooltipOpen = false; }
            };
            markers.push(marker);
            return marker;
        }
    };
    class ResizeObserver {
        constructor(callback) { this.callback = callback; observers.push(this); }
        observe(element) { this.element = element; }
        disconnect() { this.disconnected = true; }
        fire() { this.callback(); }
    }
    const world = { untouched: true };
    const window = { location: new URL(page), soulcrestMap: world };
    vm.runInNewContext(source, { window, document, L, ResizeObserver, URL, console });
    const dotnet = { invokeMethodAsync(method, ...args) { callbacks.push([method, ...args]); return Promise.resolve(); } };
    return { api: window.soulcrestDungeonMaps, L, window, document, elements, addElement, maps, images, markers, observers, callbacks, dotnet, world };
}

const pin = (id, x, y, selected = false) => ({ id, petNumber: 1093, petName: 'Example pet', npcName: 'Example source', x, y, selected });
const scene = (key = 'dungeon-one', pins = [pin('one', 120, 240, true)]) => ({
    key, imageUrl: 'https://mapdata.soulcrest/dungeon-maps/example.png', width: 1200, height: 800, pins
});

test('independent maps use full image pixels and the dungeon [-y,x] convention', () => {
    const h = harness();
    assert.equal(h.api.create('map-one', scene(), h.dotnet), true);
    assert.equal(h.api.create('map-two', { ...scene('second'), width: 640, height: 480 }), true);
    assert.equal(h.maps.length, 2);
    assert.equal(h.maps[0].options.crs, h.L.CRS.Simple);
    assert.deepEqual(h.images[0].bounds.points, [[-800, 0], [0, 1200]]);
    assert.deepEqual(h.images[1].bounds.points, [[-480, 0], [0, 640]]);
    assert.equal(h.markers.length, 0, 'pins are not shown on an unloaded image');
    h.images[0].fire('load');
    assert.deepEqual(h.markers[0].point, [-240, 120]);
    assert.equal(h.images[0].map, h.maps[0]);
    assert.equal(h.images[1].map, h.maps[1]);
    assert.equal(h.window.soulcrestMap, h.world);
    assert.deepEqual(h.callbacks, [['OnDungeonMapReady', 'dungeon-one']]);
});

test('all evidenced pins remain visible while every selected pin participates in fit', () => {
    const h = harness();
    h.api.create('map-one', scene('many', [pin('selected-a', 40, 50, true), pin('other', 900, 700), pin('selected-b', 300, 400, true)]));
    h.images[0].fire('load');
    assert.equal(h.markers.length, 3);
    assert.equal(h.markers[0].options.radius, 4.5);
    assert.equal(h.markers[1].options.radius, 8);
    assert.equal(h.markers[1].options.color, '#f6f0ff');
    assert.equal(h.markers[1].options.pane, 'dungeon-selected-pets');
    assert.equal(h.markers[1].front, true);
    const fit = h.maps[0].fits.at(-1);
    assert.deepEqual(fit.bounds.points, [[-50, 40], [-400, 300]]);
    assert.equal(fit.settings.maxZoom, 1);
    assert.equal(h.api.fitSelected('map-one'), 2);
    h.markers[1].element.emit('focus');
    assert.equal(h.markers[1].tooltipOpen, true);
    h.markers[1].element.emit('blur');
    assert.equal(h.markers[1].tooltipOpen, false);
});

test('no selected pins fits all real positions and an empty scene creates no guessed center', () => {
    const h = harness();
    h.api.create('map-one', scene('all', [pin('a', 0, 0), pin('b', 1200, 800)]));
    h.images[0].fire('load');
    assert.deepEqual(h.maps[0].fits.at(-1).bounds.points, [[0, 0], [-800, 1200]]);
    assert.equal(h.api.fitSelected('map-one'), 2);
    h.api.setScene('map-one', scene('empty', []));
    h.images[1].fire('load');
    assert.equal(h.markers[0].group.layers.length, 0);
    assert.equal(h.api.fitSelected('map-one'), 0);
    assert.deepEqual(h.maps[0].fits.at(-1).bounds.points, [[-800, 0], [0, 1200]]);
});

test('only finite, evidenced in-image pin coordinates are rendered', () => {
    const h = harness();
    h.api.create('map-one', scene('validation', [pin('real', 10, 20), pin('negative', -1, 2), pin('outside', 1201, 1),
        pin('nan', NaN, 20), pin('infinite', 10, Infinity), { ...pin('unknown', 1, 2), petNumber: 0 }, { ...pin('', 1, 2) }]));
    h.images[0].fire('load');
    assert.equal(h.markers.length, 1);
    assert.deepEqual(h.markers[0].point, [-20, 10]);
});

test('tooltip and accessible names preserve hostile names as literal DOM text', () => {
    const h = harness();
    const malicious = { ...pin('literal', 10, 20), petName: '<img src=x onerror=alert(1)>', npcName: '<script>no()</script>' };
    h.api.create('map-one', scene('text', [malicious]));
    h.images[0].fire('load');
    const marker = h.markers[0];
    assert.equal(marker.tooltip.children[0].tagName, 'strong');
    assert.equal(marker.tooltip.children[0].textContent, malicious.petName);
    assert.equal(marker.tooltip.children[2].textContent, malicious.npcName);
    assert.equal(marker.element.attributes['aria-label'], malicious.petName + ': ' + malicious.npcName);
    assert.equal(marker.element.attributes.tabindex, '0');
});

test('a scene switch removes old pixels and rejects stale image success and failure', () => {
    const h = harness();
    h.api.create('map-one', scene('old'), h.dotnet);
    const oldLoad = h.images[0].handlers.get('load');
    const oldError = h.images[0].handlers.get('error');
    h.api.setScene('map-one', scene('new', [pin('new', 600, 500, true)]));
    assert.equal(h.images[0].removed, true);
    oldLoad(); oldError();
    assert.equal(h.markers.length, 0);
    assert.deepEqual(h.callbacks, []);
    h.images[1].fire('load');
    assert.deepEqual(h.markers[0].point, [-500, 600]);
    assert.deepEqual(h.callbacks, [['OnDungeonMapReady', 'new']]);
    oldLoad(); oldError();
    assert.deepEqual(h.callbacks, [['OnDungeonMapReady', 'new']]);
});

test('an image failure clears the scene and cannot later report ready', () => {
    const h = harness();
    h.api.create('map-one', scene('broken'), h.dotnet);
    const load = h.images[0].handlers.get('load');
    h.images[0].fire('error');
    load();
    assert.equal(h.images[0].removed, true);
    assert.equal(h.markers.length, 0);
    assert.equal(h.api.fitSelected('map-one'), 0);
    assert.deepEqual(h.callbacks, [['OnDungeonMapError', 'broken', 'image-unavailable']]);
});

test('invalid scenes clear previously visible markers instead of leaving a stale map', () => {
    const h = harness();
    h.api.create('map-one', scene(), h.dotnet);
    h.images[0].fire('load');
    assert.equal(h.api.setScene('map-one', { ...scene('bad'), width: 0 }), false);
    assert.equal(h.markers[0].group.layers.length, 0);
    assert.equal(h.images[0].removed, true);
    assert.deepEqual(h.callbacks.at(-1), ['OnDungeonMapError', 'bad', 'invalid-scene']);
});

test('production images are limited to the local virtual host or same-origin relative paths', () => {
    for (const url of ['https://mapdata.soulcrest/dungeon-maps/a.png', '/images/example.png', './images/example.png']) {
        const h = harness();
        assert.equal(h.api.create('map-one', { ...scene(), imageUrl: url }, h.dotnet), true, url);
        assert.equal(h.images.length, 1);
    }
    for (const url of ['https://external.example/map.png', '//external.example/map.png', 'http://mapdata.soulcrest/a.png',
        'https://mapdata.soulcrest:444/a.png', 'https://user:secret@mapdata.soulcrest/a.png', 'https://mapdata.soulcrest.evil/a.png',
        'data:image/png;base64,AA==', 'javascript:alert(1)', 'blob:https://app.soulcrest/a', 'file:///G:/private/map.png', '\\\\server\\map.png']) {
        const h = harness();
        assert.equal(h.api.create('map-one', { ...scene(), imageUrl: url }, h.dotnet), false, url);
        assert.equal(h.images.length, 0, url);
        assert.deepEqual(h.callbacks, [['OnDungeonMapError', 'dungeon-one', 'invalid-image-url']], url);
    }
});

test('local file images are explicitly confined to local-file offline preview pages', () => {
    for (const url of ['file:///G:/offline/map.png', '../assets/map.png']) {
        const h = harness('file:///G:/offline/previews/index.html');
        assert.equal(h.api.create('map-one', { ...scene(), imageUrl: url }), true, url);
    }
    for (const url of ['file://server/share/map.png', 'file:////server/share/map.png']) {
        const h = harness('file:///G:/offline/previews/index.html');
        assert.equal(h.api.create('map-one', { ...scene(), imageUrl: url }), false, url);
    }
});

test('resize, disposal and replacement do not keep old maps or delayed callbacks alive', () => {
    const h = harness();
    h.api.create('map-one', scene('first'), h.dotnet);
    const oldLoad = h.images[0].handlers.get('load');
    h.observers[0].fire();
    assert.equal(h.maps[0].invalidations, 1);
    h.api.create('map-one', scene('replacement'), h.dotnet);
    assert.equal(h.maps[0].removed, true);
    assert.equal(h.observers[0].disconnected, true);
    h.observers[0].fire(); oldLoad();
    assert.equal(h.maps[0].invalidations, 1);
    assert.deepEqual(h.callbacks, []);
    const latestLoad = h.images[1].handlers.get('load');
    h.api.dispose('map-one'); h.api.dispose('map-one');
    latestLoad();
    assert.equal(h.maps[1].removed, true);
    assert.equal(h.observers[1].disconnected, true);
    assert.deepEqual(h.callbacks, []);
    assert.equal(h.api.setScene('map-one', scene()), false);
    assert.equal(h.api.fitSelected('map-one'), 0);
});

test('missing containers and missing Leaflet report stable errors without changing the world map', () => {
    const h = harness();
    assert.equal(h.api.create('missing', scene(), h.dotnet), false);
    h.L.map = null;
    assert.equal(h.api.create('map-one', scene(), h.dotnet), false);
    assert.deepEqual(h.callbacks, [['OnDungeonMapError', 'dungeon-one', 'container-unavailable'],
        ['OnDungeonMapError', 'dungeon-one', 'map-unavailable']]);
    assert.equal(h.window.soulcrestMap, h.world);
});

test('disposed DotNet references cannot break successful scene rendering or cleanup', async () => {
    for (const invokeMethodAsync of [() => { throw new Error('disposed'); }, () => Promise.reject(new Error('disposed'))]) {
        const h = harness();
        h.api.create('map-one', scene(), { invokeMethodAsync });
        assert.doesNotThrow(() => h.images[0].fire('load'));
        h.api.dispose('map-one');
        await Promise.resolve();
    }
});

test('native modal dialogs handle Escape, report close once and restore their original trigger', () => {
    const h = harness();
    const dialog = h.addElement('dialog-one', 'dialog');
    const trigger = h.addElement('trigger-one', 'button');
    assert.equal(h.api.openDialog('dialog-one', 'trigger-one', h.dotnet), true);
    assert.equal(dialog.open, true);
    assert.equal(dialog.modalCalls, 1);
    assert.equal(h.api.openDialog('dialog-one', 'trigger-one', h.dotnet), true);
    assert.equal(dialog.modalCalls, 1);
    dialog.escape();
    assert.equal(dialog.open, false);
    assert.equal(h.document.activeElement, trigger);
    assert.deepEqual(h.callbacks, [['OnDungeonDialogClosed']]);
    h.api.closeDialog('dialog-one');
    dialog.emit('close');
    assert.deepEqual(h.callbacks, [['OnDungeonDialogClosed']]);
    h.api.openDialog('dialog-one', 'trigger-one', h.dotnet);
    h.api.closeDialog('dialog-one');
    assert.equal(h.callbacks.length, 2);
});

test('dialog helper failures are safe and disconnected trigger elements are not refocused', () => {
    const h = harness();
    assert.equal(h.api.openDialog('missing', 'missing', h.dotnet), false);
    const dialog = h.addElement('dialog-one', 'dialog');
    const trigger = h.addElement('trigger-one', 'button');
    dialog.showModal = () => { throw new Error('not connected'); };
    assert.equal(h.api.openDialog('dialog-one', 'trigger-one', h.dotnet), false);
    assert.equal(dialog.listeners.get('close').size, 0);
    dialog.showModal = () => { dialog.open = true; };
    h.api.openDialog('dialog-one', 'trigger-one', h.dotnet);
    trigger.isConnected = false;
    h.api.closeDialog('dialog-one');
    assert.equal(trigger.focused, undefined);
    assert.deepEqual(h.callbacks, [['OnDungeonDialogClosed']]);
});

test('an immediate reopen is not lost while the native close event is still queued', () => {
    const h = harness();
    const dialog = h.addElement('dialog-one', 'dialog');
    h.addElement('trigger-one', 'button');
    dialog.close = () => { dialog.open = false; };
    h.api.openDialog('dialog-one', 'trigger-one', h.dotnet);
    h.api.closeDialog('dialog-one');
    assert.deepEqual(h.callbacks, [['OnDungeonDialogClosed']]);
    assert.equal(h.api.openDialog('dialog-one', 'trigger-one', h.dotnet), true);
    assert.equal(dialog.modalCalls, 2);
    assert.equal(dialog.open, true);
    dialog.emit('close');
    assert.deepEqual(h.callbacks, [['OnDungeonDialogClosed']], 'the old queued close cannot dismiss the reopened dialog');
    h.api.closeDialog('dialog-one');
    assert.equal(h.callbacks.length, 2);
});
