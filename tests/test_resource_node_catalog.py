import copy
import math
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
import build_tester_data as builder


class ResourceNodeCatalogTests(unittest.TestCase):
    config = {'bounds': {'minX': -100, 'maxX': 100, 'minY': -100, 'maxY': 100},
              'imageWidth': 512, 'imageHeight': 512, 'maximumZoom': 0}

    def resource(self, positions=None, **entity):
        return {'types': ['resource-herb'], 'entity': {'id': '1550032', 'name': 'Example herb',
                'mainCategoryId': 'gatherables', **entity}, 'positions': positions or [-20, 30, 40, 50]}

    def build(self, groups, de=None, map_id='synthetic', size=512):
        return builder.build_resource_nodes({'groups': groups}, {'groups': de or []}, self.config, map_id, size)

    def test_ids_depend_on_map_template_and_original_coordinates_not_order_name_or_resolution(self):
        source = self.resource()
        first = self.build([source])
        shuffled = self.resource([40.0, 50.0, -20.0, 30.0], name='Renamed herb')
        other = self.build([shuffled], size=1024)
        self.assertEqual([n['id'] for n in first], [n['id'] for n in other])
        self.assertTrue(set(n['id'] for n in first).isdisjoint(n['id'] for n in self.build([source], map_id='other')))
        changed = self.build([self.resource(id='1550033')])
        self.assertTrue(set(n['id'] for n in first).isdisjoint(n['id'] for n in changed))

    def test_exact_translation_type_native_and_logical_coordinates_are_preserved_without_item_join(self):
        row = self.build([self.resource([-20, 30])], [self.resource([-20, 30], name='Beispielkraut')])[0]
        self.assertEqual(1550032, row['gatherableId'])
        self.assertEqual('resource-herb', row['typeId'])
        self.assertEqual('Example herb', row['nameEn'])
        self.assertEqual('Beispielkraut', row['nameDe'])
        self.assertEqual((-20, 30), (row['nativeX'], row['nativeY']))
        self.assertEqual((204.8, 332.8), (row['x'], row['y']))
        self.assertNotIn('itemId', row)

    def test_exact_duplicate_placement_collapses_but_different_position_remains(self):
        rows = self.build([self.resource([-20, 30, -20, 30, 40, 50])])
        self.assertEqual(2, len(rows))
        changed = copy.deepcopy(self.resource())
        changed['types'] = ['resource-metal']
        with self.assertRaisesRegex(ValueError, 'Conflicting'):
            self.build([self.resource(), changed])

    def test_nonresource_groups_are_ignored_and_unknown_translation_remains_null(self):
        self.assertEqual([], self.build([self.resource(mainCategoryId='npcs')]))
        self.assertIsNone(self.build([self.resource()])[0]['nameDe'])

    def test_malformed_untyped_and_nonfinite_positions_fail_closed(self):
        invalid = [self.resource([1]), self.resource([math.nan, 1]), self.resource([True, 1]),
                   self.resource([math.inf, 1]), self.resource([-1000, 1])]
        no_type = self.resource()
        no_type['types'] = []
        invalid.append(no_type)
        for source in invalid:
            with self.subTest(source=source), self.assertRaises(ValueError):
                self.build([source])


if __name__ == '__main__':
    unittest.main()
