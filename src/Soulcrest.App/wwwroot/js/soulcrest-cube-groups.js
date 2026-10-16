// Offline cube-group presentation: validated membership and undirected flight guides.
window.soulcrestCubeGroups = (() => {
    // Local data defines membership. Distances are never used to invent a cube group. Reject every
    // ambiguous group, including both owners of an overlapping marker, instead of drawing part of it.
    function validatedCubeGroups(data) {
        if (!Array.isArray(data.hiddenCubeGroups)) return [];
        const rows = data.hiddenCubeGroups;
        const ids = new Map(), numbers = new Map(), owners = new Map(), positions = new Map();
        for (const group of rows) {
            if (!group || typeof group !== 'object') continue;
            ids.set(group.id, (ids.get(group.id) || 0) + 1);
            numbers.set(group.number, (numbers.get(group.number) || 0) + 1);
            for (const index of new Set(Array.isArray(group.markers) ? group.markers : [])) {
                owners.set(index, (owners.get(index) || 0) + 1);
                const marker = data.markers[index];
                if (!marker) continue;
                const key = marker[1] + ',' + marker[2];
                if (!positions.has(key)) positions.set(key, new Set());
                positions.get(key).add(group);
            }
        }
        const result = [];
        for (const group of rows) {
            if (!group || typeof group.id !== 'string' || !group.id.trim() || ids.get(group.id) !== 1
                || !Number.isInteger(group.number) || group.number < 1 || numbers.get(group.number) !== 1
                || typeof group.color !== 'string' || !/^#[0-9a-f]{6}$/i.test(group.color)
                || !Array.isArray(group.markers) || !group.markers.length || !Array.isArray(group.connections)) continue;
            const members = new Set(group.markers);
            if (members.size !== group.markers.length) continue;
            const distinctPositions = new Set(group.markers.map(index => { const marker = data.markers[index]; return marker && marker[1] + ',' + marker[2]; }));
            if (distinctPositions.size !== members.size) continue;
            const category = data.markers[group.markers[0]]?.[0];
            if (!group.markers.every(index => {
                const marker = data.markers[index];
                const cat = marker && data.categories[marker[0]];
                return Number.isInteger(index) && index >= 0 && index < data.markers.length && owners.get(index) === 1
                    && cat?.group === 'Collectibles' && cat.name === 'Hidden Cube' && marker[0] === category
                    && Number.isFinite(marker[1]) && Number.isFinite(marker[2])
                    && positions.get(marker[1] + ',' + marker[2]).size === 1
                    && (!marker[7] || !Object.hasOwn(marker[7], 'hiddenCubeGroup') || marker[7].hiddenCubeGroup === group.id);
            })) continue;
            const edges = new Set(), adjacent = new Map(group.markers.map(index => [index, []]));
            let valid = true;
            for (const edge of group.connections) {
                if (!Array.isArray(edge) || edge.length !== 2 || !edge.every(index => Number.isInteger(index) && members.has(index))
                    || edge[0] === edge[1]) { valid = false; break; }
                const key = Math.min(...edge) + ',' + Math.max(...edge);
                if (edges.has(key)) { valid = false; break; }
                edges.add(key);
                adjacent.get(edge[0]).push(edge[1]);
                adjacent.get(edge[1]).push(edge[0]);
            }
            if (!valid) continue;
            const visited = new Set(), pending = [group.markers[0]];
            while (pending.length) {
                const index = pending.pop();
                if (visited.has(index)) continue;
                visited.add(index);
                pending.push(...adjacent.get(index).filter(next => !visited.has(next)));
            }
            if (visited.size === members.size) result.push({ ...group, category });
        }
        return result;
    }

    function create(data, categoryLayers, categoryVisible, map, leaflet, toLatLng) {
        const groups = validatedCubeGroups(data);
        const byMarker = new Map(), connections = [];
        const categories = data.categories.map((category, index) => ({ category, index }))
            .filter(({ category }) => category.group === 'Collectibles' && category.name === 'Cube Groups');
        const connectionCategory = categories.length === 1 ? categories[0].index : -1;
        for (const group of groups) {
            for (const index of group.markers) byMarker.set(data.markers[index], group);
            if (connectionCategory < 0) continue;
            const layer = leaflet.layerGroup();
            for (const edge of group.connections) {
                const points = edge.map(index => toLatLng(data.markers[index][1], data.markers[index][2]));
                leaflet.polyline(points, { color: '#10151f', weight: 5, opacity: 0.6, interactive: false, lineCap: 'round' }).addTo(layer);
                leaflet.polyline(points, { color: group.color, weight: 2, opacity: 0.9, interactive: false, lineCap: 'round' }).addTo(layer);
            }
            connections.push({ layer, category: group.category });
        }
        function sync() {
            const classes = map?.getContainer().classList;
            if (connectionCategory < 0) { classes?.remove?.('show-cube-groups'); return; }
            classes?.toggle?.('show-cube-groups', categoryVisible[connectionCategory] && connections.some(item => categoryVisible[item.category]));
            const parent = categoryLayers[connectionCategory];
            for (const { layer, category } of connections) {
                if (categoryVisible[category]) parent.addLayer(layer);
                else parent.removeLayer(layer);
            }
        }
        sync();
        return { groups, connections, sync, groupFor: marker => byMarker.get(marker) };
    }

    return { validate: validatedCubeGroups, create };
})();
