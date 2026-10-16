"""Group Hidden Cubes by the nearest named place; build undirected flight guides offline.

Soulcrest's own grouping (user decision 2026-10-16): the active gaming.tools import lists the cube
positions without any grouping. Every cube joins the nearest Village or Hunting Ground label of the
same map, and the cubes of one place are connected by a deterministic minimum spanning tree over
their real spawn positions. The groups are areas for flying the cubes off, not the game's spawn
groups: nothing here says where the next cube appears.
"""
import math
import re


COLORS = ('#c084fc', '#5eead4', '#fbbf24', '#60a5fa', '#fb7185', '#a3e635',
          '#f472b6', '#38bdf8', '#fb923c', '#818cf8', '#34d399', '#e879f9')
PLACE_TYPES = {'Village', 'Hunting Ground'}


def minimum_connections(indices, markers):
    """A deterministic minimum spanning tree, with no direction or spawn order."""
    if not indices:
        return []
    remaining = set(indices)
    root = min(remaining)
    remaining.remove(root)
    distances = {i: (_distance(markers[root], markers[i]), root) for i in remaining}
    edges = []
    while remaining:
        child = min(remaining, key=lambda i: (*distances[i], i))
        parent = distances[child][1]
        edges.append([parent, child])
        remaining.remove(child)
        for i in remaining:
            candidate = (_distance(markers[child], markers[i]), child)
            if candidate < distances[i]:
                distances[i] = candidate
    return edges


def _distance(a, b):
    return (a[1] - b[1]) ** 2 + (a[2] - b[2]) ** 2


def _colors(groups, markers):
    # Colour adjacency is a presentation choice: neighbouring places get different colours.
    neighbours = {i: set() for i in range(len(groups))}
    for i, group in enumerate(groups):
        distances = []
        for j, other in enumerate(groups):
            if i == j:
                continue
            distance = min(_distance(markers[a], markers[b])
                           for a in group['markers'] for b in other['markers'])
            distances.append((distance, j))
        for _, j in sorted(distances)[:8]:
            neighbours[i].add(j)
            neighbours[j].add(i)
    assigned = {}
    while len(assigned) < len(groups):
        i = max((i for i in neighbours if i not in assigned), key=lambda i: (
            len({assigned[j] for j in neighbours[i] if j in assigned}), len(neighbours[i]), -i))
        used = {assigned[j] for j in neighbours[i] if j in assigned}
        available = next((color for color in COLORS if color not in used), None)
        if available is None:
            raise ValueError('Hidden Cube colour palette exhausted')
        assigned[i] = available
    return [assigned[i] for i in range(len(groups))]


def _slug(name):
    return re.sub(r'[^a-z0-9]+', '-', name.lower()).strip('-')


def _finite(*values):
    return all(isinstance(v, (int, float)) and not isinstance(v, bool) and math.isfinite(v) for v in values)


def apply_hidden_cube_groups(payload):
    """Adds payload['hiddenCubeGroups'] and the "Cube Groups" legend switch.

    Existing markers are never moved, removed or appended; only marker[7] gains the group ID.
    A map without Hidden Cubes or without named places stays unchanged.
    """
    categories, markers = payload['categories'], payload['markers']
    cubes = [i for i, m in enumerate(markers)
             if categories[m[0]]['group'] == 'Collectibles' and categories[m[0]]['name'] == 'Hidden Cube']
    places = [i for i, m in enumerate(markers)
              if categories[m[0]]['group'] == 'Regions' and categories[m[0]]['name'] in PLACE_TYPES
              and isinstance(m[3], str) and m[3].strip()]
    if not cubes or not places:
        return
    for i in cubes + places:
        if not _finite(markers[i][1], markers[i][2]):
            raise ValueError('Hidden Cube or place without a finite position')
    members = {}
    for cube in cubes:
        place = min(places, key=lambda p: (_distance(markers[cube], markers[p]), p))
        members.setdefault(place, []).append(cube)
    groups, seen = [], set()
    # Ordered by the place's map position (y, then x): stable numbers across builds.
    for place in sorted(members, key=lambda p: (markers[p][2], markers[p][1], p)):
        base = f"{payload['id']}-cubes-{_slug(markers[place][3]) or place}"
        group_id, suffix = base, 2
        while group_id in seen:
            group_id, suffix = f'{base}-{suffix}', suffix + 1
        seen.add(group_id)
        indices = sorted(members[place])
        groups.append({'id': group_id, 'number': len(groups) + 1, 'name': markers[place][3],
                       'nameDe': markers[place][4], 'markers': indices,
                       'connections': minimum_connections(indices, markers)})
    for group, color in zip(groups, _colors(groups, markers)):
        group['color'] = color
        for index in group['markers']:
            marker = markers[index]
            marker[7] = {**(marker[7] or {}), 'hiddenCubeGroup': group['id']}
    payload['hiddenCubeGroups'] = groups
    # User clarification 2026-10-16: a separate switch on every map surface,
    # subordinate to the Hidden Cube marker layer. This adds no map markers.
    cube_category = markers[cubes[0]][0]
    category_index = next((i for i, category in enumerate(categories)
                           if category['group'] == 'Collectibles' and category['name'] == 'Cube Groups'), None)
    if category_index is None:
        category_index = len(categories)
        categories.append({'group': 'Collectibles', 'name': 'Cube Groups', 'de': 'Cube Groups',
                           'icon': -1, 'hidden': False, 'count': len(groups)})
    legend = next(group for group in payload['groups'] if group['name'] == 'Collectibles')['categories']
    if category_index in legend:
        legend.remove(category_index)
    legend.insert(legend.index(cube_category) + 1, category_index)
