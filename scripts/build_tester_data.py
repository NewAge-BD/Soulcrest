"""Build Soulcrest's offline map package solely from cached public gaming.tools data.

Fetch separately with fetch_gamingtools_dataset.py. This builder never uses the network.
"""
import argparse
import collections
import json
import math
from pathlib import Path
import re
import shutil
import struct
import time

import cv2
import numpy as np
from fetch_gamingtools import decode
from pet_source_markers import add_pet_source_markers, source_groups

ROOT = Path(__file__).resolve().parents[1]
GAMINGTOOLS = ROOT / 'imports/gamingtools'
MAPS = {'1000': 'poeta', '1010': 'verteron', '1011': 'elthen', '1100': 'ishalgen',
        '1110': 'altgard', '1111': 'morheim', '20': 'abyss-reshanta-a',
        '22': 'chaotic-middle-reshanta', '23': 'chaotic-upper-reshanta'}
GENUS = {'intellect': 'cogni', 'feral': 'fera', 'nature': 'natura', 'trans': 'varian', 'special': 'special'}
GROUPS = {'regions': 'Regions', 'exploration': 'Locations', 'collectables': 'Collectibles',
          'locations': 'Locations', 'services': 'City Services', 'npcs': 'NPCs',
          'monsters': 'Monsters', 'gatherables': 'Resources'}


def load_json(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))


def data(kind, entity_id=None, locale='en'):
    name = kind + ('/' + str(entity_id) if entity_id else '') + '.d.json'
    return decode(load_json(GAMINGTOOLS / 'cdn/data' / locale / name))


def slug(name):
    return re.sub('[^a-z0-9]+', '-', name.lower()).strip('-')


class IconStore:
    def __init__(self, out_dir):
        self.out_dir = Path(out_dir)
        self.copied = {}

    def get(self, path):
        if not path:
            return None
        if path in self.copied:
            return self.copied[path]
        source = GAMINGTOOLS / 'assets' / path.lstrip('/')
        if not source.exists():
            source = GAMINGTOOLS / 'portraits' / Path(path).name
        if not source.exists():
            raise FileNotFoundError('Image not cached: ' + path)
        image = cv2.imread(str(source), cv2.IMREAD_UNCHANGED)
        if image is None:
            raise ValueError('Invalid image: ' + str(source))
        # Preserve source subdirectories: equally named files must never collide.
        relative = 'icons/gamingtools/' + path.removeprefix('/images/').removesuffix('.webp') + '.png'
        target = self.out_dir / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        if not cv2.imwrite(str(target), image):
            raise OSError('Cannot write ' + str(target))
        self.copied[path] = relative
        return relative

    def get_file(self, name):
        return self.get('/images/ui/resource/texture/portrait/portrait_vehicle/' + name) if name else None


def read_tile_pack(path):
    blob = Path(path).read_bytes()
    if len(blob) < 48:
        raise ValueError('Truncated GTPK header')
    magic, version, count, tile_size, minimum, maximum, width, height, entry_size, offset, _, _ = struct.unpack_from('<12I', blob)
    if magic != 0x4b505447 or version != 2 or entry_size != 24 or tile_size != 512:
        raise ValueError('Unsupported GTPK header')
    if offset < 48 or offset + count * entry_size > len(blob):
        raise ValueError('Invalid GTPK index')
    entries = []
    keys = set()
    for index in range(count):
        z, x, y, start, length = struct.unpack_from('<B3xIIQI', blob, offset + index * entry_size)
        if (z > maximum or start < offset + count * entry_size or start + length > len(blob)
                or (z, x, y) in keys or x >= 2**z or y >= 2**z):
            raise ValueError('Invalid GTPK tile entry')
        keys.add((z, x, y))
        image = cv2.imdecode(np.frombuffer(blob[start:start+length], dtype=np.uint8), cv2.IMREAD_COLOR)
        if image is None or image.shape[:2] != (512, 512):
            raise ValueError('Invalid GTPK image')
        entries.append((z, x, y, blob[start:start+length]))
    return {'width': width, 'height': height, 'maxZoom': maximum, 'minZoom': minimum}, entries


