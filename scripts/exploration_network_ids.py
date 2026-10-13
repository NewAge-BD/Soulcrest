"""Exact local map-object/quest joins for passive exploration recognition.

Dungeon completion IDs require a direct dungeon target and a matching quest instance;
they are independent of the quest ID and are never derived from it arithmetically.
Stronghold instances use a separate field and require a direct stronghold quest target.
"""
from collections import defaultdict
import re


def _positive_i32(value):
    if type(value) is int:
        return value if 0 < value <= 2147483647 else None
    if isinstance(value, str) and re.fullmatch(r'[1-9][0-9]{0,9}', value):
        number = int(value)
        return number if number <= 2147483647 else None
    return None


def _region_instances(quest):
    regions = quest.get('regions', [])
    if isinstance(regions, list):
        for region in regions:
            if isinstance(region, dict) and isinstance(region.get('instance'), dict):
                yield region['instance'].get('id')


def _single_stronghold_instance(quest):
    regions = quest.get('regions', [])
    if not isinstance(regions, list):
        return None
    instances = set()
    for region in regions:
        if not isinstance(region, dict):
            return None
        if 'instance' not in region:
            continue
        instance = region['instance']
        if not isinstance(instance, dict) or (value := _positive_i32(instance.get('id'))) is None:
            return None
        instances.add(value)
    return next(iter(instances)) if len(instances) == 1 else None


def point_network_ids(points, map_details, quests):
    objects = {str(o['id']): o for o in map_details.get('objects', [])}
    by_quest = {str(q['id']): q for q in quests if q.get('kind') == 'exploration'}
    by_instance = defaultdict(set)
    for quest in by_quest.values():
        for instance in _region_instances(quest):
            if instance is not None:
                by_instance[str(instance)].add(int(quest['id']))
    # Keep the existing quest joins; stronger completion metadata additionally
    # checks every Exploration quest record, including duplicate/conflicting IDs.
    exploration_records = defaultdict(list)
    exploration_instances = defaultdict(set)
    for quest in quests:
        if quest.get('kind') != 'exploration':
            continue
        quest_id = _positive_i32(quest.get('id'))
        exploration_records[quest_id].append(quest)
        for raw_instance in _region_instances(quest):
            if (instance := _positive_i32(raw_instance)) is not None:
                exploration_instances[instance].add(quest_id)
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
            # user request 2026-10-13: Strongholds have explicit completion
            # instances, separate from sealed dungeons. Never borrow object
            # targets, infer IDs, or select among multiple Exploration quests.
            direct_target = (point.get('target') or {}).get('id')
            match = re.fullmatch(r'stronghold-([1-9][0-9]{0,9})', direct_target) if isinstance(direct_target, str) else None
            quest_id = _positive_i32(match[1]) if match else None
            records = exploration_records.get(quest_id, [])
            if quest_id is not None and len(records) == 1:
                instance = _single_stronghold_instance(records[0])
                if instance is not None and exploration_instances[instance] == {quest_id}:
                    extra['strongholdInstanceId'] = instance
        elif 'sealed-dungeon' in kinds and len(by_instance.get(suffix, set())) == 1:
            extra['questId'] = next(iter(by_instance[suffix]))
            # Preserve the quest join above, but never borrow a dungeon target from
            # another object or infer an instance from a quest-number pattern.
            direct_target = (point.get('target') or {}).get('id')
            match = re.fullmatch(r'dungeon-([1-9][0-9]*)', direct_target) if isinstance(direct_target, str) else None
            if match and match[1] == suffix and len(match[1]) <= 10 and int(match[1]) <= 2147483647:
                extra['dungeonId'] = int(match[1])
        if extra:
            result[str(point['id'])] = extra
    return result
