"""Read-only validation of relocated hosted replay evidence. Never executes artifacts."""
from collections import Counter
import json
from pathlib import Path, PurePosixPath
import posixpath
import re
import tarfile

import replay
import replay_ci

IDENTITY = 'ChunkMaestro-private-stryker5-no-block-filter'
TERMINAL = {'Killed', 'Survived', 'NoCoverage', 'Timeout', 'CompileError', 'Ignored'}
CONFIG = 'tooling/quality/storage/replay/persistence.json'
GENERATED = 'src/Stryker.Core/Stryker.Core/Reporters/Html/Files/dist/mutation-test-elements.js'


def require(condition, message):
    if not condition:
        raise ValueError('Replay evidence: ' + message)


def load(path):
    return json.loads(path.read_bytes())


def child(folder, name):
    """Artifact members are relative POSIX names, never host paths or symlinks."""
    require(isinstance(name, str) and '\\' not in name, 'invalid artifact path')
    parts = PurePosixPath(name)
    require(not parts.is_absolute() and name == parts.as_posix()
            and all(p not in ('..', '.', '') and ':' not in p for p in parts.parts), 'artifact path escapes folder')
    path = folder / name
    require(path.resolve().is_relative_to(folder.resolve()), 'artifact path escapes folder')
    require(not any(p.is_symlink() for p in (path, *path.parents) if p.is_relative_to(folder)), 'symlink artifact')
    return path


def verify_files(folder, expected, exact=False):
    require(isinstance(expected, dict) and expected, 'missing file inventory')
    for name, digest in expected.items():
        require(replay.digest(child(folder, name).read_bytes()) == digest, 'changed artifact: ' + name)
    if exact:
        require(replay_ci.inventory(folder) == expected, 'artifact inventory differs')


def logical(name, marker):
    normalized = posixpath.normpath(name.replace('\\', '/'))
    # A host prefix may differ, but the repository-relative suffix must be unique.
    require(normalized.count(marker) == 1, 'ambiguous source path')
    suffix = normalized[normalized.index(marker):]
    require('..' not in PurePosixPath(suffix).parts and ':' not in suffix, 'source path escapes checkout')
    return suffix


def catalogue(path, root):
    """Reconstruct the runner catalogue with host-independent source paths."""
    raw = path.read_bytes()
    report = json.loads(raw)
    require(report.get('files'), 'empty Persistence report')
    rows = []
    for name, file in report['files'].items():
        relative = logical(name, 'src/ChunkMaestro.Persistence/')
        source = file['source']
        require(child(root, relative).read_bytes() == source.encode('utf-8'), 'reported source differs from checkout')
        require(not re.search(r'^\s*#line\b', source, re.M), 'mapped source is unresolved')
        for mutant in file['mutants']:
            require(mutant['status'] in TERMINAL, 'incomplete mutant')
            start, end = (replay.offset(source, mutant['location'][k]) for k in ('start', 'end'))
            require(end >= start, 'reversed mutation span')
            identity = [relative, replay.digest(source.encode()), mutant['location'], mutant['mutatorName'], mutant['replacement']]
            rows.append({'ordinal': len(rows), 'identity': replay.digest(json.dumps(identity, sort_keys=True).encode()),
                         'file': relative, 'source_sha256': replay.digest(source.encode()), 'original': mutant,
                         'span_utf16': [start, end],
                         'span_sha256': replay.digest(source.encode('utf-16-le')[start * 2:end * 2]),
                         'report_sha256': replay.digest(raw), 'supplemental_status': 'NOT_RUN'})
    require(rows and len({r['identity'] for r in rows}) == len(rows), 'empty or ambiguous mutation catalogue')
    return rows


def test_bindings(path, root, cases):
    report = load(path)
    known = {'.'.join([case[0][0], case[0][1]]) for case in cases}
    tests = {}
    for name, file in report['testFiles'].items():
        relative = logical(name, 'tests/ChunkMaestro.PersistenceTests/')
        require(child(root, relative).read_bytes() == file['source'].encode('utf-8'), 'mutation test source differs')
        for test in file['tests']:
            require(test['id'] not in tests and test['name'] in known, 'unbound or duplicate test identity')
            tests[test['id']] = test['name']
    require(tests, 'missing mutation test definitions')
    for file in report['files'].values():
        for mutant in file['mutants']:
            if mutant['status'] == 'Killed':
                require(mutant.get('killedBy') and all(k in tests for k in mutant['killedBy']), 'kill lacks bound failing test')


