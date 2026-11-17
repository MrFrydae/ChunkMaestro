"""Owner-only bounded source-shard pilot. Never full qualification or global PASS."""
import argparse
from collections import Counter
from contextlib import contextmanager
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import time

import qualify
import replay
import replay_ci
import replay_gate
import shards

ROOT = Path(__file__).resolve().parents[3]
SCOPE = 'storage-source-shard-pilot'
PREPARE_SECONDS = 420
PROBE_SECONDS = 480
RESTORE_RESERVE = 90
INDEX_SECONDS = 180
MUTATION_SECONDS = 300
GENERATED = 'src/Stryker.Core/Stryker.Core/Reporters/Html/Files/dist/mutation-test-elements.js'
TEST_PROJECT = 'tooling/quality/storage/MutationTests.csproj'


def require(value, message):
    if not value:
        raise ValueError('Shard pilot: ' + message)


def load(path):
    return json.loads(path.read_bytes())


def inventory(folder):
    result = {}
    for path in sorted(folder.rglob('*')):
        require(not path.is_symlink(), 'symlink artifact')
        if path.is_file() and path != folder / 'bundle.json':
            relative = path.relative_to(folder).as_posix()
            replay_gate.child(folder, relative)
            result[relative] = replay.digest(path.read_bytes())
    return result


def context(root=ROOT):
    head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True, timeout=15).strip()
    event = load(Path(os.environ['GITHUB_EVENT_PATH']))
    trust = replay_ci.trusted_context(os.environ, event, head)
    require(platform.system() in ('Windows', 'Darwin'), 'unsupported controller platform')
    require(not subprocess.check_output(['git', 'status', '--porcelain', '--untracked-files=normal'],
                                       cwd=root, text=True, timeout=15).strip(), 'dirty candidate checkout')
    run = os.environ.get('GITHUB_RUN_ID', '')
    attempt = os.environ.get('GITHUB_RUN_ATTEMPT', '')
    require(run.isdecimal() and int(run) > 0 and attempt.isdecimal() and int(attempt) > 0,
            'hosted run/attempt binding unavailable')
    return {'head': head, 'trust': trust, 'platform': platform.system(),
            'architecture': platform.machine(), 'run_id': run, 'run_attempt': attempt}


def sdk(root, env):
    executable = Path(shutil.which(env.get('CHUNKMAESTRO_DOTNET', 'dotnet'), path=env.get('PATH')) or '').resolve()
    require(executable.is_file(), 'pinned SDK executable unavailable')
    version = subprocess.check_output([str(executable), '--version'], cwd=root, env=env, text=True, timeout=20).strip()
    require(version == load(root / 'global.json')['sdk']['version'], 'SDK pin mismatch')
    env.update(DOTNET_ROOT=str(executable.parent), PATH=str(executable.parent) + os.pathsep + env.get('PATH', ''))
    return executable, version, replay.digest(executable.read_bytes())


class Budget:
    def __init__(self, seconds, env):
        self.started = time.monotonic()
        self.seconds = seconds
        self.env = env
        self.records = []
        self.runner = replay.command

    def remaining(self):
        return self.seconds - (time.monotonic() - self.started)

    def command(self, args, cwd, directory, label, env=None, timeout=180, reserve=0):
        left = self.remaining() - reserve
        require(left > 0, f'{label}: shared command budget exhausted before launch')
        record = self.runner(args, cwd, directory, label, self.env if env is None else env, min(timeout, left))
        self.records.append(record)
        return record

    def execute(self, args, root, output, label, timeout=180, reserve=0):
        record = self.command(args, root, output, label, timeout=timeout, reserve=reserve)
        replay.require(record)
        return record

    @contextmanager
    def private_builder(self):
        previous = replay.command
        replay.command = self.command
        try:
            yield
        finally:
            replay.command = previous


def baseline_cases(directory):
    return json.loads(shards.canonical(replay.test_cases(directory)))


