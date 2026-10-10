const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

function fixture() {
    const calls = [], layers = [], rings = [];
    const context = { window: {}, L: { TileLayer: { extend: () => function () {} }, divIcon: options => options, latLng: value => value, circleMarker: () => ({ addTo() { return this; } }), polygon: points => ({ addTo() { rings.push(points); return this; } }) } };
    const source = fs.readFileSync(path.join(__dirname, '../src/Soulcrest.App/wwwroot/js/soulcrest-map.js'), 'utf8')
        .replace('return { debugState, init, show,', `return { setup(data) {
            current = data; base = ''; map = { getBounds: () => ({pad() { return this; }}), getCenter: () => ({lng: 0, lat: 0}) };
            markerRefs = data.markers.map(() => ({ iconIndex: 0, setIcon(icon) { this.icon = icon; } }));
            dotnet = { invokeMethodAsync(...args) { window.calls.push(args); } };
        }, enableDrawing() { targetLayer = { clearLayers() { window.rings.length = 0; } }; current.refZoom = 4; }, layers: () => markerRefs, target: () => progressionTarget, debugState, init, show,`);
    context.window.calls = calls; context.window.rings = rings;
    vm.runInNewContext(source, context);
    const api = context.window.soulcrestMap;
    api.setup({id: 'altgard', refZoom: 0, icons: ['place.png'], categories: [{group:'Locations'}, {group:'Pets'}],
        markers: [[0, 10, 0, 'Dungeon', '', 0], [0, 5, 0, 'Stronghold', '', 0], [1, 1, 0, 'Pet', '', 0, 'pet']]});
    const places = [{index:0, id:'d', kind:'dungeon', done:false}, {index:1, id:'s', kind:'stronghold', done:false}];
    api.setExploration(JSON.stringify(places));
    return {api, places, calls, rings};
}

test('exploration modes select the nearest unfinished place of the chosen kind', () => {
    const {api, places} = fixture();
    api.setProgression(true, 5, '{}', '["dungeon"]', false); assert.equal(api.target().petId, 'd');
    api.setProgression(true, 5, '{}', '["stronghold"]', false); assert.equal(api.target().petId, 's');
    api.setProgression(true, 5, '{}', '["dungeon","stronghold"]', false); assert.equal(api.target().petId, 's');
    places[1].done = true; api.setExploration(JSON.stringify(places));
    api.setProgression(true, 5, '{}', '["dungeon","stronghold"]', false); assert.equal(api.target().petId, 'd');
    assert.match(api.layers()[1].icon.html, /exploration-check/);
    places[0].done = true; api.setExploration(JSON.stringify(places));
    api.setProgression(true, 5, '{}', '["dungeon","stronghold"]', false); assert.equal(api.target(), null);
    // Switching to another character with empty progress restores both markers and target.
    places.forEach(p => p.done = false); api.setExploration(JSON.stringify(places));
    api.setProgression(true, 5, '{}', '["dungeon","stronghold"]', false); assert.equal(api.target().petId, 's');
    assert.doesNotMatch(api.layers()[1].icon.html, /exploration-check/);
});

test('pet progression and disabling progression retain their existing behavior', () => {
    const {api} = fixture();
    api.setProgression(true, 5, '{}', '["pets"]', false); assert.equal(api.target().petId, 'pet');
    api.setProgression(true, 5, '{"pet":5}', '["pets"]', false); assert.equal(api.target(), null);
    api.setProgression(true, 5, '{}', '["dungeon","stronghold"]', false);
    api.setProgression(false, 5, '{}', '["dungeon","stronghold"]', false); assert.equal(api.target(), null);
});


test('arrival radius is expressed in map pixels and profile switches refresh the target', () => {
    const {api, calls, rings} = fixture();
    api.enableDrawing();
    api.setProgression(true, 5, '{}', '["dungeon"]', false, 80, 'one');
    assert.equal(rings.length, 1);
    const xs = rings[0].map(p => p[1]);
    assert.equal((Math.max(...xs) - Math.min(...xs)) * 16, 160);
    const before = calls.length;
    api.setProgression(true, 5, '{}', '["dungeon"]', false, 80, 'two');
    assert.equal(calls.length, before + 1);
    api.setProgression(true, 5, '{}', '["dungeon"]', false, 0, 'two');
    assert.equal(rings.length, 0);
});

test('closest to completion picks the fewest missing souls, the nearest on a tie', () => {
    const {api} = fixture();
    api.setup({id: 'altgard', refZoom: 0, icons: ['pet.png'], categories: [{group:'Pets'}],
        markers: [[0, 1, 0, 'A', '', 0, 'a'], [0, 20, 0, 'B', '', 0, 'b'], [0, 5, 0, 'C', '', 0, 'c'], [0, 2, 0, 'Max', '', 0, 'max']]});
    api.setProgression(true, 5, '{}', '["pets"]', true, 40, '', JSON.stringify({a: 3, b: 2, c: 2, max: 0}));
    assert.equal(api.target().petId, 'c'); // b and c miss 2 souls each; c is nearer
    api.setProgression(true, 5, '{}', '["pets"]', true, 40, '', JSON.stringify({a: 1, b: 2, c: 2, max: 0}));
    assert.equal(api.target().petId, 'a');
    api.setProgression(true, 5, '{}', '["pets"]', true, 40, '', JSON.stringify({a: 0, b: 0, c: 0, max: 0}));
    assert.equal(api.target(), null); // everything at max
});

test('Kibelisks are checked off but never chosen as dungeon or stronghold targets', () => {
    const {api} = fixture();
    api.setup({id: 'altgard', refZoom: 0, icons: ['place.png'], categories: [{group:'Locations'}],
        markers: [[0, 1, 0, 'Kibelisk', '', 0], [0, 30, 0, 'Dungeon', '', 0]]});
    api.setExploration(JSON.stringify([{index:0, id:'k', kind:'kibelisk', done:false}, {index:1, id:'d', kind:'dungeon', done:false}]));
    api.setProgression(true, 5, '{}', '["dungeon","stronghold"]', false); assert.equal(api.target().petId, 'd'); // the nearer Kibelisk is skipped
    // Not bound: grey, no check mark.
    assert.match(api.layers()[0].icon.className, /kibelisk-unbound/);
    assert.doesNotMatch(api.layers()[0].icon.html, /exploration-check/);
    // Bound: in colour, still no check mark (user request 2026-10-06); dungeons keep theirs.
    api.setExploration(JSON.stringify([{index:0, id:'k', kind:'kibelisk', done:true}, {index:1, id:'d', kind:'dungeon', done:true}]));
    assert.doesNotMatch(api.layers()[0].icon.className, /kibelisk-unbound/);
    assert.doesNotMatch(api.layers()[0].icon.html, /exploration-check/);
    assert.match(api.layers()[1].icon.html, /exploration-check/);
});

test('several progression kinds lead to the nearest open target among them', () => {
    // User request 2026-10-10: pets, sealed dungeons and strongholds can be chosen together.
    const {api} = fixture();
    api.setProgression(true, 5, '{}', '["pets","dungeon"]', false); assert.equal(api.target().petId, 'pet'); // pet at x=1 is nearest
    api.setProgression(true, 5, '{"pet":5}', '["pets","dungeon"]', false); assert.equal(api.target().petId, 'd'); // pet done: the dungeon
    api.setProgression(true, 5, '{}', '[]', false); assert.equal(api.target(), null); // nothing chosen, no target
});
