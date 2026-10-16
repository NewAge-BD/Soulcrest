const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const plain = value => JSON.parse(JSON.stringify(value));
const payload = () => ({
    id: 'altgard', refZoom: 0, icons: ['cube.png'], regions: [],
    categories: [{ group: 'Collectibles', name: 'Hidden Cube', icon: 0, count: 5 },
        { group: 'Resources', name: 'Gem', icon: 0, count: 1 },
        { group: 'Monsters', name: 'Fera', icon: -1, count: 1 },
        { group: 'Collectibles', name: 'Cube Groups', de: 'Cube Groups', icon: -1, count: 2 }],
    groups: [{ name: 'Collectibles', categories: [0, 3] }, { name: 'Resources', categories: [1] },
        { name: 'Monsters', categories: [2] }],
    markers: [[0, 100, 100, 'Cube', null, -1, null, { hiddenCubeGroup: 'a' }],
        [0, 200, 100, 'Cube', null, -1, null, { hiddenCubeGroup: 'a' }],
        [0, 200, 200, 'Cube', null, -1, null, { hiddenCubeGroup: 'a' }],
        [0, 400, 100, 'Cube', null, -1, null, { hiddenCubeGroup: 'b' }],
        [0, 500, 200, 'Cube', null, -1, null, { hiddenCubeGroup: 'b' }],
        [1, 700, 700, 'Ruby', null, -1, null], [2, 900, 900, 'Monster', null, -1, null]],
    hiddenCubeGroups: [{ id: 'a', number: 1, color: '#a78bfa', markers: [0, 1, 2], connections: [[0, 1], [1, 2]] },
        { id: 'b', number: 2, color: '#38bdf8', markers: [3, 4], connections: [[3, 4]] }]
});

function fixture(data = payload()) {
    const position = value => Array.isArray(value) ? { lat: value[0], lng: value[1] } : value;
    const group = () => {
        const members = new Set();
        return {
            addTo(parent) { parent.addLayer(this); return this; },
            addLayer(layer) { members.add(layer); return this; },
            removeLayer(layer) { members.delete(layer); return this; },
            hasLayer(layer) { return members.has(layer) || [...members].some(child => child.hasLayer?.(layer)); },
            getLayers: () => [...members], eachLayer(callback) { [...members].forEach(callback); },
            clearLayers() { members.clear(); return this; }
        };
    };
    const layer = (kind, at, options = {}) => ({ kind, at, options,
        addTo(parent) { parent.addLayer(this); return this; },
        bindPopup(factory) { this.popup = factory; return this; },
        bindTooltip() { return this; }, on() { return this; }, closePopup() {},
        getLatLng() { return position(this.at); }, setLatLng(value) { this.at = value; return this; },
        getElement: () => ({ classList: { toggle() {} } }),
        setRadius(value) { this.options.radius = value; return this; }, getRadius() { return this.options.radius; }
    });
    const containerClasses = new Set();
    const map = Object.assign(group(), {
        center: { lat: 0, lng: 0 }, zoom: 0, attributionControl: { setPrefix() {} },
        on() { return this; }, createPane() {}, getPane: () => ({ style: {} }),
        getZoom() { return this.zoom; }, getCenter() { return this.center; },
        getContainer: () => ({ style: { setProperty() {} }, classList: { add(value) { containerClasses.add(value); }, remove(value) { containerClasses.delete(value); }, toggle(value, on) { if (on) containerClasses.add(value); else containerClasses.delete(value); } } }),
        getBounds: () => ({ pad() { return this; }, contains: () => true }),
        setMaxBounds() {}, fitBounds() {}, flyToBounds() {}, closePopup() {},
        setView(at, zoom) { this.center = position(at); this.zoom = zoom; },
        panTo(at) { this.center = position(at); }
    });
    const L = {
        map: () => map, canvas: () => ({}), CRS: { Simple: {} },
        TileLayer: { extend: () => class { addTo(parent) { parent.addLayer(this); return this; } } },
        latLng: position, latLngBounds: () => ({ pad() { return this; } }), layerGroup: group,
        divIcon: options => ({ options }), marker: (at, options) => layer('marker', at, options),
        circleMarker: (at, options) => layer('circle', at, options), polyline: (at, options) => layer('line', at, options)
    };
    const next = { ...payload(), id: 'next', hiddenCubeGroups: undefined };
    const context = { window: { SoulcrestMaps: { altgard: data, next } }, L,
        document: { visibilityState: 'hidden' }, performance, setTimeout, clearTimeout, setInterval, clearInterval };
    const source = fs.readFileSync(process.env.SOULCREST_MAP_SCRIPT || path.join(__dirname, '../src/Soulcrest.App/wwwroot/js/soulcrest-map.js'), 'utf8')
        .replace('return { debugState, init, show,', `return {
            cubeState: () => ({ categories: catLayers, markers: markerRefs, groups: cubeGroups?.groups || [], connectionLayers: cubeGroups?.connections || [] }),
            validateCubes: window.soulcrestCubeGroups.validate, debugState, init, show,`);
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../src/Soulcrest.App/wwwroot/js/soulcrest-cube-groups.js'), 'utf8'), context);
    vm.runInNewContext(source, context);
    const api = context.window.soulcrestMap;
    api.init('map', JSON.stringify({ maps: ['altgard', 'next'].map(id => ({ id, size: 1000, refZoom: 0,
        minZoom: 0, maxZoom: 4, maxNativeZoom: 3, tiles: 'tiles' })) }), '/data/', { invokeMethodAsync() {} });
    return { api, map, data, containerClasses };
}

