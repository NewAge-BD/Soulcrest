import sys
import unittest
from unittest.mock import patch
import tempfile
import cv2
import numpy as np
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
from fetch_gamingtools import decode, static_path, PublicCache
from prepare_gamingtools_portraits import convert_portrait

class FetchGamingtoolsTests(unittest.TestCase):
    def test_only_static_cdn_paths(self):
        # Permission 2026-10-06: the d.json files and the files they point to, no website pages.
        for path in ['/data/en/pets.d.json', '/tiles/world_d_a.gtpk', '/images/a.webp']:
            self.assertEqual(static_path(path), path)
        for path in ['/maps/1110', '/images/../x.webp', '/data/en/pets.d.json?x=1', '//evil/x']:
            with self.assertRaises(ValueError):
                static_path(path)

    def test_devalue_shared_references_and_undefined(self):
        result = decode([{'left': 1, 'right': 1, 'missing': -1}, [2], 'value'])
        self.assertIs(result['left'], result['right'])
        self.assertEqual(result['left'], ['value'])
        self.assertIsNone(result['missing'])

    def test_plain_public_response(self):
        self.assertEqual(decode({'/maps/1110': 0}), {'/maps/1110': 0})

class WebpTests(unittest.TestCase):
    def test_png_preserves_decoded_pixels_and_alpha(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            pixels = np.zeros((8, 9, 4), dtype=np.uint8)
            pixels[2:6, 2:7] = [20, 50, 200, 128]
            source, target = root / 'pet.webp', root / 'pet.png'
            cv2.imwrite(str(source), pixels, [cv2.IMWRITE_WEBP_QUALITY, 101])
            self.assertEqual(convert_portrait(source, target), (9, 8))
            self.assertTrue(np.array_equal(cv2.imread(str(source), -1), cv2.imread(str(target), -1)))

    def test_importer_reads_new_webp_cache(self):
        import build_tester_data
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            source = root / 'assets/images/ui/resource/texture/portrait/portrait_vehicle/pet.webp'
            source.parent.mkdir(parents=True)
            cv2.imwrite(str(source), np.full((8, 9, 4), 128, dtype=np.uint8))
            store = build_tester_data.IconStore(root / 'output')
            with patch.object(build_tester_data, 'GAMINGTOOLS', root):
                result = store.get_file('pet.webp')
            self.assertEqual(result, 'icons/gamingtools/ui/resource/texture/portrait/portrait_vehicle/pet.png')
            self.assertTrue(np.array_equal(cv2.imread(str(source), -1),
                                          cv2.imread(str(root / 'output' / result), -1)))

    def test_cached_webp_needs_no_request_and_rejects_traversal(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            cache_dir = root / 'raw'
            cache_dir.mkdir()
            image = root / 'assets/images/pet.webp'
            image.parent.mkdir(parents=True)
            image.write_bytes(b'cached')
            with patch('fetch_gamingtools.CACHE', cache_dir):
                cache = PublicCache()
                with patch.object(cache, 'request', side_effect=AssertionError('network')):
                    self.assertEqual(cache.image('/images/pet.webp'), image)
                    with self.assertRaises(ValueError):
                        cache.image('/images/../pet.webp')
                    with self.assertRaises(ValueError):
                        cache.image('/images/pet.png')

    def test_requests_go_to_the_cdn_only(self):
        with tempfile.TemporaryDirectory() as folder, patch('fetch_gamingtools.CACHE', Path(folder) / 'raw'):
            cache = PublicCache()
            seen = []
            class Response:
                def __enter__(self): return self
                def __exit__(self, *args): return False
                def read(self): return b'RIFF\0\0\0\0WEBP'
            class Opener:
                def open(self, request, timeout):
                    seen.append(request.full_url)
                    return Response()
            with patch('fetch_gamingtools.urllib.request.build_opener', return_value=Opener()), patch('fetch_gamingtools.time.sleep'):
                cache.image('/images/ui/pet.webp')
            self.assertEqual(seen, ['https://cdn-hosted.gaming.tools/aion2/images/ui/pet.webp'])

if __name__ == '__main__':
    unittest.main()
