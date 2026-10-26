const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

function fixture({ bosses = [], markers = [] } = {}) {
    const drawn = [];
    let clock = 0, tick, intervals = 0, clears = 0;
    const layer = (kind, position, options) => ({ kind, position, options, bound: 0, tooltip: null,
        addTo() { if (!drawn.includes(this)) drawn.push(this); return this; },
        setLatLng(p) { this.position = p; return this; },
        getLatLng() { return this.position; },
        getElement() { return { firstElementChild: { style: {} }, classList: { toggle() {} } }; },
        setLatLngs(p) { this.position = p; return this; },
        bindTooltip(_, tooltipOptions) { this.bound++; this.tooltipOptions = tooltipOptions; return this; },
        setTooltipContent(value) { this.tooltip = value; return this; },
        unbindTooltip() { this.tooltip = null; return this; },
        setContent(text) { this.text = text; return this; }
    });
    const refs = markers.map(() => layer('category', null, {}));
    const context = { window: {}, performance: { now: () => clock },
        setInterval(fn) { intervals++; tick = fn; return 1; }, clearInterval() { tick = null; },
        L: {
            TileLayer: { extend: () => function () {} }, latLng: p => p, divIcon: o => ({ ...o, options: o }), point: (x, y) => ({ x, y }),
            marker: (p, o) => layer('marker', p, o), tooltip: o => layer('tooltip', null, o),
            circleMarker: (p, o) => layer('ring', p, o), polyline: (p, o) => layer('line', p, o)
        }
    };
    const source = fs.readFileSync(path.join(__dirname, '../src/Soulcrest.App/wwwroot/js/soulcrest-map.js'), 'utf8')
        .replace('return { debugState, init, show,', `return { setup(mapId, bosses, markers, refs) {
            current = { id: mapId, refZoom: 4, markers, bosses, categories: [{ group: 'Locations', name: 'Field Boss' }] };
            markerRefs = refs;
            indexBossMarkers();
            const remove = layer => { const i = window.drawn.indexOf(layer); if (i >= 0) window.drawn.splice(i, 1); };
            map = { getBounds: () => ({ pad() { return this; }, contains() { return true; } }),
                latLngToLayerPoint: p => ({ x: p[1], y: p[0], distanceTo(other) { return Math.hypot(this.x - other.x, this.y - other.y); } }),
                layerPointToLatLng: p => [p.y, p.x], removeLayer: remove };
            targetLayer = { clearLayers() { window.drawn.length = 0; window.cleared(); }, removeLayer: remove };
        }, player(at) { player = at ? L.marker(at, {}) : null; playerNavigationValid = !!at; drawTargets(); },
        language(value) { lang = value; updateBossTimers(); }, debugState, init, show,`);
    context.window.drawn = drawn;
    context.window.cleared = () => clears++;
    vm.runInNewContext(source, context);
    const api = context.window.soulcrestMap;
    api.setup('altgard', bosses, markers, refs);
    return { api, drawn, refs, layer,
        send: (rows, showOlderSpawns = false) => api.setBossTimers('altgard', JSON.stringify(rows), showOlderSpawns),
        clears: () => clears,
        intervals: () => intervals,
        advance(seconds) { clock += seconds * 1000; tick?.(); }
    };
}

function target(spawnId, bossRole = 'spawned', values = {}) {
    return { id: `boss:1110:${spawnId}`, bossSpawnId: spawnId, bossRole, map: 'altgard',
        x: 160 * spawnId, y: 320 * spawnId, name: `Boss ${spawnId}`,
        color: bossRole === 'spawned' ? '#4ade80' : '#ef4444', ...values };
}
const spawnedTimer = (spawnId, spawnElapsed = 0, values = {}) => ({ spawnId, spawnElapsed, remaining: 0, age: 0, spawned: true, cached: false, ...values });
const pins = f => f.drawn.filter(x => x.kind === 'marker' && x.options.icon.className.includes('boss-rush-marker'));
const lines = f => f.drawn.filter(x => x.kind === 'line' && x.options.color !== '#000');

