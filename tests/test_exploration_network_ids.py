import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
from exploration_network_ids import point_network_ids


class NetworkIdentityImportTests(unittest.TestCase):
    def test_dungeon_id_requires_exact_direct_target_and_quest_instance(self):
        points = [{'id': f'object-{name}', 'types': ['sealed-dungeon'],
                   'target': {'id': 'dungeon-320027'}} for name in ['a', 'b']]
        quests = [{'id': '2810027', 'kind': 'exploration',
                   'regions': [{'instance': {'id': '320027'}}]}]
        self.assertEqual({f'object-{name}': {'questId': 2810027, 'dungeonId': 320027}
                          for name in ['a', 'b']}, point_network_ids(points, {}, quests))

    def test_dungeon_uses_quest_id_not_instance_id_and_keeps_shared_placements(self):
        points = [{'id': 'object-a', 'types': ['sealed-dungeon']},
                  {'id': 'object-b', 'types': ['sealed-dungeon']}]
        objects = [{'id': p, 'target': {'id': 'dungeon-310001'}} for p in ['a', 'b']]
        quests = [{'id': '1810001', 'kind': 'exploration',
                   'regions': [{'instance': {'id': '310001'}}, {'instance': {'id': '310001'}}]}]
        result = point_network_ids(points, {'objects': objects}, quests)
        self.assertEqual({'object-a': {'questId': 1810001}, 'object-b': {'questId': 1810001}}, result)

    def test_dungeon_id_does_not_borrow_an_object_target(self):
        points = [{'id': 'object-a', 'types': ['sealed-dungeon']}]
        objects = [{'id': 'a', 'target': {'id': 'dungeon-320027'}}]
        quests = [{'id': '2810027', 'kind': 'exploration',
                   'regions': [{'instance': {'id': '320027'}}]}]
        self.assertEqual({'object-a': {'questId': 2810027}},
                         point_network_ids(points, {'objects': objects}, quests))

    def test_dungeon_id_does_not_guess_from_quest_id(self):
        points = [{'id': 'object-a', 'types': ['sealed-dungeon'],
                   'target': {'id': 'dungeon-320027'}}]
        for regions in [[], [{'instance': {'id': '320028'}}]]:
            with self.subTest(regions=regions):
                quests = [{'id': '2810027', 'kind': 'exploration', 'regions': regions}]
                self.assertEqual({}, point_network_ids(points, {}, quests))

    def test_dungeon_id_rejects_invalid_namespace_and_nonpositive_or_oversized_ids(self):
        for target, instance in [('stronghold-320027', '320027'), ('320027', '320027'),
                                 ('dungeon-0', '0'), ('dungeon--1', '-1'),
                                 ('dungeon-0320027', '0320027'),
                                 ('dungeon-2147483648', '2147483648'),
                                 ('dungeon-' + '9' * 100, '9' * 100),
                                 ('dungeon-320027-extra', '320027-extra'),
                                 (320027, '320027')]:
            with self.subTest(target=target):
                points = [{'id': 'object-a', 'types': ['sealed-dungeon'], 'target': {'id': target}}]
                quests = [{'id': '2810027', 'kind': 'exploration',
                           'regions': [{'instance': {'id': instance}}]}]
                result = point_network_ids(points, {}, quests)
                self.assertNotIn('dungeonId', result.get('object-a', {}))

    def test_dungeon_id_accepts_largest_positive_wire_i32(self):
        points = [{'id': 'object-a', 'types': ['sealed-dungeon'],
                   'target': {'id': 'dungeon-2147483647'}}]
        quests = [{'id': '1000001', 'kind': 'exploration',
                   'regions': [{'instance': {'id': '2147483647'}}]}]
        self.assertEqual({'object-a': {'questId': 1000001, 'dungeonId': 2147483647}},
                         point_network_ids(points, {}, quests))

    def test_ambiguous_or_missing_quest_never_guesses(self):
        points = [{'id': 'object-a', 'types': ['sealed-dungeon'], 'target': {'id': 'dungeon-310001'}}]
        quests = [{'id': str(q), 'kind': 'exploration', 'regions': [{'instance': {'id': '310001'}}]}
                  for q in [1810001, 1810002]]
        self.assertEqual({}, point_network_ids(points, {}, quests))
        self.assertEqual({}, point_network_ids(points, {}, []))

    def test_non_exploration_quest_cannot_confirm_a_dungeon_instance(self):
        points = [{'id': 'object-a', 'types': ['sealed-dungeon'], 'target': {'id': 'dungeon-320027'}}]
        quests = [{'id': '2810027', 'kind': 'story', 'regions': [{'instance': {'id': '320027'}}]}]
        self.assertEqual({}, point_network_ids(points, {}, quests))

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

    def test_stronghold_never_receives_dungeon_completion_metadata(self):
        points = [{'id': 'object-a', 'types': ['stronghold'],
                   'target': {'id': 'dungeon-320027'}}]
        quests = [{'id': '320027', 'kind': 'exploration',
                   'regions': [{'instance': {'id': '320027'}}]}]
        self.assertEqual({'object-a': {'questId': 320027}}, point_network_ids(points, {}, quests))

    def test_stronghold_direct_target_maps_exact_instance_in_separate_field(self):
        points = [{'id': f'object-{name}', 'types': ['stronghold'],
                   'target': {'id': 'stronghold-2820012'}} for name in ['a', 'b']]
        quests = [{'id': '2820012', 'kind': 'exploration',
                   'regions': [{'instance': {'id': '142017'}}]}]
        self.assertEqual({f'object-{name}': {'questId': 2820012, 'strongholdInstanceId': 142017}
                          for name in ['a', 'b']}, point_network_ids(points, {}, quests))

    def test_stronghold_instance_does_not_borrow_an_object_target(self):
        points = [{'id': 'object-a', 'types': ['stronghold']}]
        objects = [{'id': 'a', 'target': {'id': 'stronghold-2820012'}}]
        quests = [{'id': '2820012', 'kind': 'exploration',
                   'regions': [{'instance': {'id': '142017'}}]}]
        self.assertEqual({'object-a': {'questId': 2820012}},
                         point_network_ids(points, {'objects': objects}, quests))

    def test_stronghold_foreign_target_namespaces_keep_only_legacy_quest_id(self):
        quests = [{'id': '2820012', 'kind': 'exploration',
                   'regions': [{'instance': {'id': '142017'}}]}]
        for target in ['dungeon-2820012', 'quest-2820012', '2820012', 2820012]:
            with self.subTest(target=target):
                points = [{'id': 'object-a', 'types': ['stronghold'], 'target': {'id': target}}]
                self.assertEqual({'object-a': {'questId': 2820012}}, point_network_ids(points, {}, quests))

    def test_stronghold_noncanonical_targets_never_add_completion_metadata(self):
        for target, quest_id in [('stronghold-02820012', '02820012'),
                                 ('stronghold-0', '0'), ('stronghold--1', '-1'),
                                 ('stronghold-2147483648', '2147483648'),
                                 ('stronghold-' + '9' * 100, '9' * 100),
                                 ('stronghold-2820012-extra', '2820012'),
                                 ('Stronghold-2820012', '2820012'),
                                 ('stronghold-+2820012', '+2820012')]:
            with self.subTest(target=target):
                points = [{'id': 'object-a', 'types': ['stronghold'], 'target': {'id': target}}]
                quests = [{'id': quest_id, 'kind': 'exploration',
                           'regions': [{'instance': {'id': '142017'}}]}]
                self.assertNotIn('strongholdInstanceId', point_network_ids(points, {}, quests).get('object-a', {}))

    def test_stronghold_missing_quest_never_derives_instance_from_target(self):
        points = [{'id': 'object-a', 'types': ['stronghold'], 'target': {'id': 'stronghold-2820012'}}]
        self.assertEqual({}, point_network_ids(points, {}, []))
        quests = [{'id': '2820013', 'kind': 'exploration',
                   'regions': [{'instance': {'id': '142017'}}]}]
        self.assertEqual({}, point_network_ids(points, {}, quests))

    def test_stronghold_invalid_or_missing_instances_keep_legacy_quest_id(self):
        points = [{'id': 'object-a', 'types': ['stronghold'], 'target': {'id': 'stronghold-2820012'}}]
        regions_cases = [[], None, 'regions', [{}], [None], [{'instance': None}],
                         [{'instance': {}}]]
        regions_cases += [[{'instance': {'id': value}}] for value in
                          [None, 0, -1, 2147483648, True, False, 142017.0,
                           '0', '-1', '+142017', '0142017', '142017 ', '١٤٢٠١٧',
                           '2147483648', '9' * 100]]
        for regions in regions_cases:
            with self.subTest(regions=regions):
                quests = [{'id': '2820012', 'kind': 'exploration', 'regions': regions}]
                self.assertEqual({'object-a': {'questId': 2820012}}, point_network_ids(points, {}, quests))

    def test_stronghold_ambiguous_or_partly_invalid_regions_fail_closed(self):
        points = [{'id': 'object-a', 'types': ['stronghold'], 'target': {'id': 'stronghold-2820012'}}]
        for extra in [{'instance': {'id': '142018'}}, {'instance': {'id': 0}}, {'instance': None}]:
            with self.subTest(extra=extra):
                quests = [{'id': '2820012', 'kind': 'exploration',
                           'regions': [{'instance': {'id': '142017'}}, extra]}]
                self.assertEqual({'object-a': {'questId': 2820012}}, point_network_ids(points, {}, quests))

    def test_stronghold_repeated_same_instance_is_one_identity(self):
        points = [{'id': 'object-a', 'types': ['stronghold'], 'target': {'id': 'stronghold-2820012'}}]
        quests = [{'id': '2820012', 'kind': 'exploration',
                   'regions': [{'instance': {'id': '142017'}}, {}, {'instance': {'id': 142017}}]}]
        self.assertEqual({'object-a': {'questId': 2820012, 'strongholdInstanceId': 142017}},
                         point_network_ids(points, {}, quests))

    def test_stronghold_shared_instance_with_other_exploration_quest_is_ambiguous(self):
        points = [{'id': 'object-a', 'types': ['stronghold'], 'target': {'id': 'stronghold-2820012'}}]
        quests = [{'id': str(q), 'kind': 'exploration', 'regions': [{'instance': {'id': '142017'}}]}
                  for q in [2820012, 2820013]]
        self.assertEqual({'object-a': {'questId': 2820012}}, point_network_ids(points, {}, quests))

    def test_stronghold_story_quest_cannot_confirm_target_but_does_not_ambiguate_exploration(self):
        points = [{'id': 'object-a', 'types': ['stronghold'], 'target': {'id': 'stronghold-2820012'}}]
        story = {'id': '2820012', 'kind': 'story', 'regions': [{'instance': {'id': '142017'}}]}
        self.assertEqual({}, point_network_ids(points, {}, [story]))
        exploration = {'id': '2820012', 'kind': 'exploration', 'regions': [{'instance': {'id': '142017'}}]}
        other_story = dict(story, id='2000001')
        self.assertEqual({'object-a': {'questId': 2820012, 'strongholdInstanceId': 142017}},
                         point_network_ids(points, {}, [exploration, other_story]))

    def test_stronghold_duplicate_quest_records_never_select_one(self):
        points = [{'id': 'object-a', 'types': ['stronghold'], 'target': {'id': 'stronghold-2820012'}}]
        quest = {'id': '2820012', 'kind': 'exploration', 'regions': [{'instance': {'id': '142017'}}]}
        for duplicate in [quest, dict(quest, regions=[{'instance': {'id': '142018'}}])]:
            with self.subTest(duplicate=duplicate):
                self.assertEqual({'object-a': {'questId': 2820012}},
                                 point_network_ids(points, {}, [quest, duplicate]))

    def test_stronghold_duplicate_other_quest_records_do_not_hide_shared_instance(self):
        points = [{'id': 'object-a', 'types': ['stronghold'], 'target': {'id': 'stronghold-2820012'}}]
        quests = [{'id': '2820012', 'kind': 'exploration', 'regions': [{'instance': {'id': '142017'}}]},
                  {'id': '2820013', 'kind': 'exploration', 'regions': [{'instance': {'id': '142017'}}]},
                  {'id': '2820013', 'kind': 'exploration', 'regions': [{'instance': {'id': '142018'}}]}]
        self.assertEqual({'object-a': {'questId': 2820012}}, point_network_ids(points, {}, quests))

    def test_stronghold_accepts_positive_i32_boundaries(self):
        for instance in [1, '1', 2147483647, '2147483647']:
            with self.subTest(instance=instance):
                points = [{'id': 'object-a', 'types': ['stronghold'],
                           'target': {'id': 'stronghold-2147483647'}}]
                quests = [{'id': '2147483647', 'kind': 'exploration',
                           'regions': [{'instance': {'id': instance}}]}]
                self.assertEqual({'object-a': {'questId': 2147483647, 'strongholdInstanceId': int(instance)}},
                                 point_network_ids(points, {}, quests))


if __name__ == '__main__':
    unittest.main()