def expected_sources(root):
    return {path.relative_to(root).as_posix(): replay.digest(path.read_bytes())
            for path in sorted((root / 'src/ChunkMaestro.Persistence').rglob('*.cs'))
            if not {'bin', 'obj'}.intersection(path.parts)
            and not path.name.endswith(('.Designer.cs', 'ModelSnapshot.cs'))}


def methods(cases):
    return {'.'.join(case[0]) for case in cases}


def bind_tests(report, root, cases, workspace_root=None):
    tests = shards.bound_tests(load(report), root, methods(cases), workspace_root)
    require(Counter(tests.values()) == Counter('.'.join(case[0]) for case in cases),
            'mutation report test-case counts differ from complete baseline')
    return tests


def catalogue_masks(catalogue):
    return {row['identity']: row['original']['status'] == 'CompileError' for row in catalogue['rows']}


def index_catalogue(report, root, sources, cases, workspace_root=None):
    value = shards.read_catalogue(report, root, workspace_root)
    require(value['sources'] == sources, 'index omitted or changed a declared source file')
    require(all(row['original']['status'] in ('Ignored', 'CompileError') for row in value['rows']),
            'planning index executed mutations or is incomplete')
    bind_tests(report, root, cases, workspace_root)
    return value


def private_bundle(folder, bundle, root, dotnet):
    """Verify bytes before execution; never rewrite producer paths in provenance."""
    private = replay_gate.child(folder, 'private')
    provenance = replay_gate.private_tool(private,
        {'provenance_sha256': bundle['provenance_sha256'], 'sdk_sha256': bundle['sdk_sha256']}, root)
    require(provenance['source_after_build'].get(GENERATED) == replay_ci.REPORTER_SHA,
            'unreviewed generated private reporter')
    # SDK compiler paths normally match on homogeneous hosted images. Fail if
    # they do not; never reinterpret an arbitrary producer-supplied file path.
    compiler_root = dotnet.parent.resolve()
    for name, digest in provenance['compiler_files'].items():
        path = Path(name).resolve()
        require(path.is_relative_to(compiler_root) and path.is_file()
                and replay.digest(path.read_bytes()) == digest, 'SDK compiler provenance differs')
    binary = replay_gate.child(private, bundle['private_binary_relative'])
    require(binary.is_file() and replay.digest(binary.read_bytes()) == bundle['private_tool_sha256'],
            'private binary changed')
    require(bundle['private_binary_relative'] == 'stryker-net-' + replay.COMMIT +
            '/src/Stryker.CLI/Stryker.CLI/bin/Release/Stryker.CLI.dll', 'unexpected private entry point')
    # The build log remains in the private tree. Producer absolute roots are
    # mapped only below the authenticated original bundle root.
    prefix = bundle['original_bundle_root'].replace('\\', '/').rstrip('/') + '/'
    log_name = provenance['build']['log'].replace('\\', '/')
    require(log_name.startswith(prefix), 'private build log outside original bundle')
    log = replay_gate.child(folder, log_name[len(prefix):])
    require(replay.digest(log.read_bytes()) == provenance['build']['log_sha256'], 'private build log changed')
    return binary


