"""Exact local map-object/quest joins for passive exploration recognition."""
from collections import defaultdict


def point_network_ids(points, map_details, quests):
    objects = {str(o['id']): o for o in map_details.get('objects', [])}
    by_quest = {str(q['id']): q for q in quests if q.get('kind') == 'exploration'}
    by_instance = defaultdict(set)
    for quest in by_quest.values():
        for region in quest.get('regions', []):
            instance = region.get('instance', {}).get('id')
            if instance is not None:
                by_instance[str(instance)].add(int(quest['id']))
    result = {}
    for point in points:
        kinds = set(point.get('types', []))
        if not kinds.intersection({'sealed-dungeon', 'stronghold', 'teleportartifact'}):
            continue
        obj = objects.get(str(point['id']).removeprefix('object-'), {})
        target = point.get('target') or obj.get('target') or {}
        suffix = str(target.get('id', '')).split('-', 1)[-1]
        extra = {}
        if 'teleportartifact' in kinds:
            value = str(obj.get('teleport', {}).get('id', ''))
            if value.isdecimal() and int(value) > 0:
                extra['teleportId'] = int(value)
        elif 'stronghold' in kinds and suffix in by_quest:
            extra['questId'] = int(suffix)
        elif 'sealed-dungeon' in kinds and len(by_instance.get(suffix, set())) == 1:
            extra['questId'] = next(iter(by_instance[suffix]))
        if extra:
            result[str(point['id'])] = extra
    return result
