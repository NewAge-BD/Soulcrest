const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

function fixture(extraBosses = [], extraMarkers = []) {
    let clock = 0, tick, intervals = 0;
    const refs = Array.from({ length: 3 + extraMarkers.length }, () => ({
        bound: 0, content: null,
        bindTooltip(_, options) { this.bound++; this.options = options; return this; },
        setTooltipContent(value) { this.content = value; },
        unbindTooltip() { this.content = null; }
    }));
    const context = { window: {}, performance: { now: () => clock },
        setInterval(fn) { intervals++; tick = fn; return 1; }, clearInterval() { tick = null; },
        L: { TileLayer: { extend: () => function () {} } }
    };
    const source = fs.readFileSync(path.join(__dirname, '../src/Soulcrest.App/wwwroot/js/soulcrest-map.js'), 'utf8')
        .replace('return { debugState, init, show,', `return { setup(data, refs) { current=data; markerRefs=refs; indexBossMarkers(); }, language(value) { lang=value; updateBossTimers(); }, debugState, init, show,`);
    vm.runInNewContext(source, context);
    const api = context.window.soulcrestMap;
    const data = { id: 'altgard', categories: [{ group: 'Locations', name: 'Field Boss' }, { name: 'NPC' }],
        markers: [[0, 10, 20], [0, 30, 40], [1, 10, 20], ...extraMarkers],
        bosses: [{ spawnId: 101, x: 10, y: 20 }, { spawnId: 102, x: 30, y: 40 }, ...extraBosses]
    };
    api.setup(data, refs);
    const send = (rows, map = 'altgard') => api.setBossTimers(map, JSON.stringify(rows));
    return { api, refs, send, data, intervals: () => intervals, advance(seconds) { clock += seconds * 1000; tick?.(); } };
}

test('captured timer joins exact spawn and boss point, persists without marker rebuild', () => {
    const f = fixture();
    f.send([{ spawnId: 101, remaining: 3602, age: 0, spawned: false, cached: false }]);
    assert.match(f.refs[0].content, />1:00:02</);
    assert.equal(f.refs[1].content, null);
    assert.equal(f.refs[2].content, null); // NPC at same coordinates is never a boss
    f.advance(1);
    assert.match(f.refs[0].content, />1:00:01</);
    f.send([{ spawnId: 101, remaining: 3601, age: 1, spawned: false, cached: false }]);
    assert.equal(f.refs[0].bound, 1);
    assert.equal(f.intervals(), 1);
    assert.equal(f.refs[0].options.permanent, true);
});

test('zero countdown awaits server confirmation; confirmed spawns persist until invalidation', () => {
    const f = fixture();
    f.send([{ spawnId: 101, remaining: 1, age: 0, spawned: false, cached: false }]);
    f.advance(1);
    assert.match(f.refs[0].content, /Awaiting confirmation/);
    assert.doesNotMatch(f.refs[0].content, />Spawned</);
    f.send([{ spawnId: 101, remaining: 0, age: 0, spawned: true, cached: false }]);
    assert.match(f.refs[0].content, />Spawned</);
    f.advance(120);
    assert.match(f.refs[0].content, />Spawned</);
    assert.doesNotMatch(f.refs[0].content, /unconfirmed/i);
    f.api.language('de');
    assert.match(f.refs[0].content, />Gespawnt</);
    f.send([{ spawnId: 101, remaining: 0, age: 0, spawned: true, cached: true }]);
    assert.match(f.refs[0].content, /Unbestätigt/);
});

test('an old captured schedule keeps counting down until the stream is invalidated', () => {
    const f = fixture();
    f.send([{ spawnId: 101, remaining: 3600, age: 600, spawned: false, cached: false }]);
    f.advance(60);
    assert.match(f.refs[0].content, />59:00</);
    assert.doesNotMatch(f.refs[0].content, /class="provisional"/);
    f.send([{ spawnId: 101, remaining: 3540, age: 660, spawned: false, cached: true }]);
    assert.match(f.refs[0].content, /class="provisional"/);
    assert.match(f.refs[0].content, /Saved · unconfirmed/);
    assert.equal(f.refs[0].bound, 1);
});

test('wrong map, unknown IDs, ambiguous catalogue and duplicate points never get a timer', () => {
    const f = fixture([{ spawnId: 101, x: 10, y: 20 }]);
    const row = { spawnId: 101, remaining: 60, age: 0, spawned: false };
    f.send([row]);
    assert(f.refs.every(r => r.content === null));
    const g = fixture([], [[0, 10, 20]]);
    g.send([row]);
    assert(g.refs.every(r => r.content === null));
    const h = fixture();
    h.send([row], 'verteron');
    h.send([{ ...row, spawnId: 999 }]);
    assert(h.refs.every(r => r.content === null));
});

test('replacement list removes old labels and empty list stops ticking', () => {
    const f = fixture();
    const row = { remaining: 60, age: 0, spawned: false };
    f.send([{ ...row, spawnId: 101 }]);
    f.send([{ ...row, spawnId: 102 }]);
    assert.equal(f.refs[0].content, null);
    assert.match(f.refs[1].content, />01:00</);
    f.send([]);
    assert(f.refs.every(r => r.content === null));
    f.advance(30);
    assert(f.refs.every(r => r.content === null));
});