def validate_bundle(folder, root, current, dotnet, version, sdk_hash):
    """Read-only preflight; a self-consistent bundle is not source authenticity."""
    folder = folder.resolve()
    bundle = load(folder / 'bundle.json')
    require(bundle['schema_version'] == 1 and bundle['scope'] == SCOPE
            and bundle['status'] == 'PREPARED_DIAGNOSTIC' and bundle['qualification'] == 'NOT_EVALUATED',
            'bundle is not a complete diagnostic preparation')
    for key in ('head', 'platform', 'architecture', 'run_id', 'run_attempt', 'trust'):
        require(bundle[key] == current[key], 'bundle differs from authenticated current ' + key)
    require(bundle['sdk_version'] == version and bundle['sdk_sha256'] == sdk_hash, 'bundle SDK differs')
    require(bundle['inputs'] == replay.workspace_hashes(root, False), 'workspace inputs changed')
    require(set(bundle['environment_keys']) <= (replay_ci.ENV_KEYS | set(replay_ci.child_environment({}))),
            'credential-capable preparation environment key')
    require(bundle['inventory'] == inventory(folder), 'missing, extra or changed bundle artifact')
    require(bundle['sources'] == expected_sources(root), 'declared source discovery differs')
    cases = baseline_cases(folder / 'coverage')
    require(cases == bundle['baseline_cases'], 'prepared full baseline differs')
    qualify.runtime_cases(folder)
    original = index_catalogue(folder / 'private-index.json', root, bundle['sources'], cases,
                               bundle['workspace_root'])
    official = index_catalogue(folder / 'official-index.json', root, bundle['sources'], cases,
                               bundle['workspace_root'])
    require(catalogue_masks(original) == catalogue_masks(official), 'official/private index eligibility differs')
    plan = load(folder / 'plan.json')
    binding = {'head': current['head'], 'sdk_version': version, 'sdk_sha256': sdk_hash,
               'tool_sha256': bundle['private_tool_sha256'], 'upstream_commit': replay.COMMIT,
               'platform': current['platform']}
    rebuilt = shards.plan(original, load(root / replay_ci.replay_configuration(current['platform'])),
                          binding, plan['target'], plan['weights'])
    require(plan == rebuilt, 'plan differs from frozen index/configuration')
    packed = load(folder / 'batches.json')
    require(packed == shards.batch_plan(plan, bundle['max_batches']), 'batch packing changed')
    require(bundle['plan_sha256'] == plan['plan_sha256'] and
            bundle['batch_plan_sha256'] == packed['batch_plan_sha256'], 'bundle plan binding differs')
    selected = min(packed['batches'], key=lambda batch: (-batch['weight'], batch['id']))
    require(bundle['selected_batch'] == selected['id'], 'pilot does not select the heaviest batch')
    validate_commands(folder, bundle, root)
    binary = private_bundle(folder, bundle, root, dotnet)
    return bundle, plan, packed, cases, binary