def private_tool(folder, result, root):
    provenance_path = folder / 'provenance.json'
    provenance = load(provenance_path)
    require(result['provenance_sha256'] == replay.digest(provenance_path.read_bytes()), 'private provenance changed')
    require(provenance['schema_version'] == 1 and provenance['identity'] == IDENTITY
            and provenance['official_tool'] is False and provenance['upstream_commit'] == replay.COMMIT
            and provenance['archive_sha256'] == replay.ARCHIVE_SHA
            and provenance['archive_url'] == replay.ARCHIVE_URL
            and provenance['factory_before_sha256'] == replay.FACTORY_SHA
            and provenance['factory_after_sha256'] == replay.PATCHED_FACTORY_SHA
            and provenance['builder_sha256'] == replay.digest((root / 'tooling/quality/storage/replay.py').read_bytes())
            and provenance['sdk_executable_sha256'] == result['sdk_sha256'], 'private tool identity differs')
    require(replay.digest((folder / 'official-source.tar.gz').read_bytes()) == replay.ARCHIVE_SHA
            and replay.digest((folder / 'filter.patch').read_bytes()) == provenance['patch_sha256'], 'private archive or patch changed')
    before, after, built = (provenance[k] for k in ('source_before', 'source_after', 'source_after_build'))
    with tarfile.open(folder / 'official-source.tar.gz') as archive:
        archived = {'/'.join(PurePosixPath(m.name).parts[1:]): replay.digest(archive.extractfile(m).read())
                    for m in archive if m.isfile()}
    require(before == archived, 'private source inventory differs from pinned archive')
    require(before.keys() == after.keys() and {k for k in before if before[k] != after[k]} == {replay.FACTORY}
            and before[replay.FACTORY] == replay.FACTORY_SHA and after[replay.FACTORY] == replay.PATCHED_FACTORY_SHA,
            'private source delta is broader than the reviewed filter')
    require(set(built) - set(after) == {GENERATED} and all(built[k] == v for k, v in after.items())
            and built[GENERATED] == replay_ci.REPORTER_SHA, 'private build changed source or generated reporter')
    source = folder / ('stryker-net-' + replay.COMMIT)
    require(replay.hashes(source) == built, 'private built source inventory differs')
    verify_files(source, provenance['dependencies_and_outputs'])
    require(not provenance['build']['timeout'] and provenance['build']['exit_code'] == 0
            and provenance['compiler_files'], 'private build failed or compiler provenance missing')
    return provenance


def credited(rows):
    """Original compile errors stay excluded; later errors never reduce eligibility."""
    return [row for row in rows if row['original']['status'] != 'CompileError'
            and (row['original']['status'] == 'Killed' or row['supplemental_status'] == 'Killed')]


