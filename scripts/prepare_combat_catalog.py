"""Build reviewed combat facts from hash-pinned local CDN cache files and anonymous proofs.

This offline developer tool never fetches data, opens a game client, reads live captures or infers
NPC families. Positions and other unneeded source fields never enter the generated catalog.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import tempfile


MAX_DOCUMENT = 1 << 20
MAX_SOURCE = 16 << 20
MAX_TOTAL_SOURCE = 64 << 20
MAX_NODES = 200_000
MAX_DEPTH = 64
CORE_EVIDENCE = {'id', 'kind', 'sourceRef', 'sha256', 'observations', 'captures'}
KINDS = {'localNpcData', 'localMapData', 'localActivityData', 'captureMiddleBossReward',
         'captureDeathDungeonSuccess', 'localReference', 'captureDungeonContext'}
CAPTURE_KINDS = {'captureMiddleBossReward', 'captureDeathDungeonSuccess', 'captureDungeonContext'}


def _object(value, required, optional=()):
    if not isinstance(value, dict) or set(value) - set(required) - set(optional) or set(required) - set(value):
        raise ValueError('Missing or unknown object fields')
    return value


def _array(value, maximum, minimum=0):
    if not isinstance(value, list) or not minimum <= len(value) <= maximum:
        raise ValueError('Invalid bounded collection')
    return value


def _text(value, maximum=512):
    if (not isinstance(value, str) or not value.strip() or len(value) > maximum
            or any(ord(char) < 32 or 127 <= ord(char) <= 159 for char in value)):
        raise ValueError('Invalid bounded text')
    return value


def _integer(value, minimum=0):
    if type(value) is not int or not minimum <= value <= 2_147_483_647:
        raise ValueError('Invalid integer')
    return value


def _data_id(value):
    # Canonical decimal strings represent the same exact data ID, never an inferred family.
    if isinstance(value, str) and re.fullmatch(r'[1-9][0-9]*', value):
        value = int(value)
    return _integer(value, 1)


def _unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError('Duplicate JSON property')
        result[key] = value
    return result


def _json(raw):
    try:
        return json.loads(raw.decode('utf-8-sig'), object_pairs_hook=_unique_object,
                          parse_constant=lambda _: (_ for _ in ()).throw(ValueError('Nonfinite JSON value')))
    except (UnicodeError, json.JSONDecodeError, RecursionError) as error:
        raise ValueError('Invalid source JSON') from error


def _decode(value):
    """The cached d.json reference form, with explicit bounds and no cyclic object graphs."""
    if not isinstance(value, list):
        if not isinstance(value, dict):
            raise ValueError('Expected a source object')
        return value
    _array(value, MAX_NODES, 1)
    memo, active = {}, set()

    def resolve(index, depth):
        if type(index) is not int or index < -1 or index >= len(value) or depth > MAX_DEPTH:
            raise ValueError('Invalid or too deep d.json reference')
        if index == -1:
            return None
        if index in active:
            raise ValueError('Cyclic d.json reference')
        if index in memo:
            return memo[index]
        active.add(index)
        item = value[index]
        if isinstance(item, dict):
            decoded = {key: resolve(ref, depth + 1) for key, ref in item.items()}
        elif isinstance(item, list):
            decoded = [resolve(ref, depth + 1) for ref in item]
        else:
            decoded = item
        active.remove(index)
        memo[index] = decoded
        return decoded

    result = resolve(0, 0)
    if not isinstance(result, dict):
        raise ValueError('Expected a decoded source object')
    return result


def _checked_root(root):
    root = Path(root)
    if root.is_symlink() or getattr(root, 'is_junction', lambda: False)():
        raise ValueError('Linked source roots are not allowed')
    resolved = root.resolve(strict=True)
    if not resolved.is_dir():
        raise ValueError('Source root is not a directory')
    return resolved


def _source_path(root, relative, proof=False):
    _text(relative, 512)
    if proof:
        if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]*\.json', relative) or '..' in relative:
            raise ValueError('Proof file must be a simple JSON basename')
    elif not re.fullmatch(r'data/(?:en|de)/[A-Za-z0-9_-]+(?:/[A-Za-z0-9_-]+)*\.d\.json', relative):
        raise ValueError('Unexpected local CDN cache path')
    parts = PurePosixPath(relative).parts
    candidate = root
    for part in parts:
        candidate = candidate / part
        if candidate.is_symlink() or getattr(candidate, 'is_junction', lambda: False)():
            raise ValueError('Linked source paths are not allowed')
    resolved = candidate.resolve(strict=True)
    if not resolved.is_relative_to(root) or not resolved.is_file():
        raise ValueError('Source must stay inside its designated root')
    return resolved


class _Sources:
    def __init__(self, cache_root, evidence_root):
        self.cache_root = _checked_root(cache_root)
        self.evidence_root = None if evidence_root is None else _checked_root(evidence_root)
        self.loaded = {}
        self.total_bytes = 0

    def read(self, evidence):
        proof = 'proofFile' in evidence
        root = self.evidence_root if proof else self.cache_root
        if root is None:
            raise ValueError('An evidence root is required for proof files')
        path = _source_path(root, evidence['proofFile' if proof else 'cachePath'], proof)
        expected = evidence['sha256'].lower()
        if path in self.loaded:
            digest, result = self.loaded[path]
            if digest != expected:
                raise ValueError('Conflicting source hashes')
            return result
        with path.open('rb') as source:
            raw = source.read(MAX_SOURCE + 1)
        self.total_bytes += len(raw)
        if len(raw) > MAX_SOURCE or self.total_bytes > MAX_TOTAL_SOURCE:
            raise ValueError('Source byte limit exceeded')
        digest = hashlib.sha256(raw).hexdigest()
        if digest != expected:
            raise ValueError('Source SHA256 changed')
        parsed = _json(raw)  # Hash verification deliberately precedes parsing or decoding.
        result = parsed if proof else _decode(parsed)
        self.loaded[path] = digest, result
        return result


def _evidence(recipe, sources):
    evidence, documents, facts = {}, {}, {}
    for entry in _array(recipe, 1000):
        _object(entry, CORE_EVIDENCE, {'cachePath', 'proofFile'})
        identity = _text(entry['id'], 128)
        if identity in evidence or ('cachePath' in entry) == ('proofFile' in entry):
            raise ValueError('Duplicate evidence ID or missing/ambiguous source file')
        if _text(entry['kind'], 64) not in KINDS:
            raise ValueError('Unknown evidence kind')
        _text(entry['sourceRef'], 1024)
        if not isinstance(entry['sha256'], str) or not re.fullmatch(r'[0-9a-fA-F]{64}', entry['sha256']):
            raise ValueError('Invalid SHA256')
        observations = _integer(entry['observations'], 1)
        captures = _integer(entry['captures'])
        if captures > observations or entry['kind'] in CAPTURE_KINDS and captures == 0:
            raise ValueError('Invalid evidence counts')
        if entry['kind'] in CAPTURE_KINDS and 'proofFile' not in entry:
            raise ValueError('Capture evidence requires a proof file')
        document = sources.read(entry)
        if 'proofFile' in entry:
            _object(document, {'schemaVersion', 'facts'})
            if _integer(document['schemaVersion'], 1) != 1:
                raise ValueError('Unknown proof schema')
            by_id = {}
            for fact in _array(document['facts'], 1000):
                _object(fact, {'id', 'kind', 'dungeonDataId', 'mapDataId', 'npcDataId',
                               'observations', 'captures', 'qualifier'})
                fact_id = _text(fact['id'], 128)
                if fact_id in by_id or _text(fact['kind'], 64) not in KINDS:
                    raise ValueError('Invalid proof identity or kind')
                _data_id(fact['dungeonDataId'])
                _data_id(fact['mapDataId'])
                if fact['npcDataId'] is not None:
                    _data_id(fact['npcDataId'])
                fact_observations = _integer(fact['observations'], 1)
                fact_captures = _integer(fact['captures'])
                if (fact_captures > fact_observations or fact['kind'] in CAPTURE_KINDS and fact_captures == 0
                        or fact['kind'] == 'captureDungeonContext' and fact['npcDataId'] is not None
                        or fact['kind'] in {'captureMiddleBossReward', 'captureDeathDungeonSuccess'}
                        and fact['npcDataId'] is None):
                    raise ValueError('Invalid proof counts or subject')
                _text(fact['qualifier'], 1024)
                by_id[fact_id] = fact
            fact = by_id.get(identity)
            if fact is None or any(fact[key] != entry[key] for key in ('kind', 'observations', 'captures')):
                raise ValueError('Evidence kind or counts do not match its proof')
            if fact['kind'] == 'captureDungeonContext' and fact['npcDataId'] is not None:
                raise ValueError('Dungeon-context proof must not name an NPC')
            facts[identity] = fact
        evidence[identity] = {key: entry[key] for key in ('id', 'kind', 'sourceRef', 'sha256', 'observations', 'captures')}
        documents[identity] = document
    return evidence, documents, facts


def _refs(values, evidence):
    result = [_text(value, 128) for value in _array(values, 64)]
    if len(set(result)) != len(result) or any(value not in evidence for value in result):
        raise ValueError('Unknown or duplicate evidence reference')
    return result


def _locale_sources(value, evidence, kind):
    _object(value, set(), {'en', 'de'})
    result = {}
    for locale in ('en', 'de'):
        identity = value.get(locale)
        if identity is None:
            continue
        if not isinstance(identity, str) or identity not in evidence or evidence[identity]['kind'] != kind:
            raise ValueError('Unexpected locale source kind')
        result[locale] = identity
    return result


def _name(document):
    value = document.get('name')
    return None if value is None else _text(value, 256)


def _exact_npc(document, npc_id):
    variants = document.get('variants')
    if variants is not None:
        exact = [entry for entry in _array(variants, 1000) if isinstance(entry, dict)
                 and _data_id(entry.get('id')) == npc_id]
        if len(exact) != 1:
            raise ValueError('Exact NPC variant is missing or ambiguous')
        selected = exact[0]
    else:
        if _data_id(document.get('id')) != npc_id:
            raise ValueError('NPC ID differs from requested exact data ID')
        selected = document
    name = _name(selected)
    if name is None and _data_id(document.get('id')) == npc_id:
        name = _name(document)
    return selected, name


def _npc_join(selected, npc_id, map_id, maps):
    availability = selected.get('availability')
    if (not isinstance(availability, dict) or availability.get('rootType') != 'map'
            or _data_id(availability.get('rootId')) != map_id):
        raise ValueError('Exact NPC variant availability does not match map')
    placements = _array(selected.get('placements'), 10000, 1)
    found = False
    for placement in placements:
        if not isinstance(placement, dict):
            raise ValueError('Invalid NPC placement')
        for entry in _array(placement.get('maps'), 1000):
            if not isinstance(entry, dict):
                raise ValueError('Invalid placement map')
            found |= _data_id(entry.get('id')) == map_id
    if not found:
        raise ValueError('Exact NPC variant placement does not match map')
    if not maps or not all(sum(isinstance(entry, dict) and _data_id(entry.get('id')) == npc_id
                               for entry in _array(document.get('npcs'), 10000)) == 1 for document in maps):
        raise ValueError('Exact NPC ID is absent or duplicated in map detail')


def _proof_context(refs, facts, dungeon_id, map_id, npc_id=None, exact_npc=False):
    for identity in refs:
        if identity not in facts:
            continue
        fact = facts[identity]
        subject = fact['npcDataId']
        if (_data_id(fact['dungeonDataId']) != dungeon_id or _data_id(fact['mapDataId']) != map_id
                or exact_npc and (subject is None or _data_id(subject) != npc_id)
                or not exact_npc and subject is not None and _data_id(subject) != npc_id):
            raise ValueError('Proof subject does not match recipe context')


def _has_proof(refs, facts, kind, captures):
    return any(identity in facts and facts[identity]['kind'] == kind
               and facts[identity]['captures'] >= captures for identity in refs)


def build_catalog(recipe: dict, cache_root: Path, evidence_root=None) -> dict:
    """Read only hash-pinned local inputs and return Core schema 1 without mutating the recipe."""
    _object(recipe, {'schemaVersion', 'version', 'region', 'evidence', 'dungeons'})
    if _integer(recipe['schemaVersion'], 1) != 1:
        raise ValueError('Unknown recipe schema')
    version, region = _text(recipe['version'], 128), _text(recipe['region'], 64)
    sources = _Sources(cache_root, evidence_root)
    evidence, documents, facts = _evidence(recipe['evidence'], sources)
    entries = {entry['id']: entry for entry in recipe['evidence']}
    dungeons, dungeon_ids, npc_total = [], set(), 0
    for dungeon in _array(recipe['dungeons'], 100):
        _object(dungeon, {'dungeonDataId', 'mapDataId', 'mapSources', 'sourceRefs', 'npcs'})
        dungeon_id, map_id = _data_id(dungeon['dungeonDataId']), _data_id(dungeon['mapDataId'])
        if dungeon_id in dungeon_ids:
            raise ValueError('Duplicate dungeon ID or join')
        dungeon_ids.add(dungeon_id)
        map_sources = _locale_sources(dungeon['mapSources'], evidence, 'localMapData')
        refs = _refs(dungeon['sourceRefs'], evidence)
        _proof_context(refs, facts, dungeon_id, map_id)
        map_documents, names = [], {'en': None, 'de': None}
        for locale, identity in map_sources.items():
            if PurePosixPath(entries[identity].get('cachePath', '')).parts[1:2] != (locale,):
                raise ValueError('Map source locale does not match its cache path')
            document = documents[identity]
            if not isinstance(document, dict) or _data_id(document.get('id')) != map_id:
                raise ValueError('Map detail ID differs from requested data ID')
            names[locale] = _name(document)
            map_documents.append(document)
        if not map_sources and not _has_proof(refs, facts, 'captureDungeonContext', 1):
            raise ValueError('Dungeon has no reviewed map or captured context')
        npcs, npc_ids = [], set()
        for npc in _array(dungeon['npcs'], 1000):
            _object(npc, {'npcDataId', 'npcSources', 'role', 'roleSourceRefs', 'endProof', 'endSourceRefs'})
            npc_id = _data_id(npc['npcDataId'])
            npc_total += 1
            if npc_id in npc_ids or npc_total > 1000:
                raise ValueError('Duplicate NPC ID or global NPC limit exceeded')
            npc_ids.add(npc_id)
            npc_sources = _locale_sources(npc['npcSources'], evidence, 'localNpcData')
            role_refs, end_refs = _refs(npc['roleSourceRefs'], evidence), _refs(npc['endSourceRefs'], evidence)
            if any(identity not in facts or facts[identity]['kind'] not in
                   {'captureMiddleBossReward', 'captureDeathDungeonSuccess'} for identity in end_refs):
                raise ValueError('End references require NPC-specific capture facts')
            _proof_context(role_refs, facts, dungeon_id, map_id, npc_id)
            _proof_context(end_refs, facts, dungeon_id, map_id, npc_id, True)
            npc_names = {'en': None, 'de': None}
            for locale, identity in npc_sources.items():
                if PurePosixPath(entries[identity].get('cachePath', '')).parts[1:2] != (locale,):
                    raise ValueError('NPC source locale does not match its cache path')
                selected, npc_names[locale] = _exact_npc(documents[identity], npc_id)
                _npc_join(selected, npc_id, map_id, map_documents)
            role, proof = _text(npc['role'], 64), _text(npc['endProof'], 64)
            if role not in {'unknown', 'intermediateBoss', 'finalBoss'} or proof not in {'none', 'middleBossReward', 'dungeonSuccess'}:
                raise ValueError('Unknown recipe role or end proof')
            if role != 'unknown' and (not npc_sources or not map_sources):
                raise ValueError('Known NPC role requires exact local NPC and map joins')
            if role == 'intermediateBoss' and not _has_proof(role_refs, facts, 'captureMiddleBossReward', 1):
                raise ValueError('Intermediate role has no matching reward fact')
            if role == 'finalBoss' and not _has_proof(role_refs, facts, 'captureDeathDungeonSuccess', 2):
                raise ValueError('Final role has no matching repeated death/success fact')
            if (proof == 'middleBossReward' and (role != 'intermediateBoss' or not _has_proof(end_refs, facts, 'captureMiddleBossReward', 1))
                    or proof == 'dungeonSuccess' and (role != 'finalBoss' or not _has_proof(end_refs, facts, 'captureDeathDungeonSuccess', 2))
                    or role == 'unknown' and proof != 'none'):
                raise ValueError('End approval lacks matching role and capture evidence')
            npc_refs = list(dict.fromkeys([*npc_sources.values(), *role_refs]))
            if not npc_refs:
                raise ValueError('NPC has no source evidence')
            npcs.append({'npcDataId': npc_id, 'names': npc_names, 'role': role, 'sourceRefs': npc_refs,
                         'endProof': proof, 'endSourceRefs': end_refs})
        dungeons.append({'dungeonDataId': dungeon_id, 'mapDataId': map_id, 'names': names,
                         'sourceRefs': list(dict.fromkeys([*map_sources.values(), *refs])), 'npcs': npcs})
    result = {'schemaVersion': 1, 'version': version, 'region': region,
              'evidence': list(evidence.values()), 'dungeons': dungeons}
    if len(json.dumps(result, ensure_ascii=False, allow_nan=False).encode('utf-8')) > MAX_DOCUMENT:
        raise ValueError('Generated catalog exceeds one MiB')
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--recipe', required=True, type=Path)
    parser.add_argument('--cache', required=True, type=Path)
    parser.add_argument('--out', required=True, type=Path)
    args = parser.parse_args(argv)
    with args.recipe.open('rb') as source:
        raw = source.read(MAX_DOCUMENT + 1)
    if len(raw) > MAX_DOCUMENT:
        raise ValueError('Recipe exceeds one MiB')
    recipe = _json(raw)
    result = build_catalog(recipe, args.cache, args.recipe.parent)
    inputs = {args.recipe.resolve()}
    for entry in recipe['evidence']:
        root = _checked_root(args.recipe.parent if 'proofFile' in entry else args.cache)
        inputs.add(_source_path(root, entry.get('proofFile', entry.get('cachePath')), 'proofFile' in entry))
    if args.out.resolve() in inputs:
        raise ValueError('Output must not replace a source or the recipe')
    args.out.parent.mkdir(parents=True, exist_ok=True)
    content = json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False) + '\n'
    if len(content.encode('utf-8')) > MAX_DOCUMENT:
        raise ValueError('Formatted catalog exceeds one MiB')
    with tempfile.NamedTemporaryFile(mode='w', encoding='utf-8', newline='\n', dir=args.out.parent,
                                     prefix=args.out.name + '.', suffix='.tmp', delete=False) as target:
        temporary = Path(target.name)
        target.write(content)
    try:
        os.replace(temporary, args.out)
    finally:
        temporary.unlink(missing_ok=True)
    print(f'Prepared {len(result["dungeons"])} dungeons and '
          f'{sum(len(dungeon["npcs"]) for dungeon in result["dungeons"])} NPC facts locally.')


if __name__ == '__main__':
    main()