const groupLines = api => api.cubeState().connectionLayers.flatMap(item => item.layer.getLayers()).filter(layer => layer.kind === 'line');
const foreground = api => groupLines(api).filter(line => line.options.weight === 2);

test('explicit groups connect every existing cube only to its own group and retain their icons', async () => {
    const { api, data } = fixture();
    const info = JSON.parse(await api.show('altgard', 'en'));
    const state = api.cubeState();
    assert.equal(info.markers, data.markers.length);
    assert.equal(state.markers.length, data.markers.length);
    assert.equal(state.categories[0].getLayers().filter(layer => layer.kind === 'marker').length, 5);
    assert.equal(groupLines(api).length, 6); // one dark outline and one coloured line per undirected edge
    for (const source of data.hiddenCubeGroups) {
        const lines = foreground(api).filter(line => line.options.color === source.color);
        const memberAt = new Map(source.markers.map(index => [JSON.stringify([-data.markers[index][2], data.markers[index][1]]), index]));
        const graph = new Map(source.markers.map(index => [index, []]));
        for (const line of lines) {
            const ends = line.at.map(at => memberAt.get(JSON.stringify(at)));
            assert(ends.every(index => index !== undefined), 'both endpoints must belong to the same explicit group');
            graph.get(ends[0]).push(ends[1]); graph.get(ends[1]).push(ends[0]);
            assert.equal(line.options.interactive, false);
            assert.equal(line.options.dashArray, undefined);
        }
        const reached = new Set(), pending = [source.markers[0]];
        while (pending.length) {
            const index = pending.pop();
            if (reached.has(index)) continue;
            reached.add(index); pending.push(...graph.get(index).filter(next => !reached.has(next)));
        }
        assert.equal(reached.size, source.markers.length);
        for (const index of source.markers) {
            const icon = state.markers[index].options.icon.options;
            assert.match(icon.className, /hidden-cube-marker/);
            assert(icon.html.includes('--cube-group-color:' + source.color));
        }
    }
    assert(!state.markers[5].options.icon.options.className.includes('hidden-cube-marker'));
});

test('cube lines require both independently selected categories and preserve layer and icon identity', async () => {
    const { api, map, containerClasses } = fixture();
    await api.show('altgard', 'en');
    const original = groupLines(api), icon = api.cubeState().markers[0].options.icon;
    assert(original.every(line => !map.hasLayer(line)));
    api.setCategoryVisible(3, true);
    assert(original.every(line => !map.hasLayer(line)), 'groups alone must not reveal the cube layer');
    assert(!containerClasses.has('show-cube-groups'));
    api.setCategoryVisible(0, true);
    assert(original.every(line => map.hasLayer(line)));
    assert(containerClasses.has('show-cube-groups'));
    api.setCategoryVisible(0, false);
    assert(original.every(line => !map.hasLayer(line)));
    assert(map.hasLayer(api.cubeState().categories[3]), 'the Cube Groups selection is preserved');
    api.setCategoryVisible(0, true);
    assert(original.every(line => map.hasLayer(line)));
    api.setCategoryVisible(3, false);
    assert(original.every(line => !map.hasLayer(line)));
    assert(map.hasLayer(api.cubeState().markers[0]), 'cubes stay visible without group guides');
    assert(!containerClasses.has('show-cube-groups'));
    api.setCategoryVisible(3, true);
    assert.deepEqual(groupLines(api), original);
    assert.equal(api.cubeState().markers[0].options.icon, icon);
});