def validate(folder, manifest, root):
    """Return validated actual outcomes, retaining original compile-error eligibility."""
    collection = load(folder / 'replay-collection.json')
    require(collection['schema_version'] == 1 and collection['status'] == 'COLLECTED'
            and collection['platform'] == manifest['platform'] and collection['private_identity'] == IDENTITY,
            'collection incomplete or wrong platform')
    trust = collection['trust']
    require(trust['head'] == manifest['head'] and trust['repository'] == replay_ci.REPOSITORY
            and trust['actor_id'] == replay_ci.OWNER_ID and trust['event'] in ('pull_request', 'workflow_dispatch'),
            'untrusted or stale collection')
    allowed = set(replay_ci.child_environment({})) | replay_ci.ENV_KEYS
    require(set(collection['environment_keys']) <= allowed, 'credential-capable environment key')
    require(set(collection['relocated']) == {'replay', 'replay-private'}, 'incomplete relocation')
    for name, inventory in collection['relocated'].items():
        verify_files(folder / name, inventory, exact=True)
    directory = folder / 'replay'
    result = load(directory / 'result.json')
    require(result['schema_version'] == 1 and result['category'] == 'production-experiment'
            and result['status'] == 'COLLECTED' and result['head'] == manifest['head']
            and result['baseline_restored'] is True and result['inputs_preserved'] is True
            and result['architecture'] == collection['architecture']
            and result['thresholds'] == {'line': 90, 'branch': 85, 'mutation': 80}
            and result['mutation_directory'] == 'tooling/quality/storage', 'wrong replay binding or unresolved restoration')
    binding = load(directory / 'binding.json')
    require(binding.get('status') == 'UNRESOLVED'
            and all(result.get(k) == v for k, v in binding.items() if k != 'status'), 'initial frozen binding differs')
    require(result['inputs'] == replay.workspace_hashes(root, False)
            and result['runner_sha256'] == replay.digest((root / 'tooling/quality/storage/replay.py').read_bytes())
            and result['config_sha256'] == replay.digest((root / CONFIG).read_bytes())
            and result['sdk_version'] == collection['sdk_version'] == load(root / 'global.json')['sdk']['version'],
            'replay inputs, runner, configuration or SDK differ')
    require(replay.hashes(directory) == result['artifacts'] | {'result.json': replay.digest((directory / 'result.json').read_bytes())},
            'runner artifact inventory differs')
    provenance = private_tool(folder / 'replay-private', result, root)
    cases = replay.test_cases(directory / 'baseline')
    require(cases == replay.test_cases(directory / 'restored') == replay.test_cases(folder / 'coverage')
            and json.loads(json.dumps(cases)) == result['baseline_cases'], 'baseline/restoration/runtime test identities differ')
    before, after = (load(directory / (label + '-binaries.json')) for label in ('baseline', 'restored'))
    for assembly in ('Domain', 'Application', 'Persistence', 'PersistenceTests'):
        selected = {k: v for k, v in before.items() if k.endswith('/ChunkMaestro.' + assembly + '.dll')}
        require(selected and all(after.get(k) == v for k, v in selected.items()), 'restored product/test assemblies differ')
    official, patched = (catalogue(directory / (label + '.json'), root) for label in ('official', 'private-patched'))
    require(official == load(directory / 'catalogue.json') and patched == load(directory / 'patched-catalogue.json'), 'catalogue changed')
    require(set(r['identity'] for r in official) == set(r['identity'] for r in patched), 'replay catalogue identities differ')
    require(result['official_report_sha256'] == replay.digest((directory / 'official.json').read_bytes())
            and result['original_counts'] == dict(Counter(r['original']['status'] for r in official))
            and result['fixed_applicable_denominator'] == sum(r['original']['status'] != 'CompileError' for r in official),
            'original catalogue denominator changed')
    supplemented = list(replay.supplement(official, patched))
    ledger = [json.loads(line) for line in (directory / 'ledger.jsonl').read_text(encoding='utf-8').splitlines()]
    require(ledger == official + supplemented, 'append-only ledger differs from raw observations')
    for label in ('official', 'private-patched'):
        test_bindings(directory / (label + '.json'), root, cases)
    # Host paths are retained in immutable records. Resolve only paths below the
    # collector-owned temporary root; never open an artifact-supplied host path.
    def relocated(name):
        prefix = collection['original_temporary_root'].replace('\\', '/').rstrip('/') + '/'
        normalized = name.replace('\\', '/')
        require(normalized.startswith(prefix), 'command references outside retained temporary root')
        return child(folder, normalized[len(prefix):])
    require(replay.digest(relocated(provenance['build']['log']).read_bytes()) == provenance['build']['log_sha256'], 'private build log changed')
    require(replay.digest(relocated(provenance['binary']).read_bytes())
            == provenance['dependencies_and_outputs']['src/Stryker.CLI/Stryker.CLI/bin/Release/Stryker.CLI.dll'], 'private binary changed')
    require(result['official_tool_files'].get('Stryker.CLI.dll') == result['official_binary_sha256'], 'official binary binding missing')
    commands = [json.loads(line) for line in (directory / 'commands.jsonl').read_text(encoding='utf-8').splitlines()]
    require(len(commands) == 9, 'missing or extra replay commands')
    for index, command in enumerate(commands):
        require(not command['timeout'] and replay.digest(relocated(command['log']).read_bytes()) == command['log_sha256'], 'command timed out or log changed')
        if index in (4, 5):
            label = ('official', 'private-patched')[index - 4]
            require(command['exit_code'] in (0, 2) and result['tool_threshold_failures'][label] == (command['exit_code'] == 2), 'mutation command failed')
            rows = official if label == 'official' else patched
            scored = [r['original']['status'] for r in rows if r['original']['status'] not in ('CompileError', 'Ignored')]
            require(scored and (command['exit_code'] == 2) == (100 * sum(s in ('Killed', 'Timeout') for s in scored) / len(scored) < 80), 'threshold exit differs from raw report')
            reports = list((directory / label).rglob('mutation-report.json'))
            require(len(reports) == 1 and reports[0].read_bytes() == (directory / (label + '.json')).read_bytes(), 'report copy differs from original output')
            require('--config-file' in command['args'] and command['args'][command['args'].index('--config-file') + 1].replace('\\', '/').endswith('/' + CONFIG), 'mutation config differs')
            require(command['args'][1].replace('\\', '/') == provenance['binary'].replace('\\', '/')
                    if label == 'private-patched' else command['args'][1].replace('\\', '/').endswith('/Stryker.CLI.dll'), 'mutation tool differs')
        else:
            require(command['exit_code'] == 0 and command['args'][1] == ('restore', 'clean', 'build', 'test', None, None, 'clean', 'build', 'test')[index], 'baseline command failed or missing')
            require(command['args'][2].replace('\\', '/').endswith('/tooling/quality/storage/MutationTests.csproj'), 'baseline uses another project')
    return supplemented
