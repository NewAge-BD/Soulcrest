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
