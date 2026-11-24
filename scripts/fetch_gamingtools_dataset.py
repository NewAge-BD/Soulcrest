"""Cache static, public map data and pet evidence used by the gaming.tools website."""
import argparse
import json
from pathlib import Path
from fetch_gamingtools import PublicCache, CACHE
from monster_drops import is_material, item_records, material_item_ids

MAP_IDS = ['1000','1010','1011','1100','1110','1111','20','22','23']

def fetch_monster_materials(cache):
    """Cache all published material details, including each variant's own sources."""
    indexes = {locale: cache.data('items', locale=locale) for locale in ['en', 'de']}
    ids = material_item_ids(indexes['en'])
    icon_paths = set()
    for index, item_id in enumerate(ids):
        for locale in ['en', 'de']:
            detail = cache.data('items', item_id, locale)
            if str(detail.get('id')) != item_id:
                raise ValueError('Item detail identity mismatch: ' + item_id)
            icon_paths.update(row['iconPath'] for row in item_records(detail)
                              if is_material(row) and row.get('iconPath'))
        print('material', index + 1, len(ids), item_id, flush=True)
    return icon_paths


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--materials-only', action='store_true')
    parser.add_argument('--maps', nargs='+', choices=MAP_IDS,
                        help='Fetch only these maps and their referenced marker images')
    parser.add_argument('--refresh', action='store_true',
                        help='Refresh selected map indexes and map data; cached images remain reusable')
    args = parser.parse_args()
    if args.refresh and not args.maps:
        parser.error('--refresh requires --maps')
    if args.maps and args.materials_only:
        parser.error('--maps and --materials-only are separate fetch modes')
    cache = PublicCache()
    if args.maps:
        fetch_maps(cache, args.maps, refresh=args.refresh)
        return
    if args.materials_only:
        paths = fetch_monster_materials(cache)
        for index, path in enumerate(sorted(paths)):
            cache.image(path)
            print('material icon', index + 1, len(paths), flush=True)
        return
    root = CACHE.parent
    for locale in ['en', 'de']:
        index = cache.data('interactive-map', locale=locale)
        pets = cache.data('pets', locale=locale)
        print(locale, 'index', len(index['maps']), 'pets',len(pets),flush=True)
        for mid in MAP_IDS:
            cache.data('interactive-map', mid, locale)
            cache.data('maps', mid, locale)
            print(locale,'map',mid,flush=True)
    for m in index['maps']:
        if m['id'] in MAP_IDS:
            path=cache.cdn(m['map']['tilePackPath'])
            print('tiles',m['id'],path.stat().st_size,flush=True)
    facts=json.loads((Path(__file__).resolve().parents[1]/'data/pets/tamed-from.json').read_text(encoding='utf-8'))
    ids=sorted({nid for p in facts['pets'] for nid in p['npcIds']})
    for i,nid in enumerate(ids):
        cache.data('npcs',nid)
        print('npc',i+1,len(ids),nid,flush=True)
    boss_ids = {p['entity']['id'] for mid in MAP_IDS for p in cache.data('interactive-map', mid)['points']
                if 'boss' in p.get('types', []) and p.get('entity', {}).get('id')}
    boss_loot_icons = set()
    for i, nid in enumerate(sorted(boss_ids)):
        npc = cache.data('npcs', nid)
        cache.data('npcs', nid, 'de')
        boss_loot_icons.update(row['item']['iconPath'] for row in npc.get('loot', []) if row.get('item', {}).get('iconPath'))
        print('boss loot', i+1, len(boss_ids), nid, flush=True)
    # Portraits and marker icons use the same public image cache.
    icon_paths={p['iconPath'] for p in pets} | boss_loot_icons | fetch_monster_materials(cache)
    for locale in ['en']:
        index=cache.data('interactive-map',locale=locale)
        for category in index['categories']:
            for t in category.get('types',[]):
                if t.get('iconPath'):icon_paths.add(t['iconPath'])
        for mid in MAP_IDS:
            data=cache.data('interactive-map',mid)
            for point in data['points']:
                if point.get('iconPath'):icon_paths.add(point['iconPath'])
                # Boss schedule/alerts use the published NPC portrait, not the generic map symbol.
                if 'boss' in point.get('types', []) and point.get('entity', {}).get('iconPath'):
                    icon_paths.add(point['entity']['iconPath'])
            # Item icons of the resource kinds (Gem: Sapphire, Diamond, Ruby) for the legend sub-entries.
            for group in data['groups']:
                if group.get('entity',{}).get('mainCategoryId')=='gatherables' and group.get('iconPath'):
                    icon_paths.add(group['iconPath'])
    for i,path in enumerate(sorted(icon_paths)):
        cache.image(path)
        print('icon',i+1,len(icon_paths),flush=True)

def fetch_maps(cache, map_ids, *, refresh=False):
    """Targeted patch update, without fetching unrelated pets, NPCs or materials."""
    icon_paths = set()
    for locale in ['en', 'de']:
        index = cache.data('interactive-map', locale=locale, refresh=refresh)
        definitions = {str(row['id']): row for row in index['maps']}
        missing = set(map_ids) - definitions.keys()
        if missing:
            raise ValueError('Selected maps are absent from the published index: ' + ', '.join(sorted(missing)))
        for category in index['categories']:
            icon_paths.update(t['iconPath'] for t in category.get('types', []) if t.get('iconPath'))
        for mid in dict.fromkeys(map_ids):
            markers = cache.data('interactive-map', mid, locale, refresh=refresh)
            details = cache.data('maps', mid, locale, refresh=refresh)
            if str(markers.get('id')) != mid or str(details.get('id')) != mid:
                raise ValueError('Map detail identity mismatch: ' + mid)
            if locale == 'en':
                cache.cdn(definitions[mid]['map']['tilePackPath'], refresh=refresh)
            icon_paths.update(point['iconPath'] for point in markers['points'] if point.get('iconPath'))
            icon_paths.update(point['entity']['iconPath'] for point in markers['points']
                              if 'boss' in point.get('types', []) and point.get('entity', {}).get('iconPath'))
            icon_paths.update(group['iconPath'] for group in markers['groups']
                              if group.get('entity', {}).get('mainCategoryId') == 'gatherables' and group.get('iconPath'))
            print(locale, 'map', mid, definitions[mid]['name'], flush=True)
    for path in sorted(icon_paths):
        cache.image(path)
    print('marker images', len(icon_paths), flush=True)


if __name__=='__main__': main()
