"""Offline monster-material joins from the public gaming.tools item data.

Only an item's own drop acquisitions qualify it. Variants never inherit a parent
item's sources. Exact NPC IDs bind placements; a published map further limits them.
"""
from dataclasses import dataclass


# User request 2026-11-02: material families are UI groups, not name heuristics.
# These exact identities were checked against the public item index/details.
MATERIAL_TYPES = {
    '610512002': 'Meat', '610531001': 'Meat',
    '610531002': 'Fruit', '610530011': 'Leather',
    '610530001': 'Refining Stones', '610530005': 'Refining Stones',
    '610530008': 'Refining Stones', '610530010': 'Refining Stones',
    '610530014': 'Spiritstones',
    '600590001': 'Balaur Materials', '610511013': 'Balaur Materials',
    '610511014': 'Balaur Materials', '610511015': 'Balaur Materials',
    '630512002': 'Meat', '630531001': 'Meat', '630531002': 'Fruit',
    '630530001': 'Refining Stones', '630530002': 'Refining Stones',
    '630530003': 'Refining Stones', '630530004': 'Refining Stones',
    '630511014': 'Balaur Materials', '630511015': 'Balaur Materials',
}

# User request 2026-11-02: expose only these four families. Other published
# material identities remain source facts, but never enter map/drop references.
VISIBLE_MATERIAL_TYPES = frozenset({'Meat', 'Fruit', 'Leather', 'Spiritstones'})


def material_item_ids(index):
    """All published material categories, without relying on ID/name patterns."""
    if not isinstance(index, list):
        raise ValueError('Expected the gaming.tools item index')
    return sorted({str(row['id']) for row in index
                   if isinstance(row, dict) and row.get('id') and is_material(row)})


def is_material(item):
    return any(isinstance(category, str) and category.startswith('items/materials/')
               for category in item.get('categories', []))


def item_records(detail):
    """Yield each independent published item/variant without inheriting fields."""
    if not isinstance(detail, dict):
        raise ValueError('Expected a gaming.tools item detail')
    yield detail
    for variant in detail.get('variants', []):
        yield from item_records(variant)


def drop_sources(item, map_ids):
    for acquisition in item.get('acquisition', []):
        if acquisition.get('method') != 'drop':
            continue
        source = acquisition.get('source') or {}
        location = acquisition.get('location') or {}
        map_info = location.get('map') or {}
        npc_id = str(source.get('id') or '')
        map_id = str(map_info['id']) if map_info.get('id') is not None else None
        if source.get('mainCategoryId') == 'npcs' and npc_id and (map_id is None or map_id in map_ids):
            yield map_id, npc_id


@dataclass
class MonsterDropCatalog:
    items: dict
    sources: dict


def load_monster_drop_catalog(load_data, map_ids):
    """Preflight every material detail before the builder writes any map files."""
    map_ids = {str(value) for value in map_ids}
    items, sources = {}, {}
    for item_id in material_item_ids(load_data('items')):
        detail = load_data('items', item_id)
        translated = load_data('items', item_id, 'de')
        if str(detail.get('id')) != item_id or str(translated.get('id')) != item_id:
            raise ValueError('Item detail identity mismatch: ' + item_id)
        german = {str(row['id']): row for row in item_records(translated) if row.get('id')}
        for row in item_records(detail):
            if not row.get('id') or not is_material(row):
                continue
            row_id = str(row['id'])
            material_type = MATERIAL_TYPES.get(row_id)
            if material_type not in VISIBLE_MATERIAL_TYPES:
                continue
            own_sources = set(drop_sources(row, map_ids))
            if not own_sources:
                continue
            if not row.get('name'):
                raise ValueError('Missing material name: ' + row_id)
            item = {'id': row_id, 'en': row['name'], 'de': german.get(row_id, {}).get('name'),
                    'iconPath': row.get('iconPath'), 'type': material_type}
            if row_id in items and items[row_id] != item:
                raise ValueError('Conflicting material identity: ' + row_id)
            items[row_id] = item
            for source in own_sources:
                sources.setdefault(source, set()).add(row_id)
    return MonsterDropCatalog(items, sources)


def monster_drop_payload(map_id, marker_npc_ids, catalog, icons):
    """Reference existing marker indices; never add artificial spawn markers."""
    spawns, used = [], set()
    for marker, npc_id in sorted(marker_npc_ids.items()):
        source_ids = catalog.sources.get((str(map_id), str(npc_id)), set()) | \
                     catalog.sources.get((None, str(npc_id)), set())
        item_ids = sorted(item_id for item_id in source_ids
                          if catalog.items.get(item_id, {}).get('type') in VISIBLE_MATERIAL_TYPES)
        if item_ids:
            spawns.append({'marker': marker, 'npcId': str(npc_id), 'items': item_ids})
            used.update(item_ids)
    items = []
    for item_id in sorted(used):
        item = catalog.items[item_id]
        items.append({key: item[key] for key in ['id', 'en', 'de', 'type']} |
                     {'icon': icons.get(item['iconPath'])})
    return {'schemaVersion': 1, 'items': items, 'spawns': spawns}


def preflight_monster_drop_icons(catalog, gamingtools):
    """Validate source icons before the first map/icon output is changed."""
    from pathlib import Path
    import cv2
    for item in catalog.items.values():
        path = item.get('iconPath')
        if not path:
            continue
        source = Path(gamingtools) / 'assets' / path.lstrip('/')
        if not source.exists():
            source = Path(gamingtools) / 'portraits' / Path(path).name
        if not source.exists():
            raise FileNotFoundError('Material image not cached: ' + path)
        if cv2.imread(str(source), cv2.IMREAD_UNCHANGED) is None:
            raise ValueError('Invalid material image: ' + path)