def validate_commands(folder, bundle, root):
    """Reopen raw ledgers/logs; retain producer paths rather than rewriting JSON."""
    prefix = bundle['original_bundle_root'].replace('\\', '/').rstrip('/') + '/'
    def relocated(name):
        normalized = name.replace('\\', '/')
        require(normalized.startswith(prefix), 'command artifact outside prepared bundle')
        return replay_gate.child(folder, normalized[len(prefix):])
    raw = []
    for ledger in (folder / 'commands.jsonl', folder / 'private/commands.jsonl'):
        raw.extend(json.loads(line) for line in ledger.read_text(encoding='utf-8').splitlines())
    require(Counter(shards.canonical(record) for record in raw) ==
            Counter(shards.canonical(record) for record in bundle['commands']), 'raw command ledger differs')
    require(len(raw) == 7, 'missing or extra preparation commands')
    by_label = {}
    for record in raw:
        log = relocated(record['log'])
        label = log.name.rsplit('-', 1)[0]
        require(label not in by_label, 'duplicate preparation phase')
        require(not record['timeout'] and record['exit_code'] == 0 and
                replay.digest(log.read_bytes()) == record['log_sha256'], 'failed command or changed raw log')
        by_label[label] = record
    require(set(by_label) == {'restore-runtime', 'restore-tool', 'restore-mutation-project',
            'runtime-coverage', 'private-build', 'official-index', 'private-index'}, 'preparation phases differ')
    workspace = Path(bundle['workspace_root'])
    dotnet = bundle['sdk_path']
    expected = {
        'restore-runtime': [dotnet, 'restore', 'tests/ChunkMaestro.PersistenceTests/ChunkMaestro.PersistenceTests.csproj', '--locked-mode'],
        'restore-tool': [dotnet, 'tool', 'restore'],
        'restore-mutation-project': [dotnet, 'restore', TEST_PROJECT, '--locked-mode'],
        'runtime-coverage': [dotnet, 'test', 'tests/ChunkMaestro.PersistenceTests/ChunkMaestro.PersistenceTests.csproj', '-c', 'Release', '--no-restore',
            '--collect', 'XPlat Code Coverage', '--settings', 'tooling/quality/storage/coverage.runsettings',
            '--logger', 'trx', '--results-directory', str(Path(bundle['original_bundle_root']) / 'coverage')]}
    for label, args in expected.items():
        require(by_label[label]['args'] == args and by_label[label]['cwd'] == str(workspace),
                'preparation baseline command changed: ' + label)
    provenance = load(folder / 'private/provenance.json')
    require(by_label['private-build'] == provenance['build'], 'private build record differs from provenance')
    source = Path(bundle['original_bundle_root']) / 'private' / ('stryker-net-' + replay.COMMIT)
    require(by_label['private-build']['args'] == [str(arg) for arg in replay.private_build_command(dotnet, source)]
            and by_label['private-build']['cwd'] == str(source), 'private build command differs from pinned policy')
    private_original = str(Path(bundle['original_bundle_root']) / 'private' / bundle['private_binary_relative'])
    require(provenance['binary'] == private_original, 'private entry point differs from original build')
    for label, tool in (('official-index', bundle['official_tool_path']), ('private-index', private_original)):
        args = [dotnet, tool, '--config-file', str(workspace / replay_ci.replay_configuration(bundle['platform'])),
                '--mutate', '**/*.cs{0..1}']
        for pattern in shards.EXCLUSIONS:
            args.extend(['--mutate', pattern])
        args.extend(['--output', str(Path(bundle['original_bundle_root']) / label), '--log-to-file'])
        require(by_label[label]['args'] == args and by_label[label]['cwd'] == str(workspace / 'tooling/quality/storage'),
                'planning index command changed: ' + label)
        reports = list((folder / label).rglob('mutation-report.json'))
        require(len(reports) == 1 and reports[0].read_bytes() == (folder / (label + '.json')).read_bytes(),
                'raw planning report bytes differ')


def binaries(root):
    # Hash the complete relevant Release output trees, including managed/native
    # dependencies, apphosts, runtime configuration and exact file membership.
    # Private-tool artifact copies and unrelated project outputs are not runtime
    # inputs to this test project and must not enter this restoration inventory.
    result = {}
    for project in ('src/ChunkMaestro.Domain', 'src/ChunkMaestro.Application',
                    'src/ChunkMaestro.Persistence', 'tests/ChunkMaestro.PersistenceTests',
                    'tooling/quality/storage'):
        directory = replay_gate.child(root, project + '/bin/Release')
        for path in sorted(directory.rglob('*')):
            name = path.relative_to(root).as_posix()
            replay_gate.child(root, name)
            if path.is_file():
                result[name] = replay.digest(path.read_bytes())
    return result


def verify_probe_report(report, root, plan, batch, cases):
    """Account this batch only; never fabricate absent batch records."""
    value = shards.read_catalogue(report, root)
    original = plan['catalogue']
    require(value['sources'] == original['sources'] and catalogue_masks(value) == catalogue_masks(original),
            'probe source catalogue or original CompileError mask differs')
    tests = bind_tests(report, root, cases)
    owners = set(batch['owners'])
    killed, timeouts = set(), set()
    for row in value['rows']:
        status = row['original']['status']
        if status == 'CompileError':
            continue
        if row['identity'] in owners:
            require(status in ('Killed', 'Survived', 'NoCoverage', 'Timeout'), 'selected owner unresolved')
            if status == 'Killed':
                require(row['original'].get('killedBy') and all(i in tests for i in row['original']['killedBy']),
                        'unattributed probe kill')
                killed.add(row['identity'])
            if status == 'Timeout':
                timeouts.add(row['identity'])
        else:
            require(status == 'Ignored' and row['original'].get('statusReason') == shards.FILTER_REASON,
                    'unexpected out-of-owner execution')
    return {'owner_identities': sorted(owners), 'attributed_killed': sorted(killed),
            'uncredited_mutant_timeouts': sorted(timeouts), 'catalogue_identities': len(value['rows'])}