def write_tiles(path, directory):
    metadata, entries = read_tile_pack(path)
    directory.mkdir(parents=True, exist_ok=True)
    for z, x, y, encoded in entries:
        image = cv2.imdecode(np.frombuffer(encoded, dtype=np.uint8), cv2.IMREAD_COLOR)
        # 512 px source tiles become four 256 px tiles at z+1, retaining all detail.
        for dx in range(2):
            for dy in range(2):
                tile = image[dy*256:(dy+1)*256, dx*256:(dx+1)*256]
                cv2.imwrite(str(directory / f'{z+1}_{2*x+dx}_{2*y+dy}.webp'), tile,
                            [cv2.IMWRITE_WEBP_QUALITY, 101])
        if z == 0:
            cv2.imwrite(str(directory / '0_0_0.webp'), cv2.resize(image, (256, 256), interpolation=cv2.INTER_AREA),
                        [cv2.IMWRITE_WEBP_QUALITY, 101])
    return metadata


def to_pixel(config, x, y):
    if config.get('transformType', 'original') != 'original':
        raise ValueError('Unimplemented coordinate transform: ' + config['transformType'])
    bounds = config['bounds']
    width, height = config['imageWidth'], config['imageHeight']
    dx, dy = bounds['maxX'] - bounds['minX'], bounds['maxY'] - bounds['minY']
    if min(dx, dy) <= 0:
        raise ValueError('Invalid map bounds')
    scale = min(width / dx, height / dy)
    return (width - dx*scale)/2 + (x-bounds['minX'])*scale, (height-dy*scale)/2 + (y-bounds['minY'])*scale


# Visible, clickable credit asked for by gaming.tools when allowing the data use (Discord 2026-10-06).
ATTRIBUTION = ('Daten und Karte: <a href="https://aion2.gaming.tools/" target="_blank" rel="noopener">aion2.gaming.tools</a>'
               ' · Grafiken © NCSOFT')


def meters_per_pixel(config, size):
    """Game metres per map pixel: world units are centimetres (checked in game 2026-10-06 on Altgard:
    260 px to the NPC Ninir showed 258 m, 1001 px showed 998 m)."""
    bounds = config['bounds']
    dx, dy = bounds['maxX'] - bounds['minX'], bounds['maxY'] - bounds['minY']
    scale = min(config['imageWidth'] / dx, config['imageHeight'] / dy)
    logical = size / (512 * 2 ** config['maximumZoom'])
    return round(1 / (scale * logical) / 100, 6)


def name_kibelisks_by_village(payload, radius=10):
    """A Kibelisk whose name is not unique on its map takes the name of a village label right next to it.
    Altgard has "Steel Hammer Temporary Trading Post" twice; the game lists the one at 5407/3467 as
    "42. Shulak Street Stall" (user screenshots 2026-10-06), and the village label of that name is 4 px
    away. The other one keeps its name (its village label of the same name is 27 px away)."""
    names = [c['name'] for c in payload['categories']]
    if 'Kibelisk' not in names or 'Village' not in names: return
    kibelisk, village = names.index('Kibelisk'), names.index('Village')
    places = [m for m in payload['markers'] if m[0] == kibelisk]
    labels = [m for m in payload['markers'] if m[0] == village]
    for m in places:
        if sum(1 for o in places if o[3] == m[3]) < 2 or not labels: continue
        label = min(labels, key=lambda l: (l[1]-m[1])**2 + (l[2]-m[2])**2)
        if ((label[1]-m[1])**2 + (label[2]-m[2])**2) ** .5 <= radius and label[3] and label[3] != m[3]:
            m[3], m[4] = label[3], label[4]


def soul_drops(npc, pet_by_name):
    """Only explicit Soul loot, never NPC name suffixes or unverified portrait similarity."""
    found = {}
    for drop in npc.get('loot', []):
        match = re.fullmatch(r'Soul: (.+) \(Bound\)', drop.get('item', {}).get('name', ''))
        if match and match[1] in pet_by_name:
            numeric_id = pet_by_name[match[1]]
            found[numeric_id] = {'npcId': npc['id'], 'npc': npc['name'], 'petId': numeric_id,
                                 'pet': match[1], 'itemId': drop['item']['id'],
                                 'chancePercent': [w.get('chancePercent') for w in drop.get('windows', [])],
                                 'sourceRefs': ['https://aion2.gaming.tools/npcs/' + npc['id']]}
    return list(found.values())


