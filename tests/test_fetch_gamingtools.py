import json
import struct
import sys
import urllib.error
import unittest
from unittest.mock import call, patch
import tempfile
import cv2
import numpy as np
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
from fetch_gamingtools import decode, static_path, PublicCache, main
from fetch_gamingtools_dataset import fetch_maps
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

    def test_targeted_map_refresh_fetches_both_locales_one_tile_pack_and_only_referenced_assets(self):
        class Cache:
            def __init__(self):
                self.calls, self.tiles, self.images = [], [], []
            def data(self, kind, entity_id=None, locale='en', *, refresh=False):
                self.calls.append((kind, entity_id, locale, refresh))
                if entity_id is None:
                    return {'maps': [{'id': '23', 'name': 'Outer', 'map': {'tilePackPath': '/tiles/outer.gtpk'}}],
                            'categories': [{'types': [{'iconPath': '/images/category.webp'}]}]}
                if kind == 'maps':
                    return {'id': entity_id}
                return {'id': entity_id, 'points': [{'iconPath': '/images/point.webp'}],
                        'groups': [{'entity': {'mainCategoryId': 'gatherables'}, 'iconPath': '/images/herb.webp'}]}
            def cdn(self, path, *, refresh=False):
                self.tiles.append((path, refresh))
            def image(self, path):
                self.images.append(path)
        cache = Cache()
        fetch_maps(cache, ['23'], refresh=True)
        self.assertEqual(cache.calls, [(kind, mid, locale, True) for locale in ['en', 'de']
                                      for kind, mid in [('interactive-map', None), ('interactive-map', '23'), ('maps', '23')]])
        self.assertEqual(cache.tiles, [('/tiles/outer.gtpk', True)])
        self.assertEqual(cache.images, ['/images/category.webp', '/images/herb.webp', '/images/point.webp'])


    def test_targeted_refresh_rejects_an_absent_map(self):
        from unittest.mock import Mock
        cache = Mock()
        cache.data.return_value = {'maps': [], 'categories': []}
        with self.assertRaisesRegex(ValueError, 'absent'):
            fetch_maps(cache, ['23'], refresh=True)
        cache.cdn.assert_not_called()
        cache.image.assert_not_called()


class DataRefreshTests(unittest.TestCase):
    def test_refresh_replaces_only_the_selected_raw_and_decoded_pair(self):
        with tempfile.TemporaryDirectory() as folder, patch('fetch_gamingtools.CACHE', Path(folder) / 'raw'):
            root = Path(folder)
            raw = root / 'cdn/data/en/maps/23.d.json'
            decoded = raw.with_name('23.json')
            raw.parent.mkdir(parents=True)
            raw.write_bytes(b'{"name":"Old"}')
            decoded.write_bytes(b'{"name":"Old"}')
            untouched = root / 'cdn/data/de/maps/23.d.json'
            untouched.parent.mkdir(parents=True)
            untouched.write_bytes(b'other locale')
            other = raw.with_name('22.d.json')
            other.write_bytes(b'other map')
            content = b'[{"id":1,"name":2},"23","Chaotic Outer Reshanta"]'
            cache = PublicCache()
            with patch.object(cache, 'request', return_value=content) as request:
                result = cache.data('maps', '23', refresh=True)
            request.assert_called_once_with('/data/en/maps/23.d.json')
            self.assertEqual({'id': '23', 'name': 'Chaotic Outer Reshanta'}, result)
            self.assertEqual(content, raw.read_bytes())
            self.assertEqual(result, json.loads(decoded.read_text(encoding='utf-8')))
            self.assertEqual(b'other locale', untouched.read_bytes())
            self.assertEqual(b'other map', other.read_bytes())
            with patch.object(cache, 'request', side_effect=AssertionError('unexpected request')):
                self.assertEqual(result, cache.data('maps', '23'))
            self.assertFalse(list(raw.parent.glob('.refresh-*')))

    def test_http_and_invalid_responses_preserve_both_previous_cache_files(self):
        responses = [b'{broken', b'[{"name":99}]', b'\xff',
                     urllib.error.HTTPError('https://cdn-hosted.gaming.tools/aion2/data/en/maps/23.d.json',
                                            403, 'Forbidden', None, None),
                     urllib.error.HTTPError('https://cdn-hosted.gaming.tools/aion2/data/en/maps/23.d.json',
                                            429, 'Too Many Requests', None, None)]
        with tempfile.TemporaryDirectory() as folder, patch('fetch_gamingtools.CACHE', Path(folder) / 'raw'):
            raw = Path(folder) / 'cdn/data/en/maps/23.d.json'
            decoded = raw.with_name('23.json')
            raw.parent.mkdir(parents=True)
            previous_raw, previous_decoded = b'{"raw":"preserved"}', b'{"decoded":"preserved"}'
            raw.write_bytes(previous_raw)
            decoded.write_bytes(previous_decoded)
            cache = PublicCache()
            for response in responses:
                with self.subTest(response=type(response).__name__):
                    kwargs = {'side_effect': response} if isinstance(response, Exception) else {'return_value': response}
                    with patch.object(cache, 'request', **kwargs):
                        with self.assertRaises((ValueError, IndexError, urllib.error.HTTPError)):
                            cache.data('maps', '23', refresh=True)
                    self.assertEqual(previous_raw, raw.read_bytes())
                    self.assertEqual(previous_decoded, decoded.read_bytes())
                    self.assertFalse(list(raw.parent.glob('.refresh-*')))

    def test_cli_refresh_targets_only_explicit_kinds_in_the_selected_locale(self):
        with tempfile.TemporaryDirectory() as folder, patch('fetch_gamingtools.CACHE', Path(folder) / 'raw'):
            with patch.object(PublicCache, 'data', return_value={}) as data, patch('builtins.print'):
                main(['--refresh', 'interactive-map', 'interactive-map/23', 'maps/23', '--locale', 'de'])
                self.assertEqual([call('interactive-map', None, 'de', refresh=True),
                                  call('interactive-map', '23', 'de', refresh=True),
                                  call('maps', '23', 'de', refresh=True)], data.call_args_list)
                data.reset_mock()
                main(['maps/23'])
                data.assert_called_once_with('maps', '23', 'en', refresh=False)


