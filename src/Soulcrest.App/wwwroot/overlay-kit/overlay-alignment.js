/* Grindcrest overlay-editor-kit 1.15.0; source 231a2f0d40dcba872a2a83aa86b8f7b7a1edf49d. */
(function () {
    'use strict';

    const EPSILON = 0.0001;
    // A repeated gap should describe neighboring cards, not empty canvas space.
    const MAX_GAP = 128;
    const clamp = (value, min, max) => Math.min(max, Math.max(min, value));
    const number = (value, fallback) => Number.isFinite(value) ? value : fallback;
    const round = value => Math.round(value * 1000) / 1000;
    const end = (rect, axis) => rect[axis] + rect[axis === 'x' ? 'width' : 'height'];
    const cross = axis => axis === 'x' ? 'y' : 'x';
    const size = axis => axis === 'x' ? 'width' : 'height';
    const overlap = (a, b, axis) => Math.min(end(a, axis), end(b, axis)) - Math.max(a[axis], b[axis]);
    const intersects = (a, b) => overlap(a, b, 'x') > EPSILON && overlap(a, b, 'y') > EPSILON;

    function gapGuide(a, b, axis) {
        const other = cross(axis);
        const coordinate = (Math.max(a[other], b[other]) + Math.min(end(a, other), end(b, other))) / 2;
        const from = end(a, axis), to = b[axis];
        return axis === 'x'
            ? { kind: 'gap', x1: from, y1: coordinate, x2: to, y2: coordinate, value: round(to - from) }
            : { kind: 'gap', x1: coordinate, y1: from, x2: coordinate, y2: to, value: round(to - from) };
    }

    function alignmentGuide(active, peer, axis, coordinate) {
        const other = cross(axis);
        const from = Math.min(active[other], peer[other]);
        const to = Math.max(end(active, other), end(peer, other));
        return axis === 'x'
            ? { kind: 'align', x1: coordinate, y1: from, x2: coordinate, y2: to }
            : { kind: 'align', x1: from, y1: coordinate, x2: to, y2: coordinate };
    }

    function dimensionGuide(rect, axis) {
        const other = cross(axis), coordinate = rect[other] + rect[size(other)] / 2;
        return axis === 'x'
            ? { kind: 'align', x1: rect.x, y1: coordinate, x2: end(rect, axis), y2: coordinate }
            : { kind: 'align', x1: coordinate, y1: rect.y, x2: coordinate, y2: end(rect, axis) };
    }

    function snap(options) {
        const input = options.rect || {};
        const bounds = {
            width: Math.max(1, number(options.bounds?.width, 1600)),
            height: Math.max(1, number(options.bounds?.height, 1200))
        };
        const rect = {
            x: number(input.x, 0), y: number(input.y, 0),
            width: clamp(number(input.width, 1), 1, bounds.width),
            height: clamp(number(input.height, 1), 1, bounds.height)
        };
        const resize = options.mode === 'resize';
        if (resize) {
            rect.x = clamp(rect.x, 0, bounds.width - 1);
            rect.y = clamp(rect.y, 0, bounds.height - 1);
        }
        const tolerance = Math.max(0, number(options.tolerance, 8));
        const grid = Math.max(0, number(options.grid, 0));
        const peers = (options.peers || []).filter(peer =>
            [peer.x, peer.y, peer.width, peer.height].every(Number.isFinite) && peer.width > 0 && peer.height > 0
        ).slice().sort((a, b) => a.x - b.x || a.y - b.y || a.width - b.width || a.height - b.height || String(a.id || '').localeCompare(String(b.id || '')));

        function axisCandidates(axis) {
            const dimension = size(axis), other = cross(axis);
            const property = resize ? dimension : axis;
            const min = resize ? Math.min(bounds[dimension] - rect[axis], Math.max(1, number(options.minimum?.[dimension], 1))) : 0;
            const max = resize ? bounds[dimension] - rect[axis] : bounds[dimension] - rect[dimension];
            const candidates = [];
            let order = 0;
            function add(value, kind, guides, references = []) {
                const distance = Math.abs(value - rect[property]);
                if (value < min - EPSILON || value > max + EPSILON || distance > tolerance + EPSILON) return;
                candidates.push({ value: clamp(value, min, max), kind, distance,
                    // Prefer equal gaps when their distances differ by less than one pixel.
                    score: distance - (kind === 'gap' ? Math.min(0.75, tolerance / 8) : 0),
                    guides, references, order: order++ });
            }

            for (const peer of peers) {
                if (resize) {
                    for (const anchor of [peer[axis], end(peer, axis)]) {
                        add(anchor - rect[axis], 'align', active => [alignmentGuide(active, peer, axis, anchor)]);
                    }
                    add(peer[dimension], 'align', active => [dimensionGuide(active, axis), dimensionGuide(peer, axis)]);
                } else {
                    for (const anchor of [0, 0.5, 1]) {
                        const coordinate = peer[axis] + peer[dimension] * anchor;
                        add(coordinate - rect[dimension] * anchor, 'align', active => [alignmentGuide(active, peer, axis, coordinate)]);
                    }
                    // Edges can also meet without overlapping (e.g. a seamless HUD).
                    add(end(peer, axis), 'align', active => [alignmentGuide(active, peer, axis, end(peer, axis))]);
                    add(peer[axis] - rect[dimension], 'align', active => [alignmentGuide(active, peer, axis, peer[axis])]);
                }
            }

            const lane = peers.filter(peer => overlap(rect, peer, other) > EPSILON)
                .sort((a, b) => a[axis] - b[axis] || end(a, axis) - end(b, axis));
            for (let first = 0; first < lane.length; first++) {
                const a = lane[first];
                for (let second = first + 1; second < lane.length; second++) {
                    const b = lane[second];
                    if (overlap(a, b, other) <= EPSILON) continue;
                    const space = b[axis] - end(a, axis);
                    if (space <= EPSILON) continue;
                    // Ignore non-neighbor pairs, including cards in another lane
                    // that happen to overlap a tall/wide active card.
                    const obstructed = lane.some(peer => peer !== a && peer !== b &&
                        peer[axis] >= end(a, axis) - EPSILON && end(peer, axis) <= b[axis] + EPSILON &&
                        overlap(peer, a, other) > EPSILON && overlap(peer, b, other) > EPSILON);
                    if (obstructed) continue;

                    if (!resize) {
                        const middleGap = (space - rect[dimension]) / 2;
                        if (middleGap >= 0 && middleGap <= MAX_GAP) {
                            add(end(a, axis) + middleGap, 'gap', active => [gapGuide(a, active, axis), gapGuide(active, b, axis)], [a, b]);
                        }
                        if (space <= MAX_GAP) {
                            add(end(b, axis) + space, 'gap', active => [gapGuide(a, b, axis), gapGuide(b, active, axis)], [a, b]);
                            add(a[axis] - space - rect[dimension], 'gap', active => [gapGuide(active, a, axis), gapGuide(a, b, axis)], [a, b]);
                        }
                    } else {
                        if (space <= MAX_GAP) {
                            add(a[axis] - space - rect[axis], 'gap', active => [gapGuide(active, a, axis), gapGuide(a, b, axis)], [a, b]);
                        }
                        const leadingGap = rect[axis] - end(a, axis);
                        if (leadingGap >= 0 && leadingGap <= MAX_GAP && rect[axis] < b[axis]) {
                            add(b[axis] - leadingGap - rect[axis], 'gap', active => [gapGuide(a, active, axis), gapGuide(active, b, axis)], [a, b]);
                        }
                    }
                }
            }

            candidates.sort((a, b) => a.score - b.score || a.distance - b.distance || a.value - b.value || a.order - b.order);
            // Equivalent positions should not multiply the combination search.
            const distinct = candidates.filter((candidate, index) => !candidates.slice(0, index).some(previous =>
                Math.abs(previous.value - candidate.value) < EPSILON && previous.kind === candidate.kind));
            const value = clamp(grid > 0 ? Math.round(rect[property] / grid) * grid : rect[property], min, max);
            distinct.push({ value, kind: null, score: tolerance + 2, distance: Math.abs(value - rect[property]), guides: () => [], references: [], order: order++ });
            return distinct;
        }

        const horizontal = axisCandidates('x'), vertical = axisCandidates('y');
        const combinations = [];
        for (const x of horizontal) for (const y of vertical) combinations.push({ x, y, score: x.score + y.score });
        combinations.sort((a, b) => a.score - b.score || a.x.order - b.x.order || a.y.order - b.y.order);
        for (const combination of combinations) {
            const { x, y } = combination;
            const result = resize
                ? { ...rect, width: x.value, height: y.value }
                : { ...rect, x: x.value, y: y.value };
            if (x.kind || y.kind) {
                if (peers.some(peer => intersects(result, peer))) continue;
                if (x.references.some(peer => overlap(result, peer, 'y') <= EPSILON) ||
                    y.references.some(peer => overlap(result, peer, 'x') <= EPSILON)) continue;
            }
            const guides = [...x.guides(result), ...y.guides(result)].map(guide => Object.fromEntries(
                Object.entries(guide).map(([key, value]) => [key, typeof value === 'number' ? round(value) : value])));
            return { x: round(result.x), y: round(result.y), width: round(result.width), height: round(result.height), guides };
        }
    }

    window.soulcrestOverlayAlignment = { snap };
})();
