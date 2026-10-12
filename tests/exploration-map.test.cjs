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
            current = data; base = ''; map = { getBounds: () => ({pad() { return this; }}), getCenter: () => ({lng: 0, lat: 0}), closePopup() { window.popupCloses++; } };
            markerRefs = data.markers.map(() => ({ iconIndex: 0, setIcon(icon) { this.icon = icon; } }));
            dotnet = { invokeMethodAsync(...args) { window.calls.push(args); } };
        }, popup: index => explorationLine(index), language(value) { lang = value; }, enableDrawing() { targetLayer = { clearLayers() { window.rings.length = 0; } }; current.refZoom = 4; }, layers: () => markerRefs, target: () => progressionTarget, debugState, init, show,`);
    context.window.calls = calls; context.window.rings = rings; context.window.popupCloses = 0;
    vm.runInNewContext(source, context);
    const api = context.window.soulcrestMap;
    api.setup({id: 'altgard', refZoom: 0, icons: ['place.png'], categories: [{group:'Locations'}, {group:'Pets'}],
        markers: [[0, 10, 0, 'Dungeon', '', 0], [0, 5, 0, 'Stronghold', '', 0], [1, 1, 0, 'Pet', '', 0, 'pet']]});
    const places = [{index:0, id:'d', kind:'dungeon', done:false}, {index:1, id:'s', kind:'stronghold', done:false}];
    api.setExploration(JSON.stringify(places));
    return {api, places, calls, rings, popupCloses: () => context.window.popupCloses};
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


test('exploration guidance has no completion radius and profile switches refresh the target', () => {
    const {api, calls, rings} = fixture();
    api.enableDrawing();
    api.setProgression(true, 5, '{}', '["dungeon"]', false, 'one');
    assert.equal(api.target().petId, 'd');
    assert.equal(rings.length, 0); // No proximity/completion area is attached to a game exploration target.
    const before = calls.length;
    api.setProgression(true, 5, '{}', '["dungeon"]', false, 'two');
    assert.equal(calls.length, before + 1);
    assert.equal(calls.at(-1)[0], 'OnProgressionTarget');
    assert.equal(api.target().petId, 'd');
});

test('dungeon and stronghold popups expose states without any clear or reset action', () => {
    const {api, calls, places} = fixture();
    for (const language of ['de', 'en']) {
        api.language(language);
        for (const index of [0, 1]) {
            assert.doesNotMatch(api.popup(index), /exploration-toggle|onclick/);
            assert.match(api.popup(index), new RegExp(language === 'de' ? 'Unbestätigt' : 'Unconfirmed'));
            api.toggleKibelisk(index); // Public callbacks cannot mutate these kinds either.
        }
        assert.equal(calls.length, 0);
        places[0].state = 'DiscoveredIncomplete';
        places[1].state = 'Undiscovered';
        api.setExploration(JSON.stringify(places));
        assert.match(api.popup(0), new RegExp(language === 'de' ? 'Entdeckt · offen' : 'Discovered · incomplete'));
        assert.match(api.popup(1), new RegExp(language === 'de' ? 'Unentdeckt' : 'Undiscovered'));
        places[0].state = places[1].state = null;
        api.setExploration(JSON.stringify(places));
    }
    assert.equal(api.toggleExplored, undefined);
});

test('manual Kibelisk binding remains isolated to the current map and profile', () => {
    const {api, calls, popupCloses} = fixture();
    api.setExploration(JSON.stringify([{index: 0, id: 'k', kind: 'kibelisk', done: false, state: null}]));
    api.setProgression(false, 5, '{}', '["pets"]', false, 'profile-one');
    assert.match(api.popup(0), /toggleKibelisk/);
    api.toggleKibelisk(0);
    assert.deepEqual([...calls.at(-1)], ['OnKibeliskBound', 'altgard', 'profile-one', 'k', true]);
    const beforeProfileSwitch = popupCloses();
    api.setProgression(false, 5, '{}', '["pets"]', false, 'profile-two');
    assert.equal(popupCloses(), beforeProfileSwitch + 1); // An old popup cannot act on the newly selected profile.
    api.setProgression(false, 5, '{}', '["pets"]', false, 'profile-two');
    assert.equal(popupCloses(), beforeProfileSwitch + 1); // Ordinary same-profile updates keep the popup open.
    api.setExploration(JSON.stringify([{index: 0, id: 'k', kind: 'kibelisk', done: true, state: 'DiscoveredCompleted'}]));
    api.toggleKibelisk(0);
    assert.deepEqual([...calls.at(-1)], ['OnKibeliskBound', 'altgard', 'profile-two', 'k', false]);
});

test('closest to completion picks the fewest missing souls, the nearest on a tie', () => {
    const {api} = fixture();
    api.setup({id: 'altgard', refZoom: 0, icons: ['pet.png'], categories: [{group:'Pets'}],
        markers: [[0, 1, 0, 'A', '', 0, 'a'], [0, 20, 0, 'B', '', 0, 'b'], [0, 5, 0, 'C', '', 0, 'c'], [0, 2, 0, 'Max', '', 0, 'max']]});
    api.setProgression(true, 5, '{}', '["pets"]', true, '', JSON.stringify({a: 3, b: 2, c: 2, max: 0}));
    assert.equal(api.target().petId, 'c'); // b and c miss 2 souls each; c is nearer
    api.setProgression(true, 5, '{}', '["pets"]', true, '', JSON.stringify({a: 1, b: 2, c: 2, max: 0}));
    assert.equal(api.target().petId, 'a');
    api.setProgression(true, 5, '{}', '["pets"]', true, '', JSON.stringify({a: 0, b: 0, c: 0, max: 0}));
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
