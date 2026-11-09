import copy
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
import build_tester_data as builder
from fetch_gamingtools_dataset import fetch_monster_materials
from monster_drops import (MonsterDropCatalog, VISIBLE_MATERIAL_TYPES, load_monster_drop_catalog,
                           material_item_ids, monster_drop_payload,
                           preflight_monster_drop_icons)


def item(item_id='610531001', name='Fresh Raw Meat', **extra):
    return {'id': item_id, 'name': name, 'categories': ['items/materials/craftresource'],
            'iconPath': '/images/meat.webp', **extra}


def acquisition(npc='12', map_id='1110', **extra):
    return {'method': 'drop', 'source': {'id': npc, 'mainCategoryId': 'npcs'},
            'location': {'map': {'id': map_id, 'mainCategoryId': 'maps'}}, 'loot': [], **extra}


def loader_for(index, details, german=None):
    german = german or details
    def load(kind, item_id=None, locale='en'):
        assert kind == 'items'
        return index if item_id is None else (german if locale == 'de' else details)[item_id]
    return load


class Icons:
    def get(self, path):
        return 'icons/local/' + Path(path).stem + '.png' if path else None


class MonsterDropTests(unittest.TestCase):
    def test_material_index_uses_categories_including_unfamiliar_material_types(self):
        rows = [item(), item('2', 'Unfamiliar', categories=['items/materials/newtype']),
                item('3', 'Fresh Raw Meat', categories=['items/consumables/food']), item()]
        self.assertEqual(['2', '610531001'], material_item_ids(rows))
        with self.assertRaises(ValueError):
            material_item_ids({'items': rows})

    def test_exact_item_npc_map_joins_ignore_other_acquisition_methods(self):
        row = item(acquisition=[acquisition(), acquisition(), acquisition(map_id='1010'),
                               acquisition(method='quest'),
                               acquisition(source={'id': '12', 'mainCategoryId': 'items'}),
                               acquisition(source={'id': '', 'mainCategoryId': 'npcs'}),
                               acquisition(map_id='9999')])
        catalog = load_monster_drop_catalog(loader_for([row], {row['id']: row}), ['1110', '1010'])
        self.assertEqual({('1110', '12'): {'610531001'}, ('1010', '12'): {'610531001'}}, catalog.sources)
        self.assertEqual('Meat', catalog.items['610531001']['type'])

    def test_missing_source_map_matches_exact_npc_on_each_existing_map(self):
        row = item(acquisition=[acquisition(location={}), acquisition(map_id='1110'),
                               acquisition(npc='99', map_id='9999')])
        catalog = load_monster_drop_catalog(loader_for([row], {row['id']: row}), ['1110', '1010'])
        self.assertEqual({(None, '12'): {'610531001'}, ('1110', '12'): {'610531001'}}, catalog.sources)
        for map_id in ['1110', '1010']:
            payload = monster_drop_payload(map_id, {0: '12', 1: '99'}, catalog, Icons())
            self.assertEqual([{'marker': 0, 'npcId': '12', 'items': ['610531001']}], payload['spawns'])

    def test_variants_require_their_own_sources_and_keep_exact_identity(self):
        parent = item(acquisition=[acquisition()], variants=[
            item('630531001', 'Fresh Raw Meat (Bound)', acquisition=[]),
            item('630512002', 'Soft Balaur Meat (Bound)', acquisition=[acquisition(npc='13')]),
            item('88', 'Not a material', categories=['items/gear'], acquisition=[acquisition()])])
        translated = item(name='Frisches rohes Fleisch', variants=[
            item('630531001', 'Frisches rohes Fleisch (Gebunden)')])
        catalog = load_monster_drop_catalog(loader_for([parent], {parent['id']: parent},
                                                       {parent['id']: translated}), ['1110'])
        self.assertEqual({'610531001', '630512002'}, set(catalog.items))
        self.assertEqual({'630512002'}, catalog.sources[('1110', '13')])
        self.assertIsNone(catalog.items['630512002']['de'])
        self.assertEqual('Meat', catalog.items['630512002']['type'])
        self.assertEqual('Frisches rohes Fleisch', catalog.items['610531001']['de'])

    def test_only_four_retained_families_enter_items_and_mixed_npc_sources(self):
        retained = [item('610531001', acquisition=[acquisition()]),
                    item('610531002', acquisition=[acquisition()]),
                    item('610530011', acquisition=[acquisition()]),
                    item('610530014', acquisition=[acquisition()])]
        removed = [item('610530001', acquisition=[acquisition()]),
                   item('600590001', acquisition=[acquisition()]),
                   item('632510010', acquisition=[acquisition()]),
                   item('77', 'Fresh Raw Meat', acquisition=[acquisition()])]
        rows = retained + removed
        catalog = load_monster_drop_catalog(loader_for(rows, {r['id']: r for r in rows}), ['1110'])
        expected = {r['id'] for r in retained}
        self.assertEqual({'Meat', 'Fruit', 'Leather', 'Spiritstones'}, VISIBLE_MATERIAL_TYPES)
        self.assertEqual(expected, set(catalog.items))
        self.assertEqual(expected, catalog.sources[('1110', '12')])
        payload = monster_drop_payload('1110', {4: '12'}, catalog, Icons())
        self.assertEqual(expected, {r['id'] for r in payload['items']})
        self.assertEqual(expected, set(payload['spawns'][0]['items']))
        self.assertEqual([4], [r['marker'] for r in payload['spawns']])

    def test_payload_removes_excluded_refs_and_drops_empty_spawn_rows(self):
        catalog = MonsterDropCatalog({
            '1': {'id': '1', 'en': 'Meat', 'de': None, 'type': 'Meat', 'iconPath': None},
            '2': {'id': '2', 'en': 'Other', 'de': None, 'type': 'Other Materials', 'iconPath': None},
            '3': {'id': '3', 'en': 'Balaur', 'de': None, 'type': 'Balaur Materials', 'iconPath': None},
            '4': {'id': '4', 'en': 'Stone', 'de': None, 'type': 'Refining Stones', 'iconPath': None},
        }, {('1110', '12'): {'1', '2', '3', '4'}, ('1110', '13'): {'2', '3', '4'}})
        payload = monster_drop_payload('1110', {7: '12', 8: '13'}, catalog, Icons())
        self.assertEqual(['1'], [r['id'] for r in payload['items']])
        self.assertEqual([{'marker': 7, 'npcId': '12', 'items': ['1']}], payload['spawns'])

    def test_variant_drops_never_grant_the_parent_a_drop(self):
        parent = item(acquisition=[], variants=[item('630531001', acquisition=[acquisition()])])
        catalog = load_monster_drop_catalog(loader_for([parent], {parent['id']: parent}), ['1110'])
        self.assertEqual({'630531001'}, set(catalog.items))
        self.assertEqual('Meat', catalog.items['630531001']['type'])

    def test_detail_identity_mismatch_is_an_import_failure(self):
        for german in [False, True]:
            with self.subTest(german=german):
                row = item(acquisition=[acquisition()])
                wrong = item('999')
                load = loader_for([row], {row['id']: row if german else wrong},
                                   {row['id']: wrong if german else row})
                with self.assertRaisesRegex(ValueError, 'identity mismatch'):
                    load_monster_drop_catalog(load, ['1110'])

    def test_wire_payload_references_existing_markers_and_local_item_icons(self):
        first = item(acquisition=[acquisition()])
        second = item('610531002', 'Fresh Fruit', acquisition=[acquisition()])
        catalog = load_monster_drop_catalog(loader_for([first, second],
                    {first['id']: first, second['id']: second}), ['1110'])
        markers = {5: '12', 2: '12', 0: '99'}
        original = copy.deepcopy(markers)
        payload = monster_drop_payload('1110', markers, catalog, Icons())
        self.assertEqual(markers, original)
        self.assertEqual([2, 5], [row['marker'] for row in payload['spawns']])
        self.assertEqual(['610531001', '610531002'], payload['spawns'][0]['items'])
        self.assertEqual({'marker', 'npcId', 'items'}, set(payload['spawns'][0]))
        self.assertEqual({'id', 'en', 'de', 'icon', 'type'}, set(payload['items'][0]))
        self.assertEqual('icons/local/meat.png', payload['items'][0]['icon'])
        self.assertEqual({'schemaVersion': 1, 'items': [], 'spawns': []},
                         monster_drop_payload('1010', markers, catalog, Icons()))

    def test_missing_icon_is_preserved_as_unknown(self):
        row = item(acquisition=[acquisition()], iconPath=None)
        catalog = load_monster_drop_catalog(loader_for([row], {row['id']: row}), ['1110'])
        self.assertIsNone(monster_drop_payload('1110', {0: '12'}, catalog, Icons())['items'][0]['icon'])

    def test_preflight_reads_all_material_details_before_creating_output(self):
        first, second = item(), item('2')
        with tempfile.TemporaryDirectory() as folder:
            output = Path(folder) / 'not-created'
            load = loader_for([first, second], {first['id']: first})
            with patch.object(builder, 'data', side_effect=load), \
                 patch.object(sys, 'argv', ['build_tester_data.py', '--out', str(output)]):
                with self.assertRaises(KeyError):
                    builder.main()
            self.assertFalse(output.exists())

    def test_preflight_rejects_missing_or_invalid_material_images(self):
        catalog = MonsterDropCatalog({'1': {'iconPath': '/images/material.webp'}}, {})
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            with self.assertRaises(FileNotFoundError):
                preflight_monster_drop_icons(catalog, root)
            image = root / 'assets/images/material.webp'
            image.parent.mkdir(parents=True)
            image.write_bytes(b'not an image')
            with self.assertRaises(ValueError):
                preflight_monster_drop_icons(catalog, root)

    def test_fetch_includes_both_locales_and_each_material_variants_icon(self):
        row = item(variants=[item('630531001', iconPath='/images/bound.webp')])
        class Cache:
            def __init__(self):
                self.calls = []
            def data(self, kind, item_id=None, locale='en'):
                self.calls.append((kind, item_id, locale))
                return [row] if item_id is None else row
        cache = Cache()
        paths = fetch_monster_materials(cache)
        self.assertEqual({'/images/meat.webp', '/images/bound.webp'}, paths)
        self.assertEqual([('items', None, 'en'), ('items', None, 'de'),
                          ('items', row['id'], 'en'), ('items', row['id'], 'de')], cache.calls)

    def test_map_builder_preserves_markers_and_joins_monster_and_boss_placements(self):
        row = item(acquisition=[acquisition('12'), acquisition('13')])
        catalog = load_monster_drop_catalog(loader_for([row], {row['id']: row}), ['1110'])
        monster = {'types': ['monster-kind-feral'], 'entity': {'id': '12', 'name': 'Monster',
                   'mainCategoryId': 'npcs'}, 'positions': [10, 10, 20, 20]}
        service = {'types': ['merchant'], 'entity': {'id': '12', 'name': 'Merchant',
                   'mainCategoryId': 'npcs'}, 'positions': [10, 10]}
        boss = {'id': 'boss1', 'types': ['boss'], 'entity': {'id': '13', 'name': 'Boss',
                'mainCategoryId': 'npcs'}, 'x': 30, 'y': 30}
        map_data = {'points': [boss], 'labels': [], 'groups': [monster, service]}
        index = {'categories': [
            {'id': 'monsters', 'types': [{'id': 'monster-kind-feral', 'name': 'Fera'}]},
            {'id': 'locations', 'types': [{'id': 'boss', 'name': 'Boss'}]},
            {'id': 'services', 'types': [{'id': 'merchant', 'name': 'Merchant'}]}]}
        def load(kind, entity_id=None, locale='en'):
            return (index if entity_id is None else map_data) if kind == 'interactive-map' else {}
        definition = {'name': 'Altgard', 'map': {'imageWidth': 512, 'imageHeight': 512,
                      'maximumZoom': 0, 'bounds': {'minX': 0, 'maxX': 100, 'minY': 0, 'maxY': 100}}}
        with patch.object(builder, 'data', side_effect=load), \
             patch.object(builder, 'point_network_ids', return_value={}), \
             patch.object(builder, 'build_bosses', return_value=[]), \
             patch.object(builder, 'add_pet_source_markers'), \
             patch.object(builder, 'name_kibelisks_by_village'), \
             patch.object(builder, 'apply_hidden_cube_groups', None):
            original = builder.build_map('1110', definition, Icons(), {}, {}, {})
            enhanced = builder.build_map('1110', definition, Icons(), {}, {}, {}, catalog)
        self.assertEqual(original['markers'], enhanced['markers'])
        self.assertEqual(original['categories'], enhanced['categories'])
        self.assertEqual([0, 1, 2], [s['marker'] for s in enhanced['monsterDrops']['spawns']])
        self.assertEqual(['13', '12', '12'], [s['npcId'] for s in enhanced['monsterDrops']['spawns']])


if __name__ == '__main__':
    unittest.main()
