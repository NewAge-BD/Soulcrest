"""Synthetic offline catalog joins; no network, client files, captures or real player data."""
import contextlib
import copy
import hashlib
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
from prepare_combat_catalog import _decode, build_catalog, main


def encoded(document):
    """Create a small synthetic d.json reference graph, rather than relying on decoded cache sidecars."""
    entries = []

    def visit(value):
        if value is None:
            return -1
        index = len(entries)
        entries.append(None)
        if isinstance(value, dict):
            entries[index] = {key: visit(child) for key, child in value.items()}
        elif isinstance(value, list):
            entries[index] = [visit(child) for child in value]
        else:
            entries[index] = value
        return index

    visit(document)
    return json.dumps(entries, separators=(',', ':')).encode('utf-8')


class CombatCatalogPreparationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.cache, self.proofs = self.root / 'cache', self.root / 'proofs'
        self.cache.mkdir()
        self.proofs.mkdir()
        self.npc_path, self.map_path = 'data/en/npcs/700.d.json', 'data/en/maps/420.d.json'
        self.variant = {'id': '700', 'name': 'Exact NPC',
                        'availability': {'rootId': '420', 'rootType': 'map'},
                        'placements': [{'maps': [{'id': '420'}], 'positions': [{'x': 12, 'y': 34}]}]}
        self.npc = {'id': '7000', 'name': 'Wrong family name',
                    'availability': {'rootId': '420', 'rootType': 'map'},
                    'placements': [{'maps': [{'id': '420'}]}],
                    'variants': [self.variant, {'id': '701', 'name': 'Other variant',
                                               'availability': {'rootId': '421', 'rootType': 'map'},
                                               'placements': [{'maps': [{'id': '421'}]}]}]}
        self.map = {'id': '420', 'name': 'Exact Dungeon',
                    'npcs': [{'id': '700', 'name': 'Map label is not the NPC name'}]}
        self.facts = [
            {'id': 'context', 'kind': 'captureDungeonContext', 'dungeonDataId': 42, 'mapDataId': 420,
             'npcDataId': None, 'observations': 2, 'captures': 2, 'qualifier': 'Synthetic dungeon/map context'},
            {'id': 'reward', 'kind': 'captureMiddleBossReward', 'dungeonDataId': 42, 'mapDataId': 420,
             'npcDataId': 700, 'observations': 1, 'captures': 1, 'qualifier': 'Synthetic reward data ID'}]
        npc_hash, map_hash = self.write_cache(self.npc_path, self.npc), self.write_cache(self.map_path, self.map)
        proof_hash = self.write_proof()

        def evidence(identity, kind, digest, **source):
            return {'id': identity, 'kind': kind, 'sourceRef': 'synthetic-source', 'sha256': digest,
                    'observations': 1, 'captures': 0, **source}

        self.recipe = {'schemaVersion': 1, 'version': 'synthetic-v1', 'region': 'global-eu',
                       'evidence': [evidence('npc-en', 'localNpcData', npc_hash, cachePath=self.npc_path),
                                    evidence('map-en', 'localMapData', map_hash, cachePath=self.map_path)],
                       'dungeons': [{'dungeonDataId': 42, 'mapDataId': 420,
                                     'mapSources': {'en': 'map-en', 'de': None}, 'sourceRefs': ['context'],
                                     'npcs': [{'npcDataId': 700, 'npcSources': {'en': 'npc-en', 'de': None},
                                               'role': 'intermediateBoss', 'roleSourceRefs': ['reward'],
                                               'endProof': 'middleBossReward', 'endSourceRefs': ['reward']}]}]}
        for fact in self.facts:
            self.recipe['evidence'].append(evidence(fact['id'], fact['kind'], proof_hash,
                                                     proofFile='observation-evidence.json'))
            self.evidence(fact['id']).update(observations=fact['observations'], captures=fact['captures'])

    @property
    def dungeon(self):
        return self.recipe['dungeons'][0]

    @property
    def npc_recipe(self):
        return self.dungeon['npcs'][0]

    def evidence(self, identity):
        return next(entry for entry in self.recipe['evidence'] if entry['id'] == identity)

    def write_cache(self, relative, document):
        target = self.cache / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        raw = encoded(document)
        target.write_bytes(raw)
        return hashlib.sha256(raw).hexdigest()

    def write_proof(self):
        raw = json.dumps({'schemaVersion': 1, 'facts': self.facts}).encode('utf-8')
        (self.proofs / 'observation-evidence.json').write_bytes(raw)
        return hashlib.sha256(raw).hexdigest()

    def refresh_npc(self):
        self.evidence('npc-en')['sha256'] = self.write_cache(self.npc_path, self.npc)

    def refresh_map(self):
        self.evidence('map-en')['sha256'] = self.write_cache(self.map_path, self.map)

    def refresh_proof(self):
        digest = self.write_proof()
        for entry in self.recipe['evidence']:
            if 'proofFile' in entry:
                entry['sha256'] = digest

    def build(self):
        return build_catalog(self.recipe, self.cache, self.proofs)

    def test_exact_variant_join_is_local_deterministic_and_omits_unneeded_fields(self):
        original = copy.deepcopy(self.recipe)
        with patch('socket.socket', side_effect=AssertionError('Network is forbidden')):
            result = self.build()
        self.assertEqual(result, self.build())
        self.assertEqual(self.recipe, original)
        dungeon, npc = result['dungeons'][0], result['dungeons'][0]['npcs'][0]
        self.assertEqual(dungeon['names'], {'en': 'Exact Dungeon', 'de': None})
        self.assertEqual(npc['names'], {'en': 'Exact NPC', 'de': None})
        self.assertEqual(npc['sourceRefs'], ['npc-en', 'reward'])
        self.assertEqual(dungeon['sourceRefs'], ['map-en', 'context'])
        self.assertEqual(npc['endProof'], 'middleBossReward')
        self.assertNotIn('positions', json.dumps(result))
        self.assertNotIn('Wrong family name', json.dumps(result))
        self.assertTrue(all(set(entry) == {'id', 'kind', 'sourceRef', 'sha256', 'observations', 'captures'}
                            for entry in result['evidence']))

    def test_top_level_aggregate_or_another_variant_cannot_replace_exact_variant_evidence(self):
        original = copy.deepcopy(self.npc)
        for change in ('availability', 'placement', 'missing', 'duplicate'):
            with self.subTest(change=change):
                self.npc = copy.deepcopy(original)
                if change == 'availability':
                    self.npc['variants'][0]['availability']['rootId'] = '421'
                elif change == 'placement':
                    self.npc['variants'][0]['placements'][0]['maps'][0]['id'] = '421'
                elif change == 'missing':
                    self.npc['id'] = '700'  # Top-level exact ID still cannot replace a missing variant.
                    self.npc['variants'][0]['id'] = '702'
                else:
                    self.npc['variants'].append(copy.deepcopy(self.npc['variants'][0]))
                self.refresh_npc()
                with self.assertRaises(ValueError):
                    self.build()

    def test_unvariant_source_requires_exact_top_id_without_family_conversion(self):
        self.npc = copy.deepcopy(self.variant)
        self.refresh_npc()
        self.assertEqual(self.build()['dungeons'][0]['npcs'][0]['npcDataId'], 700)
        self.npc['id'] = '2310700'
        self.refresh_npc()
        with self.assertRaisesRegex(ValueError, 'exact data ID'):
            self.build()

    def test_missing_variant_name_stays_null_instead_of_using_family_or_map_names(self):
        self.npc['variants'][0].pop('name')
        self.refresh_npc()
        self.assertEqual(self.build()['dungeons'][0]['npcs'][0]['names'], {'en': None, 'de': None})

    def test_numeric_join_can_use_other_locale_while_names_never_fall_back(self):
        other = copy.deepcopy(self.map)
        other['name'] = 'Synthetic German Dungeon'
        path = 'data/de/maps/420.d.json'
        source = copy.deepcopy(self.evidence('map-en'))
        source.update(id='map-de', cachePath=path, sha256=self.write_cache(path, other))
        self.recipe['evidence'].append(source)
        self.dungeon['mapSources'] = {'en': None, 'de': 'map-de'}
        result = self.build()['dungeons'][0]
        self.assertEqual(result['names'], {'en': None, 'de': 'Synthetic German Dungeon'})
        self.assertEqual(result['npcs'][0]['names'], {'en': 'Exact NPC', 'de': None})

    def test_cache_path_cannot_escape_or_select_non_data_roots(self):
        for path in ('../source.d.json', '/data/en/npcs/700.d.json', 'C:/source.d.json',
                     'data/en/npcs/../700.d.json', 'data/en/npcs\\700.d.json',
                     'images/en/700.d.json', 'data/en/npcs/700.d.json?query=1'):
            with self.subTest(path=path):
                self.evidence('npc-en')['cachePath'] = path
                with self.assertRaises(ValueError):
                    self.build()

    def test_proof_is_basename_only_and_requires_explicit_evidence_root(self):
        for path in ('../observation-evidence.json', 'nested/proof.json', 'C:\\proof.json'):
            with self.subTest(path=path):
                self.evidence('reward')['proofFile'] = path
                with self.assertRaises(ValueError):
                    self.build()
        self.evidence('reward')['proofFile'] = 'observation-evidence.json'
        with self.assertRaisesRegex(ValueError, 'evidence root'):
            build_catalog(self.recipe, self.cache)

    def test_symlinked_source_is_rejected_even_if_its_target_is_inside_cache(self):
        relative = 'data/en/npcs/link.d.json'
        link = self.cache / relative
        try:
            link.symlink_to(self.cache / self.npc_path)
        except OSError as error:
            self.skipTest(f'Creating symlinks is unavailable: {type(error).__name__}')
        self.evidence('npc-en')['cachePath'] = relative
        with self.assertRaisesRegex(ValueError, 'Linked source'):
            self.build()

    def test_hash_change_is_detected_before_invalid_source_json_is_parsed(self):
        (self.cache / self.npc_path).write_bytes(b'not JSON')
        with self.assertRaisesRegex(ValueError, 'SHA256 changed'):
            self.build()

    def test_exact_npc_must_also_exist_once_in_the_exact_map_detail(self):
        original = copy.deepcopy(self.map)
        for change in ('map-id', 'npc-id', 'duplicate'):
            with self.subTest(change=change):
                self.map = copy.deepcopy(original)
                if change == 'map-id':
                    self.map['id'] = '421'
                elif change == 'npc-id':
                    self.map['npcs'][0]['id'] = '701'
                else:
                    self.map['npcs'].append(copy.deepcopy(self.map['npcs'][0]))
                self.refresh_map()
                with self.assertRaises(ValueError):
                    self.build()

    def test_end_approval_requires_matching_npc_subject_kind_and_counts(self):
        recipe, facts = copy.deepcopy(self.recipe), copy.deepcopy(self.facts)
        for change in ('missing-end', 'different-npc', 'different-map', 'counts', 'local-end-ref'):
            with self.subTest(change=change):
                self.recipe, self.facts = copy.deepcopy(recipe), copy.deepcopy(facts)
                if change == 'missing-end':
                    self.npc_recipe['endSourceRefs'] = []
                elif change == 'different-npc':
                    self.facts[1]['npcDataId'] = 701
                elif change == 'different-map':
                    self.facts[1]['mapDataId'] = 421
                elif change == 'counts':
                    self.evidence('reward')['observations'] = 2
                else:
                    self.npc_recipe['endSourceRefs'].append('npc-en')
                self.refresh_proof()
                with self.assertRaises(ValueError):
                    self.build()

    def test_unknown_role_never_gets_end_approval_from_an_existing_reward_fact(self):
        self.npc_recipe['role'] = 'unknown'
        with self.assertRaisesRegex(ValueError, 'End approval'):
            self.build()
        self.npc_recipe['endProof'] = 'none'
        self.npc_recipe['endSourceRefs'] = []
        self.assertEqual(self.build()['dungeons'][0]['npcs'][0]['role'], 'unknown')

    def test_final_role_and_terminal_approval_require_two_captures(self):
        self.facts[1].update(kind='captureDeathDungeonSuccess', observations=2, captures=2)
        self.evidence('reward').update(kind='captureDeathDungeonSuccess', observations=2, captures=2)
        self.npc_recipe.update(role='finalBoss', endProof='dungeonSuccess')
        self.refresh_proof()
        self.assertEqual(self.build()['dungeons'][0]['npcs'][0]['endProof'], 'dungeonSuccess')
        self.facts[1]['captures'] = 1
        self.evidence('reward')['captures'] = 1
        self.refresh_proof()
        with self.assertRaisesRegex(ValueError, 'repeated death/success'):
            self.build()

    def test_captured_context_can_remain_unknown_without_a_local_npc_join(self):
        self.dungeon['mapSources'] = {'en': None, 'de': None}
        self.npc_recipe.update(npcSources={'en': None, 'de': None}, role='unknown',
                               endProof='none', endSourceRefs=[])
        result = self.build()['dungeons'][0]
        self.assertEqual(result['names'], {'en': None, 'de': None})
        self.assertEqual(result['npcs'][0]['names'], {'en': None, 'de': None})
        self.assertEqual(result['npcs'][0]['sourceRefs'], ['reward'])

    def test_djson_cycles_out_of_bounds_and_unbounded_depth_fail_closed(self):
        for document in ([{'self': 0}], [{'id': 8}], [{'next': index + 1} for index in range(66)] + ['end']):
            with self.subTest(document=document[:2]), self.assertRaises(ValueError):
                _decode(document)

    def test_cli_writes_only_generated_catalog_and_refuses_to_overwrite_an_input(self):
        recipe_path, output = self.proofs / 'recipe.json', self.root / 'generated' / 'catalog.json'
        recipe_path.write_text(json.dumps(self.recipe), encoding='utf-8')
        arguments = ['--recipe', str(recipe_path), '--cache', str(self.cache), '--out', str(output)]
        with contextlib.redirect_stdout(io.StringIO()):
            main(arguments)
        self.assertEqual(json.loads(output.read_text(encoding='utf-8')), self.build())
        arguments[-1] = str(recipe_path)
        before = recipe_path.read_bytes()
        with self.assertRaisesRegex(ValueError, 'Output must not replace'):
            main(arguments)
        self.assertEqual(recipe_path.read_bytes(), before)


if __name__ == '__main__':
    unittest.main()