test('confirmed boss pin stays visible without its category and has one escaped name/status label', () => {
    const f = fixture();
    f.send([spawnedTimer(1)]);
    f.api.setTargets(JSON.stringify([target(1, 'spawned', { name: 'Boss <one>' })]));
    assert.equal(pins(f).length, 1);
    const pin = pins(f)[0];
    assert.match(pin.options.icon.html, /--boss-color:#4ade80/);
    assert.match(pin.tooltip, /Boss &lt;one&gt;/);
    assert.match(pin.tooltip, />Spawned · 00:00</);
    assert.equal(pin.bound, 1);
    assert.equal(f.drawn.filter(x => x.kind === 'tooltip').length, 0);
    assert.deepEqual(Array.from(pin.position), [-20, 10]);
    assert(f.drawn.some(x => x.kind === 'ring' && x.options.color === '#4ade80'));
    f.api.setTargets('[]');
    assert.equal(f.drawn.length, 0);
    assert.equal(pin.tooltip, null);
});

test('all spawned bosses and first upcoming spawn start at the player, later spawns chain in time order', () => {
    const f = fixture();
    f.send([spawnedTimer(1), spawnedTimer(2)]);
    f.api.player([-3, 2]);
    f.api.setTargets(JSON.stringify([
        target(1), target(2), target(3, 'next'),
        target(4, 'preview', { after: 'boss:1110:3' }),
        target(5, 'preview', { after: 'boss:1110:4' })
    ]));
    const routeLines = lines(f);
    assert.equal(routeLines.length, 5);
    assert.deepEqual(routeLines.map(line => line.options.color), ['#4ade80', '#4ade80', '#ef4444', '#ef4444', '#ef4444']);
    for (const line of routeLines.slice(0, 3)) assert.deepEqual(Array.from(line.position[0]), [-3, 2]);
    assert.deepEqual(Array.from(routeLines[3].position[0]), [-60, 30]);
    assert.deepEqual(Array.from(routeLines[4].position[0]), [-80, 40]);
    assert.equal(routeLines[2].options.dashArray, null);
    assert.equal(routeLines[3].options.dashArray, '10 8');
    assert(routeLines[3].options.opacity < 0.5);
    assert.equal(pins(f)[2].options.opacity, 1);
    assert.equal(pins(f)[3].options.opacity, 0.45);
});

test('preview chain remains visible without a player while direct player legs are absent', () => {
    const f = fixture();
    f.send([spawnedTimer(1)]);
    f.api.setTargets(JSON.stringify([target(1), target(2, 'next'), target(3, 'preview', { after: 'boss:1110:2' })]));
    assert.equal(pins(f).length, 3);
    assert.equal(lines(f).length, 1);
    assert.deepEqual(Array.from(lines(f)[0].position[0]), [-40, 20]);
    assert.deepEqual(Array.from(lines(f)[0].position[1]), [-60, 30]);
});

test('missing chronological boss position breaks that leg instead of falling back to the player', () => {
    const f = fixture();
    f.api.player([-3, 2]);
    f.api.setTargets(JSON.stringify([
        target(1, 'next'),
        target(3, 'preview', { after: 'boss:1110:2' }),
        target(4, 'preview', { after: 'boss:1110:3' })
    ]));
    assert.equal(pins(f).length, 3);
    assert.equal(lines(f).length, 2);
    assert.deepEqual(Array.from(lines(f)[0].position[0]), [-3, 2]);
    assert.deepEqual(Array.from(lines(f)[1].position[0]), [-60, 30]);
    assert.deepEqual(Array.from(lines(f)[1].position[1]), [-80, 40]);
});

test('cached pins are faded green and preserve an explicitly unconfirmed countdown in both languages', () => {
    const f = fixture();
    f.api.player([-3, 2]);
    f.api.setTargets(JSON.stringify([
        target(1, 'spawned', { bossProvisional: true }),
        target(2, 'next', { bossProvisional: true, color: '#4ade80' })
    ]));
    f.send([
        { spawnId: 1, spawnElapsed: 10, remaining: 0, age: 10, spawned: true, cached: true },
        { spawnId: 2, remaining: 61, age: 10, spawned: false, cached: true }
    ]);
    const [alive, upcoming] = pins(f);
    assert.match(alive.tooltip, /Unconfirmed/);
    assert.doesNotMatch(alive.tooltip, />Spawned</);
    assert.match(upcoming.tooltip, /Unconfirmed · 01:01/);
    assert(pins(f).every(pin => pin.options.opacity < 0.5 && pin.options.icon.html.includes('#4ade80')));
    assert(lines(f).every(line => line.options.color === '#4ade80' && line.options.opacity < 0.5));
    f.advance(1);
    assert.match(upcoming.tooltip, /Unconfirmed · 01:00/);
    assert.equal(pins(f)[1], upcoming);
    assert.equal(upcoming.bound, 1);
    f.api.language('de');
    assert.match(upcoming.tooltip, /Unbestätigt · 01:00/);
    f.advance(60);
    assert.match(pins(f).find(pin => pin.options.icon.className.includes('boss-role-next')).tooltip, /Unbestätigt · Warte auf Bestätigung/);
    assert.doesNotMatch(pins(f).find(pin => pin.options.icon.className.includes('boss-role-next')).tooltip, />Gespawnt</);
});

test('first upcoming pin keeps a live countdown at zero without turning into a spawned marker', () => {
    const f = fixture();
    f.api.setTargets(JSON.stringify([target(1, 'next')]));
    f.send([{ spawnId: 1, remaining: 1, age: 0, spawned: false, cached: false }]);
    const pin = pins(f)[0];
    assert.match(pin.tooltip, />00:01</);
    f.advance(1);
    assert.match(pin.tooltip, /Awaiting confirmation/);
    assert.doesNotMatch(pin.tooltip, />Spawned</);
    assert.match(pin.options.icon.html, /#ef4444/);
});

test('timer label moves from category marker to pin and back without duplicate labels', () => {
    const f = fixture({ bosses: [{ spawnId: 1, x: 160, y: 320 }], markers: [[0, 160, 320]] });
    f.send([{ spawnId: 1, remaining: 62, age: 0, spawned: false, cached: false }]);
    const category = f.refs[0];
    assert.match(category.tooltip, />01:02</);
    f.api.setTargets(JSON.stringify([target(1, 'next')]));
    const pin = pins(f)[0];
    assert.equal(category.tooltip, null);
    assert.match(pin.tooltip, /Boss 1/);
    assert.match(pin.tooltip, />01:02</);
    f.advance(1);
    assert.match(pin.tooltip, />01:01</);
    assert.equal(pin.bound, 1);
    f.api.setTargets('[]');
    assert.equal(pin.tooltip, null);
    assert.match(category.tooltip, />01:01</);
    assert.equal(category.bound, 2);
    assert.equal(f.intervals(), 1);
});

test('pin-first updates attach the timer once and changing a target does not retain its old label', () => {
    const f = fixture({ bosses: [{ spawnId: 1, x: 160, y: 320 }], markers: [[0, 160, 320]] });
    f.api.setTargets(JSON.stringify([target(1, 'next')]));
    const oldPin = pins(f)[0];
    assert.match(oldPin.tooltip, /Waiting for boss timer/);
    f.send([{ spawnId: 1, remaining: 60, age: 0, spawned: false, cached: false }]);
    assert.match(oldPin.tooltip, />01:00</);
    assert.equal(oldPin.bound, 1);
    assert.equal(f.refs[0].tooltip, null);
    f.api.setTargets(JSON.stringify([target(1, 'next', { name: 'Renamed', x: 320 })]));
    assert.equal(pins(f).length, 1);
    assert.equal(oldPin.tooltip, null);
    assert.match(pins(f)[0].tooltip, /Renamed/);
    assert.match(pins(f)[0].tooltip, />01:00</);
    assert.equal(f.refs[0].tooltip, null);
});

test('boss targets never appear on another map', () => {
    const f = fixture();
    f.api.setTargets(JSON.stringify([target(1, 'next', { map: 'verteron' })]));
    assert.equal(f.drawn.length, 0);
});


test('invalid player context removes direct boss lines and their arrows but keeps fixed preview legs', () => {
    const f = fixture();
    f.send([spawnedTimer(1)]);
    f.api.setNavigationContext(1, true);
    f.api.setPlayer('altgard', 0, 0, false, true, 1);
    f.api.setTargets(JSON.stringify([
        target(1, 'spawned', { x: 16000, y: 0 }),
        target(2, 'next', { x: 16000, y: 8000 }),
        target(3, 'preview', { x: 24000, y: 8000, after: 'boss:1110:2' })
    ]));
    const arrows = () => f.drawn.filter(layer => layer.options?.icon?.className === 'route-arrow');
    const directLines = lines(f).slice(0, 2);
    const before = arrows().length;
    assert.equal(lines(f).length, 3);
    assert(before > 10);
    f.api.setNavigationContext(2, false);
    assert.equal(lines(f).length, 1);
    assert.equal(pins(f).length, 3);
    assert(arrows().length > 0 && arrows().length < before);
    assert(directLines.every(line => !f.drawn.includes(line)));
    assert.deepEqual(Array.from(lines(f)[0].position[0]), [-500, 1000]);
    assert.deepEqual(Array.from(lines(f)[0].position[1]), [-500, 1500]);
    f.api.setNavigationContext(3, true);
    assert.equal(lines(f).length, 1); // the new generation needs its own fresh player fix
    f.api.setPlayer('altgard', 800, 800, false, true, 1);
    assert.equal(lines(f).length, 1); // stale fixes cannot restore old player-rooted guidance
    f.api.setPlayer('altgard', 800, 800, false, false, 3);
    assert.equal(lines(f).length, 1);
    f.api.setPlayer('altgard', 320, 640, false, true, 3);
    assert.equal(lines(f).length, 3);
    for (const line of lines(f).slice(0, 2)) assert.deepEqual(Array.from(line.position[0]), [-40, 20]);
    f.api.setNavigationContext(4, true);
    assert.equal(lines(f).length, 1); // even an allowed generation change revokes the previous fix
});

test('a lost player fix only removes boss-rooted legs while manual legacy guidance remains', () => {
    const f = fixture();
    f.send([spawnedTimer(1)]);
    f.api.setPlayer('altgard', 0, 0, false, true);
    const manual = { id: 'manual', map: 'altgard', x: 160, y: 160, name: 'Manual', color: '#facc15' };
    f.api.setTargets(JSON.stringify([manual, target(1), target(2, 'next'), target(3, 'preview', { after: 'boss:1110:2' })]));
    assert.equal(lines(f).length, 4);
    f.api.setPlayer('altgard', 0, 0, false, false);
    assert.equal(lines(f).length, 2);
    assert.equal(lines(f)[0].options.color, '#facc15');
    assert.deepEqual(Array.from(lines(f)[1].position[0]), [-40, 20]);
    f.api.setPlayer('altgard', 160, 160, false, true);
    assert.equal(lines(f).length, 4);
});


test('spawn lifetime counts upwards independently of list age and expires only after the inclusive minute', () => {
    const f = fixture({ bosses: [{ spawnId: 1, x: 16000, y: 0 }], markers: [[0, 16000, 0]] });
    f.api.player([0, 0]);
    f.send([spawnedTimer(1, 25.25, { age: 7200 }), { spawnId: 2, remaining: 600, age: 0, spawned: false }]);
    f.api.setTargets(JSON.stringify([
        target(1, 'spawned', { x: 16000, y: 0 }), target(2, 'next'),
        target(3, 'preview', { after: 'boss:1110:2' })
    ]));
    const spawnedPin = pins(f)[0], originalPins = pins(f), originalLines = lines(f), clears = f.clears();
    assert.match(spawnedPin.tooltip, />Spawned · 00:25</);
    assert.match(spawnedPin.tooltip, /Last update.*7200 s/);
    f.advance(0.74);
    assert.match(spawnedPin.tooltip, />Spawned · 00:25</);
    f.advance(0.01);
    assert.match(spawnedPin.tooltip, />Spawned · 00:26</);
    f.api.language('de');
    assert.match(spawnedPin.tooltip, />Gespawnt · 00:26</);
    f.advance(34);
    assert.equal(pins(f)[0], spawnedPin);
    assert.match(spawnedPin.tooltip, />Gespawnt · 01:00</);
    assert.deepEqual(pins(f), originalPins);
    assert.deepEqual(lines(f), originalLines);
    assert.equal(f.clears(), clears);
    const arrowsBefore = f.drawn.filter(layer => layer.options?.icon?.className === 'route-arrow').length;
    assert(arrowsBefore > 10);
    f.advance(0.001);
    assert.equal(pins(f).length, 2);
    assert(pins(f).every(pin => !pin.options.icon.className.includes('boss-role-spawned')));
    assert.equal(lines(f).length, 2);
    assert.equal(spawnedPin.tooltip, null);
    assert.match(f.refs[0].tooltip, />Gespawnt · 01:00</);
    assert.equal(f.refs[0].bound, 2);
    assert.equal(f.clears(), clears + 1);
    assert(f.drawn.filter(layer => layer.options?.icon?.className === 'route-arrow').length < arrowsBefore);
    f.advance(5);
    assert.match(f.refs[0].tooltip, />Gespawnt · 01:05</);
    assert.equal(f.clears(), clears + 1); // old category statuses keep ticking without scene rebuilds
});

test('cached recent spawns have an unconfirmed lifetime and expire without confirming death', () => {
    const f = fixture({ bosses: [{ spawnId: 1, x: 160, y: 320 }], markers: [[0, 160, 320]] });
    f.api.player([0, 0]);
    f.api.setTargets(JSON.stringify([target(1, 'spawned', { bossProvisional: true })]));
    f.send([spawnedTimer(1, 59.25, { age: 0, cached: true })]);
    assert.match(pins(f)[0].tooltip, /Unconfirmed · 00:59/);
    f.advance(0.75);
    assert.match(pins(f)[0].tooltip, /Unconfirmed · 01:00/);
    f.advance(0.001);
    assert.equal(pins(f).length, 0);
    assert.equal(lines(f).length, 0);
    assert.match(f.refs[0].tooltip, /Unconfirmed · 01:00/);
    assert.doesNotMatch(f.refs[0].tooltip, /Awaiting confirmation|Dead|Despawned/);
});

test('show-older preference restores known older spawned pins and keeps long upward counters running', () => {
    const f = fixture();
    const rows = [spawnedTimer(1, 3725.9, { age: 1 }), spawnedTimer(2, 86401, { age: 0, cached: true })];
    f.api.player([0, 0]);
    f.api.setTargets(JSON.stringify([target(1), target(2, 'spawned', { bossProvisional: true }), target(3, 'next')]));
    f.send(rows);
    assert.equal(pins(f).length, 1);
    assert.equal(lines(f).length, 1);
    f.send(rows, true);
    assert.equal(pins(f).length, 3);
    assert.equal(lines(f).length, 3);
    assert.match(pins(f)[0].tooltip, /Spawned · 62:05/);
    assert.match(pins(f)[1].tooltip, /Unconfirmed · 1440:01/);
    const pin = pins(f)[0], clears = f.clears();
    f.advance(1);
    assert.equal(pins(f)[0], pin);
    assert.match(pin.tooltip, /Spawned · 62:06/);
    assert.equal(f.clears(), clears);
    f.send(rows, false);
    assert.equal(pins(f).length, 1);
    assert.equal(lines(f).length, 1);
    f.send(rows, true);
    assert.equal(pins(f).length, 3);
    f.api.setBossTimers('altgard', JSON.stringify(rows)); // omitted third argument uses the safe default
    assert.equal(pins(f).length, 1);
});

test('unknown or invalid spawn lifetime stays hidden in both modes and never uses observation age', () => {
    for (const spawnElapsed of [undefined, null, -1, '25', NaN, Infinity]) {
        const f = fixture();
        f.api.player([0, 0]);
        f.api.setTargets(JSON.stringify([target(1)]));
        assert.equal(pins(f).length, 0); // pin-first must wait for its actual spawn timestamp
        for (const older of [false, true]) {
            f.send([spawnedTimer(1, spawnElapsed, { spawnElapsed, age: 25 })], older);
            assert.equal(pins(f).length, 0);
            assert.equal(lines(f).length, 0);
            f.advance(1);
            assert.equal(pins(f).length, 0);
        }
    }
});

test('fresh server corrections restore eligible pins without resetting lifetime to a new list age', () => {
    const f = fixture();
    f.api.player([0, 0]);
    f.api.setTargets(JSON.stringify([target(1)]));
    f.send([spawnedTimer(1, 61, { age: 0 })]);
    assert.equal(pins(f).length, 0);
    f.send([spawnedTimer(1, 40, { age: 0 })]);
    assert.equal(pins(f).length, 1);
    assert.match(pins(f)[0].tooltip, />Spawned · 00:40</);
    const pin = pins(f)[0], clears = f.clears();
    f.advance(1);
    f.send([spawnedTimer(1, 41, { age: 0 })]);
    assert.equal(pins(f)[0], pin);
    assert.match(pin.tooltip, />Spawned · 00:41</);
    assert.equal(f.clears(), clears);
});
