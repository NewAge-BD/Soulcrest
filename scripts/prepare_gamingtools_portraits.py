"""Prepare source pet portraits as PNG for Windows overlays, without changing the active map package."""
import json
from pathlib import Path
import shutil
import cv2
from fetch_gamingtools import CACHE, PublicCache


def convert_portrait(source, target):
    image = cv2.imread(str(source), cv2.IMREAD_UNCHANGED)
    if image is None or len(image.shape) != 3 or image.shape[2] not in (3, 4):
        raise ValueError('Unreadable portrait: ' + str(source))
    target.parent.mkdir(parents=True, exist_ok=True)
    if not cv2.imwrite(str(target), image):
        raise OSError('Cannot write portrait: ' + str(target))
    return image.shape[1], image.shape[0]


def main():
    root = CACHE.parent
    cache = PublicCache()
    data = {'pets': cache.data('pets')}
    report = []
    for pet in data['pets']:
        path = pet['iconPath']
        existing = root / 'portraits' / Path(path).name
        cached = root / 'assets' / path.lstrip('/')
        if existing.exists() and not cached.exists():
            cached.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(existing, cached)
        source = cache.image(path)
        target = root / 'prepared' / 'portraits' / (Path(path).stem + '.png')
        width, height = convert_portrait(source, target)
        report.append({'petId': pet['id'], 'name': pet['name'], 'source': path,
                       'png': str(target.relative_to(root)), 'width': width, 'height': height})
        (root / 'prepared' / 'portraits.json').write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding='utf-8')
        print(f"{len(report)}/{len(data['pets'])}: {pet['id']} {width}x{height}", flush=True)


if __name__ == '__main__':
    main()
