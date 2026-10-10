"""Build quest monster markers offline from verified station assignments and gaming.tools NPC IDs.

The station input joins the reviewed episode audit with the reviewed quest/step audit.
No fuzzy name matching or instance-to-world projection is used for monster positions.
"""
from pathlib import Path
import argparse, json, collections

ROOT = Path(__file__).resolve().parents[1]
MAPS = {'1110': ('altgard', 8192), '1010': ('verteron', 4096)}

def read(path): return json.loads(path.read_text(encoding='utf-8-sig'))

def pixel(config, size, x, y):
    b = config['bounds']; dx, dy = b['maxX']-b['minX'], b['maxY']-b['minY']
    width, height = config['imageWidth'], config['imageHeight']
    scale = min(width/dx, height/dy); logical = size/(512*2**config['maximumZoom'])
    return round(((width-dx*scale)/2+(x-b['minX'])*scale)*logical, 3), round(((height-dy*scale)/2+(y-b['minY'])*scale)*logical, 3)

def build(root, stations, output):
    cache = root/'imports/gamingtools/cdn/data'
    definitions = {d['id']: d['map'] for d in read(cache/'en/interactive-map.json')['maps']}
    maps = {mid: read(cache/f'en/interactive-map/{mid}.json') for mid in MAPS}
    result = []; missing = []; seen = set()
    for station in stations:
        qid, order = station.get('QuestId'), station.get('Step')
        if not qid or order is None: continue
        q = read(cache/f'en/quests/{qid}.json')
        de_path = cache/f'de/quests/{qid}.json'; de = read(de_path) if de_path.exists() else {}
        step = next((s for s in q.get('steps', []) if s['order'] == order), {})
        spawns = []
        for objective in step.get('objectives', []):
            if objective.get('kind') != 'killnpc': continue
            mid = str(objective.get('map', {}).get('id'))
            if mid not in MAPS: continue  # never project a dungeon NPC onto its parent world
            map_id, size = MAPS[mid]
            if station['MapId'] != map_id: continue
            for target in objective.get('targets', []):
                if target.get('mainCategoryId') != 'npcs': continue
                npc = str(target['id']); count = objective.get('count')
                if not isinstance(count, int) or count <= 0: continue
                groups = [g for g in maps[mid]['groups'] if str(g.get('entity', {}).get('id')) == npc
                          and any(t.startswith('monster-') for t in g.get('types', []))]
                if not groups: missing.append(dict(quest=qid, npc=npc, map=mid)); continue
                german = next((t.get('name') for s in de.get('steps', []) for o in s.get('objectives', []) for t in o.get('targets', []) if str(t.get('id')) == npc), None)
                coords = set()
                for group in groups:
                    path = group.get('iconPath') or '/images/ui/resource/sprite/hud_minimap/ut_hud_minimap_monster_aggressive_sprite.webp'
                    icon = 'icons/gamingtools/'+path.removeprefix('/images/').removesuffix('.webp')+'.png'
                    if not (output/icon).exists():
                        import cv2
                        source = root/'imports/gamingtools/assets'/path.lstrip('/')
                        image = cv2.imread(str(source), cv2.IMREAD_UNCHANGED) if source.exists() else None
                        if image is None: raise FileNotFoundError('Monster symbol is not cached: '+str(source))
                        (output/icon).parent.mkdir(parents=True, exist_ok=True)
                        if not cv2.imwrite(str(output/icon), image): raise OSError('Could not write monster symbol')
                    p = group['positions']; assert len(p) % 2 == 0
                    for i in range(0, len(p), 2):
                        x, y = pixel(definitions[mid], size, p[i], p[i+1])
                        if (x,y) in coords: continue
                        coords.add((x,y))
                        spawns.append(dict(MapId=map_id, X=x, Y=y, NpcId=npc, Name=target['name'], NameDe=german, Count=count, Icon=icon))
        if not spawns: continue
        key = (station['RouteId'], station['StopIndex']); assert key not in seen; seen.add(key)
        result.append(dict(station, Spawns=spawns, Source=f'https://aion2.gaming.tools/quests/{qid}'))
    output.mkdir(parents=True, exist_ok=True)
    (output/'leveling-objectives.json').write_text(json.dumps(result, ensure_ascii=False, separators=(',', ':'))+'\n', encoding='utf-8')
    return dict(stations=len(result), spawnMarkers=sum(len(s['Spawns']) for s in result),
                quests=len({s['QuestId'] for s in result}), missing=missing)

if __name__ == '__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--stations', type=Path, default=ROOT/'imports/generated/leveling-stations.json')
    parser.add_argument('--out', type=Path, default=ROOT/'imports/generated/mapdata')
    args=parser.parse_args()
    print(json.dumps(build(ROOT, read(args.stations), args.out), ensure_ascii=False))
