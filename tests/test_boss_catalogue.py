import copy
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
import build_tester_data as builder


class Icons:
    def get(self, path):
        return 'cached' + path if path else None


class BossCatalogueTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        fixture = Path(__file__).parent / 'fixtures/boss-catalog/abyss-verteron.json'
        cls.maps = {m['id']: m for m in json.loads(fixture.read_text(encoding='utf-8'))['maps']}

    def build(self, source):
        with patch.object(builder, 'boss_loot', return_value=[{'id': 'fixture-item'}]):
            return builder.build_bosses(source['points'], source['translatedPoints'], source['details'],
                                        source['config'], source['size'], Icons())

    def test_all_published_abyss_and_verteron_field_bosses_remain_in_catalogue(self):
        for mid, count in [('20', 8), ('22', 5), ('23', 0), ('1010', 24)]:
            with self.subTest(map=mid):
                bosses = self.build(self.maps[mid])
                expected = {int(s['id']) for s in self.maps[mid]['details']['namedSpawns']}
                self.assertEqual(count, len(bosses))
                self.assertEqual(expected, {b['spawnId'] for b in bosses})

    def test_multiple_positions_keep_identity_names_portrait_loot_and_source_without_a_target(self):
        bosses = {b['spawnId']: b for b in self.build(self.maps['20'])}
        for spawn_id, npc_id, en, de in [
                (2002, 2600089, 'Watcher Kaira', 'Aufseherin Kaira'),
                (2003, 2600084, 'Guardian Lord Nahma', 'Wächtergott Nahma')]:
            with self.subTest(spawn=spawn_id):
                boss = bosses[spawn_id]
                self.assertEqual(npc_id, boss['npcId'])
                self.assertEqual(en, boss['en'])
                self.assertEqual(de, boss['de'])
                self.assertIsNone(boss['x'])
                self.assertIsNone(boss['y'])
                self.assertIn('/portrait/portrait_256/', boss['icon'])
                self.assertEqual([{'id': 'fixture-item'}], boss['loot'])
                self.assertEqual(['https://aion2.gaming.tools/npcs/' + str(npc_id)], boss['sourceRefs'])
        self.assertEqual({2002, 2003}, {b['spawnId'] for b in bosses.values() if b['x'] is None})

    def test_unambiguous_positions_keep_existing_projection_and_same_names_do_not_merge(self):
        bosses = {b['spawnId']: b for b in self.build(self.maps['20'])}
        self.assertEqual((2082.906, 2017.39), (bosses[2006]['x'], bosses[2006]['y']))
        self.assertEqual([2600084, 2600093, 2600094],
                         sorted(b['npcId'] for b in bosses.values() if b['en'] == 'Guardian Lord Nahma'))
        self.assertIsNotNone(bosses[2004]['x'])
        self.assertIsNotNone(bosses[2005]['x'])

    def test_repeated_record_at_same_source_position_does_not_remove_known_target(self):
        source = copy.deepcopy(self.maps['20'])
        point = next(p for p in source['points'] if p['entity']['id'] == '2600096')
        source['points'].append(copy.deepcopy(point))
        boss = next(b for b in self.build(source) if b['spawnId'] == 2006)
        self.assertEqual((2082.906, 2017.39), (boss['x'], boss['y']))

    def test_ambiguous_spawn_identity_is_never_chosen_from_identical_npc_id(self):
        source = copy.deepcopy(self.maps['20'])
        source['details']['namedSpawns'].append({'id': '9999', 'npc': {'id': '2600096'}})
        bosses = self.build(source)
        self.assertNotIn(2006, {b['spawnId'] for b in bosses})
        self.assertNotIn(9999, {b['spawnId'] for b in bosses})
        self.assertEqual(7, len(bosses))

    def test_only_explicit_boss_points_join_exact_named_spawn_identity(self):
        source = copy.deepcopy(self.maps['20'])
        source['points'] = [p for p in source['points'] if p['entity']['id'] == '2600096']
        source['points'][0]['types'] = ['npc']
        self.assertEqual([], self.build(source))
        source['points'][0]['types'] = ['boss']
        source['points'][0]['entity']['id'] = '999999'
        self.assertEqual([], self.build(source))


    def lower_calendar_source(self):
        source = copy.deepcopy(self.maps['20'])
        source['details']['id'] = '20'
        rules = {
            '2002': ('1006', '01:00', ['monday', 'tuesday', 'wednesday', 'thursday', 'friday', 'saturday', 'sunday']),
            '2003': ('1013', '21:00', ['friday', 'sunday']),
            '2004': ('1014', '21:00', ['friday', 'sunday']),
            '2005': ('1015', '21:00', ['friday', 'sunday']),
            '2006': ('1018', '21:30', ['monday', 'thursday', 'saturday']),
            '2007': ('1019', '21:30', ['monday', 'thursday', 'saturday']),
            '2008': ('1020', '21:30', ['monday', 'thursday', 'saturday']),
        }
        for spawn in source['details']['namedSpawns']:
            if spawn['id'] not in rules:
                continue
            schedule_id, time, days = rules[spawn['id']]
            spawn['schedule'] = {'id': schedule_id, 'timeOfDay': time, 'weekdays': days, 'timezoneVerified': False}
            if spawn['id'] == '2002':
                spawn['schedule']['cycleMinutes'] = 180
        return source

    def test_seven_exact_lower_rules_add_weekly_eu_calendars_without_changing_any_boss_fact(self):
        source = self.lower_calendar_source()
        original = copy.deepcopy(source)
        bosses = {boss['spawnId']: boss for boss in self.build(source)}
        baseline = {boss['spawnId']: boss for boss in self.build(self.maps['20'])}
        self.assertEqual(set(range(2001, 2009)), set(bosses))
        self.assertEqual(set(range(2002, 2009)), {key for key, boss in bosses.items() if 'calendarSchedule' in boss})
        for key, boss in bosses.items():
            self.assertEqual(baseline[key], {field: value for field, value in boss.items() if field != 'calendarSchedule'})
        self.assertEqual({'kind': 2, 'timeZoneId': 'Europe/Berlin',
                          'times': ['01:00:00', '04:00:00', '07:00:00', '10:00:00', '13:00:00', '16:00:00', '19:00:00', '22:00:00'],
                          'weekdays': [0, 1, 2, 3, 4, 5, 6]}, bosses[2002]['calendarSchedule'])
        for key in [2003, 2004, 2005]:
            self.assertEqual({'kind': 2, 'timeZoneId': 'Europe/Berlin', 'times': ['21:00:00'],
                              'weekdays': [0, 5]}, bosses[key]['calendarSchedule'])
        for key in [2006, 2007, 2008]:
            self.assertEqual({'kind': 2, 'timeZoneId': 'Europe/Berlin', 'times': ['21:30:00'],
                              'weekdays': [1, 4, 6]}, bosses[key]['calendarSchedule'])
        self.assertEqual(original, source)  # Includes the unchanged timezoneVerified:false source values.

    def test_missing_or_changed_rule_never_falls_back_from_a_name_or_schedule_id(self):
        for value in [None, {}, [], '1006',
                      {'id': '9999'},
                      {'id': '1006', 'timeOfDay': '01:00'}]:
            with self.subTest(schedule=value):
                source = self.lower_calendar_source()
                next(spawn for spawn in source['details']['namedSpawns'] if spawn['id'] == '2002')['schedule'] = value
                bosses = {boss['spawnId']: boss for boss in self.build(source)}
                self.assertEqual(8, len(bosses))
                self.assertNotIn('calendarSchedule', bosses[2002])
                self.assertEqual(6, sum('calendarSchedule' in boss for boss in bosses.values()))
        source = self.lower_calendar_source()
        for spawn in source['details']['namedSpawns']:
            spawn.pop('schedule', None)
        self.assertTrue(all('calendarSchedule' not in boss for boss in self.build(source)))

    def test_unapproved_schedule_fields_or_types_do_not_promote_a_calendar(self):
        changes = [('id', '1013'), ('id', 1006), ('timeOfDay', '02:00'),
                   ('cycleMinutes', 240), ('cycleMinutes', 0), ('cycleMinutes', True),
                   ('cycleMinutes', 180.0), ('cycleMinutes', '180'), ('weekdays', ['monday']),
                   ('timezoneVerified', True), ('timezoneVerified', 0),
                   ('timeZoneId', 'Asia/Seoul'), ('durationMinutes', 30)]
        for field, value in changes:
            with self.subTest(field=field, value=value):
                source = self.lower_calendar_source()
                spawn = next(spawn for spawn in source['details']['namedSpawns'] if spawn['id'] == '2002')
                spawn['schedule'][field] = value
                bosses = {boss['spawnId']: boss for boss in self.build(source)}
                self.assertNotIn('calendarSchedule', bosses[2002])
                self.assertEqual(set(range(2001, 2009)), set(bosses))

    def test_duplicate_or_conflicting_named_spawn_never_supplies_calendar_authority(self):
        for changed in [False, True]:
            with self.subTest(conflicting=changed):
                source = self.lower_calendar_source()
                duplicate = copy.deepcopy(next(spawn for spawn in source['details']['namedSpawns'] if spawn['id'] == '2002'))
                if changed:
                    duplicate['schedule']['timeOfDay'] = '02:00'
                source['details']['namedSpawns'].append(duplicate)
                bosses = {boss['spawnId']: boss for boss in self.build(source)}
                self.assertEqual(set(range(2001, 2009)), set(bosses))
                self.assertNotIn('calendarSchedule', bosses[2002])
                self.assertEqual(6, sum('calendarSchedule' in boss for boss in bosses.values()))

    def test_exact_spawn_schedule_cannot_promote_another_map_or_npc(self):
        for map_id in ['1010', '1110', '22', '23', '020', None]:
            with self.subTest(map=map_id):
                source = self.lower_calendar_source()
                source['details']['id'] = map_id
                self.assertTrue(all('calendarSchedule' not in boss for boss in self.build(source)))
        source = self.lower_calendar_source()
        next(spawn for spawn in source['details']['namedSpawns'] if spawn['id'] == '2002')['npc']['id'] = '999999'
        for point in source['points']:
            if point['entity']['id'] == '2600089':
                point['entity']['id'] = '999999'
        bosses = {boss['spawnId']: boss for boss in self.build(source)}
        self.assertEqual(8, len(bosses))
        self.assertNotIn('calendarSchedule', bosses[2002])

    def test_active_cache_has_all_eight_lower_identities_and_exactly_seven_approved_rules(self):
        path = builder.GAMINGTOOLS / 'cdn/data/en/maps/20.d.json'
        points = builder.GAMINGTOOLS / 'cdn/data/en/interactive-map/20.d.json'
        if not path.is_file() or not points.is_file():
            self.skipTest('Optional active local gaming.tools cache is absent.')
        source = copy.deepcopy(self.maps['20'])
        source['details'] = builder.data('maps', '20')
        source['points'] = builder.data('interactive-map', '20')['points']
        original = copy.deepcopy(source['details'])
        bosses = {boss['spawnId']: boss for boss in self.build(source)}
        self.assertEqual(set(range(2001, 2009)), set(bosses))
        self.assertEqual(set(range(2002, 2009)), {key for key, boss in bosses.items() if 'calendarSchedule' in boss})
        self.assertEqual(original, source['details'])


if __name__ == '__main__':
    unittest.main()
