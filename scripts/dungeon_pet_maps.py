"""Package verified dungeon pet maps without changing the world-map manifest entries."""
import hashlib
import json
import math
import os
from pathlib import Path
import re
import tempfile

import cv2
import numpy as np


def prepare_dungeon_pet_maps(root):
    root = Path(root)
    raw = (root / 'data/pets/dungeon-map-pins.json').read_bytes().replace(b'\r\n', b'\n')
    catalog = json.loads(raw)
    sources = json.loads((root / 'data/pets/dungeon-soul-sources.json').read_text(encoding='utf-8-sig'))
    qualified = {(pet['petNumber'], npc['id'], map_id)
                 for pet in sources['pets'] for source in pet['sources']
                 for npc in source['npcs'] for map_id in npc['mapIds']}
    if catalog.get('schemaVersion') != 1 or not catalog.get('maps'):
        raise ValueError('Invalid dungeon pet map catalog')
    images, keys, pets, count = {}, set(), set(), 0
    for scene in catalog['maps']:
        key, relative = scene['id'], scene['image']
        if key in keys or not re.fullmatch(r'[a-z0-9]+(?:-[a-z0-9]+)*', key):
            raise ValueError('Invalid or duplicate dungeon map identity')
        keys.add(key)
        if not re.fullmatch(r'dungeon-maps/[a-z0-9][a-z0-9-]*\.(png|webp)', relative):
            raise ValueError('Invalid dungeon image path')
        size = scene['width'], scene['height']
        if any(type(value) is not int or value < 1 or value > 16384 for value in size):
            raise ValueError('Invalid dungeon image size')
        expected = scene['imageSha256']
        if not re.fullmatch(r'[0-9a-f]{64}', expected):
            raise ValueError('Invalid dungeon image checksum')
        if relative not in images:
            file = root / 'data/maps/dungeon-images' / Path(relative).name
            encoded = file.read_bytes()
            if hashlib.sha256(encoded).hexdigest() != expected:
                raise ValueError('Dungeon image checksum does not match catalog')
            decoded = cv2.imdecode(np.frombuffer(encoded, dtype=np.uint8), cv2.IMREAD_UNCHANGED)
            if decoded is None or (decoded.shape[1], decoded.shape[0]) != size:
                raise ValueError('Dungeon image dimensions do not match catalog')
            images[relative] = (encoded, expected, size)
        elif images[relative][1:] != (expected, size):
            raise ValueError('Inconsistent shared dungeon image')
        if not scene.get('spawns'):
            raise ValueError('Dungeon map has no proven placements')
        identities, positions = set(), set()
        for pin in scene['spawns']:
            identity = pin['id']
            position = pin['petNumber'], pin['npcId'], pin.get('x'), pin.get('y')
            if identity in identities or position in positions:
                raise ValueError('Duplicate dungeon placement')
            if (pin['petNumber'], pin['npcId'], scene['mapId']) not in qualified:
                raise ValueError('Dungeon placement has no matching Soul source')
            for value, extent in zip((pin.get('x'), pin.get('y')), size):
                if type(value) not in (int, float) or not math.isfinite(value) or not 0 <= value <= extent:
                    raise ValueError('Dungeon placement is outside its image')
            identities.add(identity)
            positions.add(position)
            pets.add(pin['petNumber'])
            count += 1
    digest = hashlib.sha256(raw).hexdigest()
    fingerprint = hashlib.sha256(json.dumps({'catalog': digest,
        'images': sorted((name, image[1]) for name, image in images.items())},
        sort_keys=True, separators=(',', ':')).encode()).hexdigest()
    descriptor = {'schemaVersion': 1, 'catalog': 'dungeon-maps.json', 'catalogSha256': digest,
                  'fingerprintSha256': fingerprint, 'mapCount': len(keys),
                  'petCount': len(pets), 'spawnCount': count}
    return raw, images, descriptor


def write_dungeon_pet_maps(directory, prepared):
    directory = Path(directory)
    raw, images, descriptor = prepared
    for relative, (encoded, _, _) in images.items():
        atomic_write(directory / relative, encoded)
    atomic_write(directory / descriptor['catalog'], raw)
    return descriptor


def atomic_write(path, content):
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=path.parent, prefix=path.name + '.', suffix='.tmp', delete=False) as temporary:
        temporary.write(content)
        name = temporary.name
    try:
        os.replace(name, path)
    finally:
        if os.path.exists(name):
            os.unlink(name)