def prepare(output, max_batches, root=ROOT):
    output = output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    result = {'schema_version': 1, 'scope': SCOPE, 'status': 'UNRESOLVED', 'qualification': 'NOT_EVALUATED'}
    budget = None
    before = None
    try:
        current = context(root)
        require(type(max_batches) is int and 1 <= max_batches <= 20, 'invalid bounded batch count')
        result.update(current, workspace_root=str(root.resolve()), original_bundle_root=str(output))
        env = replay_ci.child_environment(os.environ)
        with replay_ci.isolated_environment(env):
            dotnet, version, sdk_hash = sdk(root, env)
            budget = Budget(PREPARE_SECONDS, env)
            before = replay.workspace_hashes(root, False)
            result.update(inputs=before, sdk_version=version, sdk_sha256=sdk_hash, sdk_path=str(dotnet),
                          sources=expected_sources(root), environment_keys=sorted(env), max_batches=max_batches)
            for args, label in (([dotnet, 'restore', 'tests/ChunkMaestro.PersistenceTests/ChunkMaestro.PersistenceTests.csproj', '--locked-mode'], 'restore-runtime'),
                                ([dotnet, 'tool', 'restore'], 'restore-tool'),
                                ([dotnet, 'restore', TEST_PROJECT, '--locked-mode'], 'restore-mutation-project'),
                                ([dotnet, 'test', 'tests/ChunkMaestro.PersistenceTests/ChunkMaestro.PersistenceTests.csproj', '-c', 'Release', '--no-restore',
                                  '--collect', 'XPlat Code Coverage', '--settings', 'tooling/quality/storage/coverage.runsettings',
                                  '--logger', 'trx', '--results-directory', output / 'coverage'], 'runtime-coverage')):
                budget.execute(args, root, output, label)
            qualify.runtime_cases(output)
            cases = baseline_cases(output / 'coverage')
            result['baseline_cases'] = cases
            with budget.private_builder():
                provenance_path = replay.build_private(output / 'private', dotnet)
            provenance = load(provenance_path)
            require(provenance['source_after_build'].get(GENERATED) == replay_ci.REPORTER_SHA, 'unreviewed private reporter')
            binary = Path(provenance['binary'])
            tool_version = load(root / '.config/dotnet-tools.json')['tools']['dotnet-stryker']['version']
            packages = Path(os.environ.get('USERPROFILE' if os.name == 'nt' else 'HOME', '')) / '.nuget/packages'
            official = packages / 'dotnet-stryker' / tool_version / 'tools/net10.0/any/Stryker.CLI.dll'
            require(official.is_file(), 'restored official tool unavailable')
            result.update(provenance_sha256=replay.digest(provenance_path.read_bytes()),
                          private_binary_relative=binary.relative_to(output / 'private').as_posix(),
                          private_tool_sha256=replay.digest(binary.read_bytes()),
                          official_tool_sha256=replay.digest(official.read_bytes()), official_tool_path=str(official))
            catalogues = {}
            for label, tool in (('official-index', official), ('private-index', binary)):
                args = [dotnet, tool, '--config-file', root / replay_ci.replay_configuration(current['platform']),
                        '--mutate', '**/*.cs{0..1}']
                for pattern in shards.EXCLUSIONS:
                    args.extend(['--mutate', pattern])
                args.extend(['--output', output / label, '--log-to-file'])
                record = budget.command(args, root / 'tooling/quality/storage', output, label, timeout=INDEX_SECONDS)
                replay.require(record)
                reports = list((output / label).rglob('mutation-report.json'))
                require(len(reports) == 1, label + ': missing or ambiguous raw index')
                (output / (label + '.json')).write_bytes(reports[0].read_bytes())
                catalogues[label] = index_catalogue(reports[0], root, result['sources'], cases)
            require(catalogue_masks(catalogues['official-index']) == catalogue_masks(catalogues['private-index']),
                    'official/private index eligibility differs')
            binding = {'head': current['head'], 'sdk_version': version, 'sdk_sha256': sdk_hash,
                       'tool_sha256': result['private_tool_sha256'], 'upstream_commit': replay.COMMIT,
                       'platform': current['platform']}
            config = load(root / replay_ci.replay_configuration(current['platform']))
            plan = shards.plan(catalogues['private-index'], config, binding,
                               target=50 if current['platform'] == 'Windows' else 200)
            packed = shards.batch_plan(plan, max_batches)
            replay.write(output / 'plan.json', plan)
            replay.write(output / 'batches.json', packed)
            result.update(plan_sha256=plan['plan_sha256'], batch_plan_sha256=packed['batch_plan_sha256'],
                          selected_batch=min(packed['batches'], key=lambda batch: (-batch['weight'], batch['id']))['id'])
            require(before == replay.workspace_hashes(root, False), 'preparation changed frozen workspace inputs')
            result['status'] = 'PREPARED_DIAGNOSTIC'
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
        result['error'] = str(error)
        print('[storage] PREPARE UNRESOLVED: ' + str(error), flush=True)
    finally:
        result['commands'] = budget.records if budget else []
        result['inputs_preserved'] = before is not None and before == replay.workspace_hashes(root, False)
        if not result['inputs_preserved']:
            result['status'] = 'UNRESOLVED'
        result['elapsed_seconds'] = time.monotonic() - budget.started if budget else 0
        try:
            result['inventory'] = inventory(output)
        except (OSError, ValueError) as error:
            result.update(status='UNRESOLVED', inventory_error=str(error), inventory={})
        replay.write(output / 'bundle.json', result)
        print('[storage] PREPARE ' + result['status'] + '; elapsed=' + str(round(result['elapsed_seconds'], 1)) +
              's; selected_batch=' + str(result.get('selected_batch', 'none')) +
              '; qualification=NOT_EVALUATED; artifact=' + str(output / 'bundle.json'), flush=True)
    return 0 if result['status'] == 'PREPARED_DIAGNOSTIC' else 1


