import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
import build_tester_data as builder


class Icons:
    def get(self, path):
        return 'cached' + path if path else None


class BossLootTests(unittest.TestCase):
    def test_exact_ids_deduplicate_tables_and_join_translations(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for locale in ['en', 'de']:
                path = root / 'cdn/data' / locale / 'npcs/123.d.json'
                path.parent.mkdir(parents=True)
                path.touch()
            item = {'id': '45', 'name': 'First item', 'iconPath': '/first.webp', 'rarity': 'unique'}
            en = {'loot': [{'item': item, 'tableKind': 'basic'}, {'item': item, 'tableKind': 'own'},
                           {'item': {'id': '46', 'name': 'Second item'}}]}
            de = {'loot': [{'item': {'id': '45', 'name': 'Erstes Item'}}]}
            with patch.object(builder, 'GAMINGTOOLS', root), patch.object(builder, 'data', side_effect=lambda k, i, locale='en': en if locale == 'en' else de):
                pool = builder.boss_loot('123', Icons())
            self.assertEqual(2, len(pool))
            self.assertEqual('Erstes Item', pool[0]['de'])
            self.assertEqual('cached/first.webp', pool[0]['icon'])
            self.assertIsNone(pool[1]['de'])
            self.assertIsNone(pool[1]['icon'])
            self.assertTrue(pool[0]['ownPool'])
            self.assertFalse(pool[1]['ownPool'])

    def build_pool(self, rows):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            path = root / 'cdn/data/en/npcs/123.d.json'
            path.parent.mkdir(parents=True)
            path.touch()
            with patch.object(builder, 'GAMINGTOOLS', root), patch.object(builder, 'data', return_value={'loot': rows}):
                return builder.boss_loot('123', Icons())

    def test_own_pool_wins_for_duplicate_exact_ids_in_both_source_orders(self):
        item = {'id': '45', 'name': 'Boss gear', 'rarity': 'unique'}
        for kinds in [('basic', 'own'), ('own', 'basic'), ('basic', 'own', 'basic')]:
            with self.subTest(kinds=kinds):
                pool = self.build_pool([{'item': item, 'tableKind': kind} for kind in kinds])
                self.assertEqual(1, len(pool))
                self.assertTrue(pool[0]['ownPool'])
                self.assertEqual('unique', pool[0]['rarity'])

    def test_only_the_exact_direct_own_table_kind_establishes_ownership(self):
        for kind in ['basic', None, '', 'Own', 'OWN', 'named', 'special', 'own ']:
            with self.subTest(kind=kind):
                row = {'item': {'id': '45', 'name': 'Named Unique Boss Sword', 'rarity': 'unique'}}
                if kind is not None:
                    row['tableKind'] = kind
                self.assertFalse(self.build_pool([row])[0]['ownPool'])
        self.assertTrue(self.build_pool([{'item': {'id': '45', 'name': 'Material', 'rarity': 'common'},
                                         'tableKind': 'own'}])[0]['ownPool'])

    def test_ownership_merge_preserves_first_item_facts_and_never_promotes_another_id(self):
        pool = self.build_pool([
            {'item': {'id': '45', 'name': 'First label', 'iconPath': '/first.webp', 'rarity': 'unique'},
             'tableKind': 'basic'},
            {'item': {'id': '45', 'name': 'Second label', 'iconPath': '/second.webp', 'rarity': 'legend'},
             'tableKind': 'own'},
            {'item': {'id': '46', 'name': 'First label', 'rarity': 'unique'}, 'tableKind': 'basic'}])
        self.assertEqual(['45', '46'], [item['id'] for item in pool])
        self.assertEqual('First label', pool[0]['en'])
        self.assertEqual('cached/first.webp', pool[0]['icon'])
        self.assertEqual('unique', pool[0]['rarity'])
        self.assertTrue(pool[0]['ownPool'])
        self.assertFalse(pool[1]['ownPool'])

    def test_nested_container_contents_never_acquire_or_supply_direct_pool_ownership(self):
        hidden_gear = {'item': {'id': '99', 'name': 'Unique gear in container', 'rarity': 'unique'},
                       'tableKind': 'own'}
        pool = self.build_pool([
            {'item': {'id': '45', 'name': 'Common container', 'rarity': 'common',
                      'contents': [hidden_gear]}, 'tableKind': 'basic', 'contents': [hidden_gear]},
            {'item': {'id': '46', 'name': 'Own common container', 'rarity': 'common',
                      'contents': [hidden_gear]}, 'tableKind': 'own'},
            {'item': {'id': '47', 'name': 'Direct unique gear', 'rarity': 'unique'},
             'tableKind': 'basic', 'parent': {'tableKind': 'own'}}])
        self.assertEqual(['45', '46', '47'], [item['id'] for item in pool])
        self.assertEqual([False, True, False], [item['ownPool'] for item in pool])
        self.assertEqual(['common', 'common', 'unique'], [item['rarity'] for item in pool])

    def test_unknown_pool_is_not_a_source_confirmed_empty_pool(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            with patch.object(builder, 'GAMINGTOOLS', root):
                self.assertIsNone(builder.boss_loot('123', Icons()))
                path = root / 'cdn/data/en/npcs/123.d.json'
                path.parent.mkdir(parents=True)
                path.touch()
                with patch.object(builder, 'data', return_value={'loot': []}):
                    self.assertEqual([], builder.boss_loot('123', Icons()))
                with patch.object(builder, 'data', return_value={}):
                    self.assertIsNone(builder.boss_loot('123', Icons()))


if __name__ == '__main__':
    unittest.main()
