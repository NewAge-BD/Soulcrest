import copy
import json
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'scripts'))
from hidden_cube_groups import apply_hidden_cube_groups


def fixture():
    # Two places west and east; the cubes in between join the nearer one.
    categories = [{'group': 'Collectibles', 'name': 'Hidden Cube'},
                  {'group': 'Regions', 'name': 'Village'},
                  {'group': 'Regions', 'name': 'Hunting Ground'}]
    markers = [[1, 0, 0, 'West Camp', 'Westlager', 0, None, None],
               [2, 100, 0, 'East Woods', 'Ostwald', 0, None, None]]
    markers += [[0, x, y, None, None, 0, None, None]
                for x, y in [(10, 5), (20, -5), (40, 0), (60, 1), (90, 10), (95, -10)]]
    return {'id': 'altgard', 'categories': categories, 'markers': markers,
            'groups': [{'name': 'Collectibles', 'categories': [0]}, {'name': 'Regions', 'categories': [1, 2]}]}


def connected(group):
    expected = set(group['markers'])
    reached = {min(expected)}
    for _ in expected:
        for a, b in group['connections']:
            assert a in expected and b in expected
            if a in reached or b in reached:
                reached.update((a, b))
    return reached == expected


class HiddenCubeGroupsTests(unittest.TestCase):
    def test_each_cube_joins_the_nearest_named_place(self):
        payload = fixture()
        before = copy.deepcopy(payload['markers'])
        apply_hidden_cube_groups(payload)
        self.assertEqual([m[:7] for m in before], [m[:7] for m in payload['markers']])
        groups = payload['hiddenCubeGroups']
        self.assertEqual([g['id'] for g in groups], ['altgard-cubes-west-camp', 'altgard-cubes-east-woods'])
        self.assertEqual([g['number'] for g in groups], [1, 2])
        self.assertEqual([(g['name'], g['nameDe']) for g in groups],
                         [('West Camp', 'Westlager'), ('East Woods', 'Ostwald')])
        self.assertEqual([g['markers'] for g in groups], [[2, 3, 4], [5, 6, 7]])
        for group in groups:
            self.assertEqual(len(group['connections']), len(group['markers']) - 1)
            self.assertTrue(connected(group))
            for index in group['markers']:
                self.assertEqual(payload['markers'][index][7], {'hiddenCubeGroup': group['id']})
        self.assertNotEqual(*[g['color'] for g in groups])
        self.assertEqual(payload['groups'][0]['categories'], [0, 3])
        self.assertEqual(payload['categories'][3], {'group': 'Collectibles', 'name': 'Cube Groups', 'de': 'Cube Groups',
                                                    'icon': -1, 'hidden': False, 'count': 2})

    def test_maps_without_cubes_or_places_stay_unchanged(self):
        for remove in ('cubes', 'places'):
            payload = fixture()
            payload['markers'] = [m for m in payload['markers'] if (m[0] == 0) != (remove == 'cubes')]
            before = copy.deepcopy(payload)
            apply_hidden_cube_groups(payload)
            with self.subTest(remove=remove):
                self.assertEqual(payload, before)

    def test_invalid_positions_fail_closed(self):
        payload = fixture()
        payload['markers'][3][1] = float('nan')
        with self.assertRaises(ValueError):
            apply_hidden_cube_groups(payload)
        self.assertNotIn('hiddenCubeGroups', payload)

    def test_metadata_is_additive_ties_are_stable_and_output_is_deterministic(self):
        def build():
            payload = fixture()
            payload['markers'][2][7] = {'existing': 42}
            payload['markers'].append([0, 50, 0, None, None, 0, None, None])  # exactly between both places
            apply_hidden_cube_groups(payload)
            return payload
        payload = build()
        self.assertEqual(payload['markers'][2][7], {'existing': 42, 'hiddenCubeGroup': 'altgard-cubes-west-camp'})
        self.assertIn(8, payload['hiddenCubeGroups'][0]['markers'])  # tie: the earlier place
        self.assertEqual(payload, build())

    def test_duplicate_place_names_get_unique_ids(self):
        payload = fixture()
        payload['markers'][1][3] = 'West Camp'
        apply_hidden_cube_groups(payload)
        self.assertEqual([g['id'] for g in payload['hiddenCubeGroups']],
                         ['altgard-cubes-west-camp', 'altgard-cubes-west-camp-2'])

    def test_generated_packages_group_every_cube_once(self):
        found = False
        for path in sorted((ROOT / 'imports/generated/mapdata').glob('*/data.js')):
            text = path.read_text(encoding='utf-8')
            data = json.loads(text[text.index('= {') + 2:].rstrip().rstrip(';'))
            groups = data.get('hiddenCubeGroups') or []
            if not groups:
                continue
            found = True
            cubes = {i for i, m in enumerate(data['markers']) if data['categories'][m[0]]['name'] == 'Hidden Cube'}
            with self.subTest(map=data['id']):
                self.assertEqual(sorted(i for g in groups for i in g['markers']), sorted(cubes))
                self.assertEqual(len({g['id'] for g in groups}), len(groups))
                self.assertEqual([g['number'] for g in groups], list(range(1, len(groups) + 1)))
                self.assertTrue(all(connected(g) for g in groups))
        if not found:
            self.skipTest('no generated map data package')


if __name__ == '__main__':
    unittest.main()
