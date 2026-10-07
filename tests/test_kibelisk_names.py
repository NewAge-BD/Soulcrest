import sys
import unittest
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
from build_tester_data import name_kibelisks_by_village


def payload(*markers):
    return {'categories': [{'name': 'Kibelisk'}, {'name': 'Village'}], 'markers': [list(m) for m in markers]}


class KibeliskNameTests(unittest.TestCase):
    def test_duplicate_takes_the_village_label_next_to_it(self):
        # Altgard data (gaming.tools): two "Steel Hammer Temporary Trading Post" Kibelisks; the game lists one
        # as "Shulak Street Stall" (user screenshots 2026-10-06).
        p = payload([0, 5407.4, 3466.7, 'Steel Hammer Temporary Trading Post', 'Provisorischer Handelsposten von Stahlhammer', 10, None, None],
                    [0, 5721.3, 5204.9, 'Steel Hammer Temporary Trading Post', 'Provisorischer Handelsposten von Stahlhammer', 10, None, None],
                    [1, 5404.2, 3464.3, 'Shulak Street Stall', 'Shulak-Straßenstand', -1, None, None],
                    [1, 5747.5, 5209.9, 'Steel Hammer Temporary Trading Post', 'Provisorischer Handelsposten von Stahlhammer', -1, None, None])
        name_kibelisks_by_village(p)
        self.assertEqual(p['markers'][0][3:5], ['Shulak Street Stall', 'Shulak-Straßenstand'])
        self.assertEqual(p['markers'][1][3], 'Steel Hammer Temporary Trading Post')

    def test_unique_names_and_far_labels_stay(self):
        p = payload([0, 100, 100, 'Safe Haven', 'Exulantendorf', 10, None, None],
                    [0, 1799, 1748, 'Cantas Valley Mushroom Tree Peak', None, 10, None, None],
                    [0, 1772, 1580, 'Cantas Valley Mushroom Tree Peak', None, 10, None, None],
                    [1, 102, 101, 'Other Village', None, -1, None, None],
                    [1, 1799, 1842, 'Ellun River Meadow', None, -1, None, None])
        name_kibelisks_by_village(p)
        self.assertEqual([m[3] for m in p['markers'][:3]], ['Safe Haven', 'Cantas Valley Mushroom Tree Peak', 'Cantas Valley Mushroom Tree Peak'])


if __name__ == '__main__':
    unittest.main()