test('target changes, marker focus and language changes leave group connections intact', async () => {
    const { api, map } = fixture();
    await api.show('altgard', 'en');
    api.setCategoryVisible(0, true);
    api.setCategoryVisible(3, true);
    const original = groupLines(api);
    api.setTargets(JSON.stringify([{ id: 'cube', map: 'altgard', x: 100, y: 100, name: 'Cube', color: '#ffffff' }]));
    api.setTargets('[]');
    api.focusMonster('Monster');
    api.clearFocus();
    api.setLanguage('de');
    assert.deepEqual(groupLines(api), original);
    assert(original.every(line => map.hasLayer(line)));
    const popup = api.cubeState().markers[0].popup();
    assert.match(popup, /Gruppe 1/);
    assert.match(popup, /keine Spawnreihenfolge/);
    api.setLanguage('en');
    assert.match(api.cubeState().markers[0].popup(), /Group 1/);
    assert.match(api.cubeState().markers[0].popup(), /no spawn order/);
    api.setCategoryVisible(0, false);
    api.setTargets(JSON.stringify([{ id: 'cube', map: 'altgard', x: 100, y: 100, name: 'Cube', color: '#ffffff' }]));
    assert(original.every(line => !map.hasLayer(line)), 'a selected target must not reveal hidden group lines');
});

test('map changes remove previous connections and old packages retain the original plain cube icons', async () => {
    const { api, map } = fixture();
    await api.show('altgard', 'en');
    api.setCategoryVisible(0, true);
    api.setCategoryVisible(3, true);
    const previous = groupLines(api);
    await api.show('next', 'en');
    api.setCategoryVisible(0, true);
    assert(previous.every(line => !map.hasLayer(line)));
    assert.equal(groupLines(api).length, 0);
    assert(!api.cubeState().markers[0].options.icon.options.className.includes('hidden-cube-marker'));
    assert(!api.cubeState().markers[0].popup().includes('Group 1'));
    await api.show('altgard', 'en');
    assert.equal(groupLines(api).length, 6);
    assert(groupLines(api).every(line => !map.hasLayer(line)));
});

const invalidCases = [
    ['a foreign edge', data => { data.hiddenCubeGroups[0].connections.push([2, 3]); }, ['b']],
    ['overlapping membership', data => { data.hiddenCubeGroups[1].markers.unshift(2); data.hiddenCubeGroups[1].connections.unshift([2, 3]); }, []],
    ['a non-cube marker', data => { data.hiddenCubeGroups[0].markers.push(5); data.hiddenCubeGroups[0].connections.push([2, 5]); }, ['b']],
    ['a mismatched marker group', data => { data.markers[0][7].hiddenCubeGroup = 'b'; }, ['b']],
    ['a disconnected member', data => { data.hiddenCubeGroups[0].connections.pop(); }, ['b']],
    ['a duplicate edge', data => { data.hiddenCubeGroups[0].connections.push([1, 0]); }, ['b']],
    ['a duplicate group id', data => { data.hiddenCubeGroups[1].id = 'a'; }, []],
    ['a duplicate group number', data => { data.hiddenCubeGroups[1].number = 1; }, []],
    ['an invalid colour', data => { data.hiddenCubeGroups[0].color = 'red;opacity:0'; }, ['b']],
    ['a fractional index', data => { data.hiddenCubeGroups[0].markers[0] = 0.5; }, ['b']],
    ['a duplicate member', data => { data.hiddenCubeGroups[0].markers.push(1); }, ['b']],
    ['groups occupying the same point', data => { data.markers[3][1] = 100; data.markers[3][2] = 100; }, []],
    ['two members occupying the same point', data => { data.markers[1][1] = 100; data.markers[1][2] = 100; }, ['b']]
];
for (const [name, change, retained] of invalidCases) {
    test('invalid groups are rejected completely: ' + name, async () => {
        const data = payload(); change(data);
        const { api } = fixture(data);
        await api.show('altgard', 'en');
        assert.deepEqual(plain(api.cubeState().groups.map(group => group.id)), retained);
        const validColours = new Set(data.hiddenCubeGroups.filter(group => retained.includes(group.id)).map(group => group.color));
        assert(foreground(api).every(line => validColours.has(line.options.color)));
    });
}

test('optional marker metadata and singleton groups remain valid without inventing connections', () => {
    const data = payload();
    for (const marker of data.markers) marker[7] = null;
    data.hiddenCubeGroups[0] = { id: 'a', number: 1, color: '#a78bfa', markers: [0], connections: [] };
    const { api } = fixture(data);
    assert.deepEqual(plain(api.validateCubes(data).map(group => group.id)), ['a', 'b']);
});

test('metadata without the separate Cube Groups category never draws guides', async () => {
    const data = payload();
    data.categories.pop(); data.groups[0].categories = [0];
    const { api, containerClasses } = fixture(data);
    await api.show('altgard', 'en');
    api.setCategoryVisible(0, true);
    assert.equal(groupLines(api).length, 0);
    assert(!containerClasses.has('show-cube-groups'));
    assert.match(api.cubeState().markers[0].popup(), /Group 1/);
});
