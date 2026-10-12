import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
from exploration_network_ids import point_network_ids


class NetworkIdentityImportTests(unittest.TestCase):
    def test_dungeon_uses_quest_id_not_instance_id_and_keeps_shared_placements(self):
        points = [{'id': 'object-a', 'types': ['sealed-dungeon']},
                  {'id': 'object-b', 'types': ['sealed-dungeon']}]
        objects = [{'id': p, 'target': {'id': 'dungeon-310001'}} for p in ['a', 'b']]
        quests = [{'id': '1810001', 'kind': 'exploration',
                   'regions': [{'instance': {'id': '310001'}}, {'instance': {'id': '310001'}}]}]
        result = point_network_ids(points, {'objects': objects}, quests)
        self.assertEqual({'object-a': {'questId': 1810001}, 'object-b': {'questId': 1810001}}, result)

    def test_ambiguous_or_missing_quest_never_guesses(self):
        points = [{'id': 'object-a', 'types': ['sealed-dungeon'], 'target': {'id': 'dungeon-310001'}}]
        quests = [{'id': str(q), 'kind': 'exploration', 'regions': [{'instance': {'id': '310001'}}]}
                  for q in [1810001, 1810002]]
        self.assertEqual({}, point_network_ids(points, {}, quests))
        self.assertEqual({}, point_network_ids(points, {}, []))

    def test_shared_teleport_ids_keep_both_place_keys(self):
        points = [{'id': f'object-{p}', 'types': ['teleportartifact']} for p in ['a', 'b']]
        objects = [{'id': p, 'teleport': {'id': '1052043'}} for p in ['a', 'b']]
        self.assertEqual({'object-a': {'teleportId': 1052043}, 'object-b': {'teleportId': 1052043}},
                         point_network_ids(points, {'objects': objects}, []))

    def test_stronghold_requires_exact_exploration_quest(self):
        points = [{'id': 'object-a', 'types': ['stronghold'], 'target': {'id': 'stronghold-1820001'}}]
        quests = [{'id': '1820001', 'kind': 'exploration'}]
        self.assertEqual({'object-a': {'questId': 1820001}}, point_network_ids(points, {}, quests))
        quests[0]['kind'] = 'story'
        self.assertEqual({}, point_network_ids(points, {}, quests))


if __name__ == '__main__':
    unittest.main()
