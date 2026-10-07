"""Cache static, public map data and pet evidence used by the gaming.tools website."""
import json
from pathlib import Path
from fetch_gamingtools import PublicCache, CACHE

MAP_IDS = ['1000','1010','1011','1100','1110','1111','20','22','23']

def main():
    cache = PublicCache()
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
    # Portraits and marker icons use the same public image cache.
    icon_paths={p['iconPath'] for p in pets}
    for locale in ['en']:
        index=cache.data('interactive-map',locale=locale)
        for category in index['categories']:
            for t in category.get('types',[]):
                if t.get('iconPath'):icon_paths.add(t['iconPath'])
        for mid in MAP_IDS:
            data=cache.data('interactive-map',mid)
            for point in data['points']:
                if point.get('iconPath'):icon_paths.add(point['iconPath'])
            # Item icons of the resource kinds (Gem: Sapphire, Diamond, Ruby) for the legend sub-entries.
            for group in data['groups']:
                if group.get('entity',{}).get('mainCategoryId')=='gatherables' and group.get('iconPath'):
                    icon_paths.add(group['iconPath'])
    for i,path in enumerate(sorted(icon_paths)):
        cache.image(path)
        print('icon',i+1,len(icon_paths),flush=True)

if __name__=='__main__': main()