class TileRefreshTests(unittest.TestCase):
    @staticmethod
    def tile_pack(colour=0):
        pixels = np.full((512, 512, 3), colour, dtype=np.uint8)
        ok, encoded = cv2.imencode('.webp', pixels, [cv2.IMWRITE_WEBP_QUALITY, 101])
        if not ok:
            raise AssertionError('Unable to encode synthetic tile')
        header = struct.pack('<12I', 0x4b505447, 2, 1, 512, 0, 0, 512, 512, 24, 48, 0, 0)
        entry = struct.pack('<B3xIIQI', 0, 0, 0, 72, len(encoded))
        return header + entry + encoded.tobytes()

    def test_valid_refresh_replaces_only_the_selected_tile_pack_byte_for_byte(self):
        with tempfile.TemporaryDirectory() as folder, patch('fetch_gamingtools.CACHE', Path(folder) / 'raw'):
            path = '/tiles/outer.gtpk'
            target = Path(folder) / 'cdn/tiles/outer.gtpk'
            target.parent.mkdir(parents=True)
            previous, current = self.tile_pack(), self.tile_pack(120)
            target.write_bytes(previous)
            untouched = target.with_name('other.gtpk')
            untouched.write_bytes(previous)
            cache = PublicCache()
            with patch.object(cache, 'request', return_value=current) as request:
                self.assertEqual(target, cache.cdn(path, refresh=True))
            request.assert_called_once_with(path)
            self.assertEqual(current, target.read_bytes())
            self.assertEqual(previous, untouched.read_bytes())
            with patch.object(cache, 'request', side_effect=AssertionError('unexpected request')):
                self.assertEqual(target, cache.cdn(path))

    def test_invalid_tile_and_http_error_leave_the_previous_pack_intact(self):
        valid = self.tile_pack()
        invalid_offset = valid[:48] + struct.pack('<B3xIIQI', 0, 0, 0, 1, len(valid) - 72) + valid[72:]
        invalid_image = valid[:48] + struct.pack('<B3xIIQI', 0, 0, 0, 72, 8) + b'not-webp'
        responses = [b'truncated', invalid_offset, invalid_image,
                     urllib.error.HTTPError('https://cdn-hosted.gaming.tools/aion2/tiles/outer.gtpk',
                                            403, 'Forbidden', None, None)]
        with tempfile.TemporaryDirectory() as folder, patch('fetch_gamingtools.CACHE', Path(folder) / 'raw'):
            target = Path(folder) / 'cdn/tiles/outer.gtpk'
            target.parent.mkdir(parents=True)
            target.write_bytes(valid)
            cache = PublicCache()
            for response in responses:
                with self.subTest(response=type(response).__name__):
                    kwargs = {'side_effect': response} if isinstance(response, Exception) else {'return_value': response}
                    with patch.object(cache, 'request', **kwargs):
                        with self.assertRaises((ValueError, urllib.error.HTTPError)):
                            cache.cdn('/tiles/outer.gtpk', refresh=True)
                    self.assertEqual(valid, target.read_bytes())
                    self.assertFalse(list(target.parent.glob('*.tmp')))


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