def probe(bundle_folder, output, root=ROOT):
    output = output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    result = {'schema_version': 1, 'scope': SCOPE, 'status': 'UNRESOLVED', 'qualification': 'NOT_EVALUATED'}
    before = None
    initial_cases = None
    initial_binaries = None
    budget = None
    try:
        current = context(root)
        result.update(current)
        env = replay_ci.child_environment(os.environ)
        with replay_ci.isolated_environment(env):
            dotnet, version, sdk_hash = sdk(root, env)
            budget = Budget(PROBE_SECONDS, env)
            bundle, plan, packed, prepared_cases, binary = validate_bundle(bundle_folder, root, current, dotnet, version, sdk_hash)
            before = replay.workspace_hashes(root, False)
            result.update(plan_sha256=plan['plan_sha256'], batch_plan_sha256=packed['batch_plan_sha256'],
                          prepared_bundle_sha256=replay.digest((bundle_folder / 'bundle.json').read_bytes()),
                          selected_batch=bundle['selected_batch'], environment_keys=sorted(env))
            project = root / TEST_PROJECT
            def baseline(label, reserve):
                for verb, args in (('clean', []), ('build', ['--no-restore', '--no-incremental']),
                                   ('test', ['--no-restore', '--no-build', '--logger', 'trx', '--results-directory', output / label])):
                    budget.execute([dotnet, verb, project, '-c', 'Release', *args], root, output,
                                   label + '-' + verb, reserve=reserve)
                replay.write(output / (label + '-binaries.json'), binaries(root))
                return baseline_cases(output / label)
            budget.execute([dotnet, 'restore', project, '--locked-mode'], root, output, 'restore', reserve=RESTORE_RESERVE)
            initial_cases = baseline('baseline', RESTORE_RESERVE)
            initial_binaries = binaries(root)
            require(initial_cases == prepared_cases and initial_binaries, 'consumer baseline differs from prepared full cases')
            batch = packed['batches'][bundle['selected_batch']]
            config = shards.batch_configuration(plan, packed, batch['id'])
            config_path = output / 'batch-config.json'
            replay.write(config_path, config)
            record = budget.command([dotnet, binary, '--config-file', config_path, '--output', output / 'mutation', '--log-to-file'],
                                    root / 'tooling/quality/storage', output, 'private-patched',
                                    timeout=MUTATION_SECONDS, reserve=RESTORE_RESERVE)
            require(not record['timeout'] and record['exit_code'] in (0, 2),
                    'selected batch did not complete: reason=' + str(record.get('timeout_reason') or record['exit_code']) +
                    '; elapsed=' + str(record.get('seconds')) + '; log=' + str(record.get('log')))
            reports = list((output / 'mutation').rglob('mutation-report.json'))
            require(len(reports) == 1, 'missing or ambiguous batch report')
            report = output / 'mutation-report.json'
            report.write_bytes(reports[0].read_bytes())
            accounting = verify_probe_report(report, root, plan, batch, initial_cases)
            result.update(accounting, raw_report_sha256=replay.digest(report.read_bytes()),
                          config_sha256=replay.digest(config_path.read_bytes()), completed_batch=True)
            # The missing batches are deliberate: never call full validate_batches
            # or publish qualification from one representative timing diagnostic.
            result['status'] = 'PARTIAL_DIAGNOSTIC'
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
        result['error'] = str(error)
        print('[storage] PROBE UNRESOLVED: ' + str(error), flush=True)
    finally:
        if initial_cases is not None:
            try:
                restored = baseline('restored', 0)
                require(restored == initial_cases, 'restored full test-case identities differ')
                require(binaries(root) == initial_binaries, 'restored product/test binaries differ')
                result['baseline_restored'] = True
            except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
                result.update(status='UNRESOLVED', restoration_error=str(error))
        result['inputs_preserved'] = before is not None and before == replay.workspace_hashes(root, False)
        if not result['inputs_preserved']:
            result['status'] = 'UNRESOLVED'
        result['commands'] = budget.records if budget else []
        result['elapsed_seconds'] = time.monotonic() - budget.started if budget else 0
        replay.write(output / 'probe.json', result)
        print('[storage] PROBE ' + result['status'] + '; elapsed=' + str(round(result['elapsed_seconds'], 1)) +
              's; attributed_kills=' + str(len(result.get('attributed_killed', []))) +
              '; baseline_restored=' + str(result.get('baseline_restored', False)) +
              '; qualification=NOT_EVALUATED; artifact=' + str(output / 'probe.json'), flush=True)
    return 0 if result['status'] == 'PARTIAL_DIAGNOSTIC' and result.get('baseline_restored') else 1


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_subparsers(dest='mode', required=True)
    create = modes.add_parser('prepare')
    create.add_argument('output', type=Path)
    create.add_argument('--batches', type=int, required=True)
    run = modes.add_parser('probe')
    run.add_argument('bundle', type=Path)
    run.add_argument('output', type=Path)
    args = parser.parse_args()
    sys.exit(prepare(args.output, args.batches) if args.mode == 'prepare' else probe(args.bundle, args.output))