def build_soul_sources(pet_summaries):
    by_name = {p['name']: p['id'] for p in pet_summaries}
    result, conflicts = {}, []
    # Existing explicit Abyss facts, including transparently marked same-name derivations.
    for entry in load_json(ROOT / 'data/pets/abyss-soul-sources.json')['sources']:
        row = {**entry, 'petId': str(entry['petId'])}
        result[str(entry['npcId'])] = row
    tamed = collections.defaultdict(set)
    for pet in load_json(ROOT / 'data/pets/tamed-from.json')['pets']:
        for nid in pet['npcIds']:
            tamed[nid].add(pet['petId'])
    for nid, pet_ids in tamed.items():
        npc = data('npcs', nid)
        drops = soul_drops(npc, by_name)
        if len(drops) > 1 or (drops and {d['petId'] for d in drops} != pet_ids):
            conflicts.append({'npcId': nid, 'lootPets': [d['petId'] for d in drops],
                              'tamedPets': sorted(pet_ids), 'sourceRefs': ['https://aion2.gaming.tools/npcs/'+nid]})
            result.pop(nid, None)
        elif drops:
            result[nid] = drops[0]
    # Infer only exact-name matches with a single agreed explicit loot result, and mark the origin.
    by_npc_name = collections.defaultdict(list)
    for row in result.values():
        if not row.get('derivedFrom'):
            by_npc_name[row['npc']].append(row)
    conflict_ids = {c['npcId'] for c in conflicts}
    for mid in MAPS:
        for group in data('interactive-map', mid)['groups']:
            entity = group.get('entity', {})
            nid = entity.get('id')
            if entity.get('mainCategoryId') != 'npcs' or nid in result or nid in conflict_ids:
                continue
            detail_path = GAMINGTOOLS/'cdn/data/en/npcs'/f'{nid}.d.json'
            if not detail_path.exists() or data('npcs',nid).get('loot'):
                continue
            origins = by_npc_name.get(entity.get('name'), [])
            if origins and len({p['petId'] for p in origins}) == 1:
                result[nid] = {**origins[0], 'npcId': nid, 'derivedFrom': [o['npcId'] for o in origins],
                               'sourceRefs': ['https://aion2.gaming.tools/npcs/'+nid] +
                                             [ref for o in origins for ref in o['sourceRefs']]}
    return result, conflicts


def build_catalog(icons):
    summaries = data('pets')
    german = {p['id']: p for p in data('pets', locale='de')}
    identities = load_json(ROOT / 'data/pets/catalog-identities.json')['pets']
    pets, numeric_to_catalog = {}, {}
    for p in summaries:
        ids = identities.get(p['id'], {}).get('catalogIds', [slug(p['name'])])
        preferred = identities.get(p['id'], {}).get('primaryCatalogId')
        primary = preferred if preferred in ids else ids[0]
        numeric_to_catalog[p['id']] = primary
        for catalog_id in ids:
            pets[catalog_id] = {'id': catalog_id, 'en': p['name'], 'de': german.get(p['id'], {}).get('name'),
                               'genus': GENUS[p['genus']['code']], 'iconName': Path(p['iconPath']).stem,
                               'icon': icons.get(p['iconPath']), 'spawns': {}, 'monsters': {},
                               'sourceRefs': ['https://aion2.gaming.tools/pets/'+p['id']],
                               'numericId': p['id'], 'legacyDuplicate': catalog_id != primary}
    return summaries, pets, numeric_to_catalog


def boss_loot(npc_id, icons):
    """Published item pool, unique by exact item ID. No probabilities inferred or combined."""
    base = GAMINGTOOLS / 'cdn/data'
    if not (base / 'en/npcs' / f'{npc_id}.d.json').exists():
        return None  # unknown is different from a source-confirmed empty pool
    npc = data('npcs', npc_id)
    if not isinstance(npc.get('loot'), list):
        return None
    translated = data('npcs', npc_id, 'de') if (base / 'de/npcs' / f'{npc_id}.d.json').exists() else {}
    german = {r['item']['id']: r['item'].get('name') for r in translated.get('loot', []) if r.get('item', {}).get('id')}
    pool = {}
    for row in npc['loot']:
        item = row.get('item', {})
        if not item.get('id') or not item.get('name') or item['id'] in pool:
            continue
        pool[item['id']] = {'id': item['id'], 'en': item['name'], 'de': german.get(item['id']),
                            'icon': icons.get(item.get('iconPath')), 'rarity': item.get('rarity')}
    return list(pool.values())


