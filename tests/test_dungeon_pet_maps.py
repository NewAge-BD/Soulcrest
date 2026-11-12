import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

import cv2
import numpy as np


SPEC = importlib.util.spec_from_file_location('dungeon_pet_maps', Path(__file__).resolve().parents[1] / 'scripts/dungeon_pet_maps.py')
module = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(module)


class DungeonPetMapPackagingTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        (self.root / 'data/pets').mkdir(parents=True)
        images = self.root / 'data/maps/dungeon-images'
        images.mkdir(parents=True)
        _, encoded = cv2.imencode('.png', np.zeros((20, 32, 3), dtype=np.uint8))
        self.image = encoded.tobytes()
        (images / 'example.png').write_bytes(self.image)
        self.catalog = {'schemaVersion': 1, 'maps': [{
            'id': 'example', 'mapId': 600001, 'en': 'Synthetic dungeon',
            'image': 'dungeon-maps/example.png', 'width': 32, 'height': 20,
            'imageSha256': hashlib.sha256(self.image).hexdigest(),
            'sourceRefs': ['https://example.invalid/map'],
            'spawns': [{'id': 'source-one', 'npcId': 2300107, 'petNumber': 1028,
                        'x': 3.5, 'y': 9, 'sourceRefs': ['https://example.invalid/placement']}]}]}
        sources = {'pets': [{'petNumber': 1028, 'sources': [{'mapIds': [600001],
                    'npcs': [{'id': 2300107, 'mapIds': [600001]}]}]}]}
        (self.root / 'data/pets/dungeon-soul-sources.json').write_text(json.dumps(sources), encoding='utf-8')
        self.save()

    def save(self):
        (self.root / 'data/pets/dungeon-map-pins.json').write_text(json.dumps(self.catalog), encoding='utf-8')

    def test_copies_exact_product_images_and_binds_catalog_to_package_fingerprint(self):
        prepared = module.prepare_dungeon_pet_maps(self.root)
        output = self.root / 'package'
        summary = module.write_dungeon_pet_maps(output, prepared)
        self.assertEqual((output / 'dungeon-maps/example.png').read_bytes(), self.image)
        self.assertEqual(json.loads((output / 'dungeon-maps.json').read_bytes()), self.catalog)
        self.assertEqual((summary['mapCount'], summary['petCount'], summary['spawnCount']), (1, 1, 1))
        original = summary['fingerprintSha256']
        self.catalog['maps'][0]['spawns'][0]['x'] = 5
        self.save()
        self.assertNotEqual(module.prepare_dungeon_pet_maps(self.root)[2]['fingerprintSha256'], original)
        self.assertFalse(list(output.rglob('*.tmp')))

    def test_shared_images_do_not_merge_distinct_variant_placements(self):
        second = copy.deepcopy(self.catalog['maps'][0])
        second['id'] = 'second-variant'
        second['spawns'][0]['x'] = 12
        self.catalog['maps'].append(second)
        self.save()
        raw, images, summary = module.prepare_dungeon_pet_maps(self.root)
        self.assertEqual(len(images), 1)
        self.assertEqual(summary['mapCount'], 2)
        self.assertEqual(summary['spawnCount'], 2)
        self.assertEqual(json.loads(raw)['maps'][1]['spawns'][0]['x'], 12)

    def test_missing_corrupt_or_wrong_sized_images_fail_before_packaging(self):
        image = self.root / 'data/maps/dungeon-images/example.png'
        image.unlink()
        with self.assertRaises(FileNotFoundError):
            module.prepare_dungeon_pet_maps(self.root)
        image.write_bytes(b'not an image')
        with self.assertRaisesRegex(ValueError, 'checksum'):
            module.prepare_dungeon_pet_maps(self.root)
        image.write_bytes(self.image)
        self.catalog['maps'][0]['width'] = 31
        self.save()
        with self.assertRaisesRegex(ValueError, 'dimensions'):
            module.prepare_dungeon_pet_maps(self.root)

    def test_unqualified_source_and_outside_or_missing_coordinates_cannot_enter_package(self):
        for key, value in [('npcId', 999999), ('petNumber', 999999), ('x', -1), ('y', 21), ('x', None), ('y', True)]:
            with self.subTest(key=key, value=value):
                previous = self.catalog['maps'][0]['spawns'][0][key]
                self.catalog['maps'][0]['spawns'][0][key] = value
                self.save()
                with self.assertRaises(ValueError):
                    module.prepare_dungeon_pet_maps(self.root)
                self.catalog['maps'][0]['spawns'][0][key] = previous

    def test_paths_duplicates_and_inconsistent_shared_image_metadata_fail_closed(self):
        original = copy.deepcopy(self.catalog)
        for path in ['../outside.png', '/dungeon-maps/example.png', 'https://example.invalid/map.png']:
            self.catalog = copy.deepcopy(original)
            self.catalog['maps'][0]['image'] = path
            self.save()
            with self.assertRaises(ValueError):
                module.prepare_dungeon_pet_maps(self.root)
        self.catalog = copy.deepcopy(original)
        self.catalog['maps'][0]['spawns'].append(copy.deepcopy(self.catalog['maps'][0]['spawns'][0]))
        self.save()
        with self.assertRaises(ValueError):
            module.prepare_dungeon_pet_maps(self.root)
        self.catalog = copy.deepcopy(original)
        second = copy.deepcopy(self.catalog['maps'][0])
        second['id'], second['width'] = 'other', 16
        self.catalog['maps'].append(second)
        self.save()
        with self.assertRaisesRegex(ValueError, 'shared'):
            module.prepare_dungeon_pet_maps(self.root)


if __name__ == '__main__':
    unittest.main()
