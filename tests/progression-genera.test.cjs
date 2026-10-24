const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

const symbol = (id, x, y = 0) => [0, x, y, id, '', 0, id];
const monster = (id, x, y = 0) => [1, x, y, id, '', -1, id];
const place = (name, x) => [2, x, 0, name, '', 0];

function fixture(markers) {
    const calls = [];
    const context = { window: { calls }, L: { TileLayer: { extend: () => function () {} } } };
    const source = fs.readFileSync(path.join(__dirname, '../src/Soulcrest.App/wwwroot/js/soulcrest-map.js'), 'utf8')
        .replace('return { debugState, init, show,', `return {
            setup(data) { current = data; map = { getCenter: () => ({lng:0, lat:0}), closePopup() {} };
                dotnet = { invokeMethodAsync(...args) { window.calls.push(args); } }; },
            move(x) { player = { getLatLng: () => ({lng:x, lat:0}) }; updateProgression(); },
            target: () => progressionTarget, candidates: () => progressionCandidates,
            densityCache: () => progressionDensityAreas.get(current), debugState, init, show,`);
    vm.runInNewContext(source, context);
    const api = context.window.soulcrestMap;
    api.setup({ id: 'altgard', refZoom: 0, def: { metersPerPixel: 1 }, markers,
        categories: [{ group: 'Pets' }, { group: 'Monsters' }, { group: 'Locations' }] });
    const select = (allowed, { enabled = true, goal = 105, souls = {}, closest = false,
        missing = {}, density = false, counts = {}, kinds = ['pets'] } = {}) =>
        api.setProgression(enabled, goal, JSON.stringify(souls), JSON.stringify(kinds), closest,
            '', JSON.stringify(missing), density, JSON.stringify(counts), allowed === null ? null : JSON.stringify(allowed));
    return { api, calls, select };
}

test('each selected genus subset can provide guidance without filtering unrelated marker data', () => {
    const ids = ['cogni-pet', 'natura-pet', 'varian-pet', 'fera-pet'];
    const { api, select } = fixture(ids.map((id, i) => symbol(id, i + 1)));
    for (const id of ids) {
        select([id]);
        assert.equal(api.target().petId, id);
    }
    select(['varian-pet', 'fera-pet']);
    assert.equal(api.target().petId, 'varian-pet');
    select(ids);
    assert.equal(api.target().petId, 'cogni-pet');
});

test('explicit empty selection clears pet guidance while omitted and null arguments retain legacy choices', () => {
    const { api, calls, select } = fixture([symbol('near', 1), symbol('far', 20)]);
    select(['far']);
    assert.equal(api.target().petId, 'far');
    select([]);
    assert.equal(api.target(), null);
    assert.equal(calls.at(-1)[2], null);
    api.setProgression(true, 105, '{}');
    assert.equal(api.target().petId, 'near');
    select([], { enabled: false });
    assert.equal(api.target(), null);
    select(null);
    assert.equal(api.target().petId, 'near');
});

for (const goal of [5, 30, 105]) {
    test(`genus selection precedes the ${goal}-soul goal and never revives excluded pets`, () => {
        const { api, select } = fixture([symbol('excluded', 1), symbol('selected', 20)]);
        select(['selected'], { goal, souls: { selected: goal - 1 } });
        assert.equal(api.target().petId, 'selected');
        select(['selected'], { goal, souls: { selected: goal } });
        assert.equal(api.target(), null);
    });
}

test('closest-to-completion only ranks selected unfinished pets', () => {
    const { api, select } = fixture([symbol('excluded', 1), symbol('selected', 20), symbol('done', 0)]);
    const options = { closest: true, missing: { excluded: 1, selected: 25, done: 0 } };
    select(['selected', 'done'], options);
    assert.equal(api.target().petId, 'selected');
    select(['done'], options);
    assert.equal(api.target(), null);
    select(['excluded', 'selected'], options);
    assert.equal(api.target().petId, 'excluded');
});

test('excluded dense pets cannot outrank selected pets and their best-area geometry stays cached', () => {
    const { api, select } = fixture([symbol('excluded', 1), symbol('selected', 10), symbol('selected', 1000),
        monster('selected', 10), monster('selected', 1000), monster('selected', 1001)]);
    const options = { density: true, counts: { excluded: 100, selected: 3 } };
    select(['selected'], options);
    assert.equal(api.target().petId, 'selected');
    assert.equal(api.target().x, 1000);
    const cache = api.densityCache();
    assert.ok(cache);
    select(['excluded'], options);
    assert.equal(api.target().petId, 'excluded');
    select(['selected'], options);
    assert.equal(api.target().x, 1000);
    assert.equal(api.densityCache(), cache);
});

test('monster-only fallback obeys genus selection and density just like pet symbols', () => {
    const { api, select } = fixture([monster('excluded', 1), monster('selected', 10),
        monster('selected', 1000), monster('selected', 1001)]);
    select(['selected'], { density: true, counts: { excluded: 100, selected: 3 } });
    assert.equal(api.target().petId, 'selected');
    assert.equal(api.target().x, 1000);
    select(['selected']);
    assert.equal(api.target().x, 10);
    select([]);
    assert.equal(api.target(), null);
});

test('empty pet selection leaves chosen dungeons and strongholds competing by distance', () => {
    const { api, select } = fixture([symbol('pet', 1), place('Dungeon', 5), place('Stronghold', 10)]);
    const places = [{ index: 1, id: 'dungeon', kind: 'dungeon', done: false },
        { index: 2, id: 'stronghold', kind: 'stronghold', done: false }];
    api.setExploration(JSON.stringify(places));
    select([], { kinds: ['pets', 'dungeon', 'stronghold'] });
    assert.equal(api.target().petId, 'dungeon');
    select([], { kinds: ['pets', 'stronghold'] });
    assert.equal(api.target().petId, 'stronghold');
    select(['pet'], { kinds: ['pets', 'dungeon', 'stronghold'] });
    assert.equal(api.target().petId, 'pet');
    places[0].done = true;
    api.setExploration(JSON.stringify(places));
    select([], { kinds: ['pets', 'dungeon', 'stronghold'] });
    assert.equal(api.target().petId, 'stronghold');
});

test('rapid selection changes rebuild candidates immediately while player movement reuses them', () => {
    const { api, calls, select } = fixture([symbol('a', 10), symbol('a', 100), symbol('b', 20)]);
    select(['a']);
    const initial = api.candidates();
    api.move(100);
    assert.equal(api.target().x, 100);
    assert.equal(api.candidates(), initial);
    for (const id of ['b', 'a', 'b']) {
        const previous = api.candidates();
        select([id]);
        assert.equal(api.target().petId, id);
        assert.notEqual(api.candidates(), previous);
        assert.equal(calls.at(-1)[2], id);
    }
    select([]);
    assert.equal(api.target(), null);
    assert.equal(api.candidates().length, 0);
});