def build_map(mid, definition, icons, pets, numeric_to_catalog, sources):
    en, de = data('interactive-map', mid), data('interactive-map', mid, 'de')
    index, de_index = data('interactive-map'), data('interactive-map', locale='de')
    types = {t['id']: (GROUPS[c['id']], t) for c in index['categories'] for t in c.get('types', [])}
    de_types = {t['id']: t for c in de_index['categories'] for t in c.get('types', [])}
    de_points = {p['id']: p for p in de['points'] + de['labels']}
    de_entities = {g['entity']['id']: g['entity'] for g in de['groups'] if g.get('entity')}
    payload = {'id': MAPS[mid], 'label': definition['name'], 'refZoom': 5 if mid == '1110' else 4,
               'size': 8192 if mid == '1110' else 4096, 'groups': [], 'categories': [],
               'icons': [], 'markers': [], 'regions': []}
    cats = {}
    def icon_index(path):
        if not path: return -1
        rel = icons.get(path)
        if rel not in payload['icons']: payload['icons'].append(rel)
        return payload['icons'].index(rel)
    def append(type_id, x, y, name, name_de, icon_path=None, pet=None, extra=None):
        group_name, t = types.get(type_id, ('NPCs', {'name': type_id}))
        if type_id not in cats:
            cats[type_id] = len(payload['categories'])
            payload['categories'].append({'name': t.get('name') or type_id, 'de': de_types.get(type_id, {}).get('name'),
                'group': group_name, 'icon': icon_index(t.get('iconPath')), 'hidden': False, 'count': 0})
            group = next((g for g in payload['groups'] if g['name'] == group_name), None)
            if group is None:
                group = {'name': group_name, 'categories': []}; payload['groups'].append(group)
            group['categories'].append(cats[type_id])
        cat = cats[type_id]
        payload['categories'][cat]['count'] += 1
        px, py = to_pixel(definition['map'], x, y)
        logical_scale = payload['size'] / (512*2**definition['map']['maximumZoom'])
        px, py = px*logical_scale, py*logical_scale
        payload['markers'].append([cat, round(px, 3), round(py, 3), name, name_de,
                                   icon_index(icon_path), pet, extra])
    def choose_type(type_ids):
        # Rank and genus are facets of one NPC, not separate duplicate markers.
        return next((t for t in type_ids if t.startswith('monster-kind-')), type_ids[0] if type_ids else 'npc-other')
    for point in en['points']:
        translated = de_points.get(point['id'], {})
        append(choose_type(point['types']), point['x'], point['y'],
               point.get('name') or point.get('entity', {}).get('name'),
               translated.get('name') or translated.get('entity', {}).get('name'), point.get('iconPath'))
    for label in en['labels']:
        append(label['type'], label['x'], label['y'], label.get('name'), de_points.get(label['id'], {}).get('name'))
    for group in en['groups']:
        entity = group.get('entity', {})
        row = sources.get(entity.get('id')) if entity.get('mainCategoryId') == 'npcs' else None
        pet = numeric_to_catalog.get(str(row['petId'])) if row else None
        type_id = choose_type(group['types'])
        positions = group['positions']
        if len(positions) % 2: raise ValueError('Invalid placement pairs')
        # Resource kinds (Gem: Sapphire, Diamond, Ruby) carry their item icon; the legend lists them.
        kind_icon = group.get('iconPath') if entity.get('mainCategoryId') == 'gatherables' else None
        for i in range(0, len(positions), 2):
            append(type_id, positions[i], positions[i+1], entity.get('name'),
                   de_entities.get(entity.get('id'), {}).get('name'), kind_icon, pet=pet,
                   extra={'npcId': entity.get('id'), 'derivedFrom': row.get('derivedFrom')} if row else None)
    name_kibelisks_by_village(payload)
    # Network boss lists use named-spawn IDs, not NPC IDs. Join only by the source's exact entity ID.
    spawn_refs = collections.defaultdict(set)
    for spawn in data('maps', mid).get('namedSpawns', []):
        spawn_refs[str(spawn['npc']['id'])].add(int(spawn['id']))
    spawn_ids = {npc: next(iter(ids)) for npc, ids in spawn_refs.items() if len(ids) == 1}
    payload['bosses'] = []
    for point in en['points']:
        entity = point.get('entity', {})
        spawn_id = spawn_ids.get(str(entity.get('id')))
        if 'boss' not in point.get('types', []) or spawn_id is None:
            continue
        translated = de_points.get(point['id'], {}).get('entity', {})
        px, py = to_pixel(definition['map'], point['x'], point['y'])
        logical_scale = payload['size'] / (512*2**definition['map']['maximumZoom'])
        payload['bosses'].append({'spawnId': spawn_id, 'npcId': int(entity['id']),
            'en': entity['name'], 'de': translated.get('name'),
            'x': round(px*logical_scale, 3), 'y': round(py*logical_scale, 3),
            'icon': icons.get(entity.get('iconPath')), 'loot': boss_loot(entity['id'], icons),
            'sourceRefs': ['https://aion2.gaming.tools/npcs/' + entity['id']]})
    payload['sourceMapId'] = int(mid)
    # A spawn with several published positions is ambiguous. Never pick an arbitrary location.
    boss_counts = collections.Counter(b['spawnId'] for b in payload['bosses'])
    payload['bosses'] = [b for b in payload['bosses'] if boss_counts[b['spawnId']] == 1]
    add_pet_source_markers(payload, pets, cluster=0.03)
    for marker in payload['markers']:
        if marker[6]:
            field = 'spawns' if payload['categories'][marker[0]]['group'] == 'Pets' else 'monsters'
            values = pets[marker[6]][field]; values[MAPS[mid]] = values.get(MAPS[mid], 0)+1
    return payload


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path, default=ROOT/'imports/generated/mapdata')
    parser.add_argument('--skip-tiles', action='store_true')
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    icons = IconStore(args.out)
    summaries, pets, numeric = build_catalog(icons)
    sources, conflicts = build_soul_sources(summaries)
    manifest = {'version': time.strftime('%Y%m%d%H%M%S'), 'maps': [], 'source': 'https://aion2.gaming.tools/map'}
    german_maps = {m['id']:m for m in data('interactive-map', locale='de')['maps']}
    for definition in data('interactive-map')['maps']:
        mid=definition['id']
        if mid not in MAPS: continue
        payload=build_map(mid,definition,icons,pets,numeric,sources)
        directory=args.out/MAPS[mid]; directory.mkdir(parents=True,exist_ok=True)
        if not args.skip_tiles:
            write_tiles(GAMINGTOOLS/'cdn'/definition['map']['tilePackPath'].lstrip('/'),directory/'tiles')
        # Loot is only needed by Boss Rush. Keep it out of Leaflet/tracking's frequently read map file.
        map_payload = {**payload, 'bosses': [{k: v for k, v in boss.items() if k != 'loot'} for boss in payload['bosses']]}
        (directory/'data.js').write_text('window.SoulcrestMaps = window.SoulcrestMaps || {};\nwindow.SoulcrestMaps['+
            json.dumps(MAPS[mid])+'] = '+json.dumps(map_payload,ensure_ascii=False,separators=(',',':'))+';\n',encoding='utf-8')
        (directory/'bosses.json').write_text(json.dumps({'mapId': int(mid), 'bosses': payload['bosses']},
            ensure_ascii=False, indent=1), encoding='utf-8')
        manifest['maps'].append({'id':MAPS[mid],'label':german_maps.get(mid,definition)['name'],
            'group':{'elyos':'Elyos','asmodians':'Asmodier','asmodian':'Asmodier','abyss':'Abyss'}.get(definition['group'],definition['groupName']),
            'tiles':MAPS[mid]+'/tiles/{z}_{x}_{y}.webp','maxNativeZoom':definition['map']['maximumZoom']+1,
            'refZoom':payload['refZoom'],'size':payload['size'],'minZoom':0,'maxZoom':payload['refZoom']+2,
            'attribution':ATTRIBUTION,
            'sourceMapId':mid,'calibrationResidualPixels':definition['map'].get('calibrationResidualPixels'),
            'calibrationVerified':definition['map'].get('calibrationVerified',False),
            'metersPerPixel':meters_per_pixel(definition['map'],payload['size'])})
        print(MAPS[mid],len(payload['markers']),flush=True)
    for pet in pets.values():
        if pet.get('legacyDuplicate'):
            primary = pets[numeric[pet['numericId']]]
            pet['spawns'], pet['monsters'] = dict(primary['spawns']), dict(primary['monsters'])
    for name,obj in [('manifest.json',manifest),('pets.json',sorted(pets.values(),key=lambda p:(p['genus'],p['en'],p['id']))),
                     ('source-audit.json',{'sources':list(sources.values()),'conflicts':conflicts})]:
        (args.out/name).write_text(json.dumps(obj,ensure_ascii=False,indent=1),encoding='utf-8')
    print('Pets',len(pets),'soul sources',len(sources),'conflicts',len(conflicts),flush=True)

if __name__ == '__main__': main()
