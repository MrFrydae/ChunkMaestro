"""Version 1, two Windows cells; diagnostic accounting only, never qualification."""
import argparse
from collections import Counter
from copy import deepcopy
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time

import replay
import replay_ci
import replay_gate
import shard_ci as source
import shards
import test_groups as groups

ROOT = source.ROOT
SCOPE = 'storage-test-cell-pilot'
RESTORE_RESERVE = 120
PRIVATE_SECONDS = 180
require = shards.require
load = source.load


def windows_context(root):
    current = source.context(root)
    require(current['platform'] == 'Windows', 'two-cell pilot is Windows only')
    event = load(Path(os.environ['GITHUB_EVENT_PATH']))
    require(current['trust']['event'] == 'workflow_dispatch' or
            event.get('action') == 'labeled' and event.get('label', {}).get('name') == SCOPE,
            'explicit two-cell request required')
    return current


def trx(directory):
    paths = list(directory.rglob('*.trx'))
    require(len(paths) == 1, 'missing/ambiguous full TRX')
    return groups.read_trx(paths[0])


def make_plan(bundle, original, config, baseline, root):
    binding = {k: bundle[k] for k in ('head', 'sdk_version', 'sdk_sha256', 'platform')}
    binding.update(tool_sha256=bundle['private_tool_sha256'], upstream_commit=replay.COMMIT)
    tests = groups.plan(original, config, binding, baseline, bundle['inputs'], 2, True)
    sources = shards.plan(original, config, binding, target=10)
    name = 'src/ChunkMaestro.Persistence/LocalFleetStore.cs'
    text = (root / name).read_bytes().decode('utf-8')
    # Explicit source anchors, no heuristic method parser or fallback to another file.
    start = text.index('private static async Task<LocalFleetStore> OpenWorkerAsync(')
    end = text.index('internal static bool IsControllerContention(', start)
    interval = [len(text[:start].encode('utf-16-le')) // 2, len(text[:end].encode('utf-16-le')) // 2]
    executable = {r['identity'] for r in original['rows'] if r['original']['status'] != 'CompileError'}
    choices = [r for r in sources['regions'] if r['root']['file'] == name and
               shards.contains(interval, r['root']['span']) and 0 < len(r['owners']) <= 10 and
               executable.intersection(r['owners'])]
    require(choices, 'unsupported: no <=10-owner executable startup/IO region; no fallback')
    region = min(choices, key=lambda r: (len(r['owners']), r['root']['span'], r['id']))
    result = {'schema_version': 1, 'scope': SCOPE, 'qualification': 'NOT_EVALUATED',
              'tests': tests, 'sources': sources, 'region': region,
              'rationale': 'Smallest executable canonical region wholly inside OpenWorkerAsync startup/IO; lexical span tie break.',
              'source_bundle_sha256': bundle['bundle_sha256']}
    result['plan_sha256'] = shards.sha(result)
    return result


def configuration(plan, group):
    require(type(group) is int and group in (0, 1), 'unknown cell')
    config = groups.configuration(plan['tests'], group)
    region = plan['region']
    config['stryker-config']['mutate'] = [shards.CATALOGUE_KEEPALIVE, *shards.EXCLUSIONS,
        shards.pattern(region['root']), *(shards.pattern(n, True) for n in region['exclusions'])]
    require(config['stryker-config'].get('coverage-analysis', 'perTest') == 'perTest', 'execution coverage policy differs')
    return config


def plan_inventory(folder):
    names = {'cell-plan.json', 'cell-0-config.json', 'cell-1-config.json'}
    require({p.name for p in folder.iterdir()} == names | {'source', 'cell-bundle.json'},
            'extra/missing cell preparation artifact')
    return groups.inventory(folder, sorted(names))


def preflight(folder, root, current, dotnet, version, sdk_hash):
    sealed = folder / 'source'
    bundle, _, _, _, binary = source.validate_bundle(sealed, root, current, dotnet, version, sdk_hash)
    bundle = dict(bundle, bundle_sha256=replay.digest((sealed / 'bundle.json').read_bytes()))
    plan = load(folder / 'cell-plan.json')
    original = shards.read_catalogue(sealed / 'official-index.json', root, bundle['workspace_root'])
    rebuilt = make_plan(bundle, original, load(root / replay_ci.replay_configuration('Windows')),
                        trx(sealed / 'coverage'), root)
    require(plan == rebuilt, 'cell plan differs from authenticated raw full inputs')
    manifest = load(folder / 'cell-bundle.json')
    require(manifest == {'schema_version': 1, 'scope': SCOPE, 'status': 'PREPARED_DIAGNOSTIC',
            'qualification': 'NOT_EVALUATED', 'plan_sha256': plan['plan_sha256'],
            'source_bundle_sha256': bundle['bundle_sha256'], 'inventory': plan_inventory(folder)},
            'cell preparation manifest differs')
    for group in (0, 1):
        require(load(folder / f'cell-{group}-config.json') == configuration(plan, group), 'changed prepared cell config')
    return bundle, plan, binary


def prepare(output, root=ROOT):
    output.mkdir(parents=True, exist_ok=False)
    windows_context(root)
    # The source collector owns its unchanged 420s budget and 180s index bound.
    require(source.prepare(output / 'source', 16, root) == 0, 'source preparation unresolved')
    current = windows_context(root)
    env = replay_ci.child_environment(os.environ)
    with replay_ci.isolated_environment(env):
        dotnet, version, sdk_hash = source.sdk(root, env)
        bundle, _, _, _, _ = source.validate_bundle(output / 'source', root, current, dotnet, version, sdk_hash)
    bundle = dict(bundle, bundle_sha256=replay.digest((output / 'source/bundle.json').read_bytes()))
    original = shards.read_catalogue(output / 'source/official-index.json', root, bundle['workspace_root'])
    plan = make_plan(bundle, original, load(root / replay_ci.replay_configuration('Windows')),
                     trx(output / 'source/coverage'), root)
    replay.write(output / 'cell-plan.json', plan)
    for group in (0, 1):
        replay.write(output / f'cell-{group}-config.json', configuration(plan, group))
    replay.write(output / 'cell-bundle.json', {})
    replay.write(output / 'cell-bundle.json', {'schema_version': 1, 'scope': SCOPE,
        'status': 'PREPARED_DIAGNOSTIC', 'qualification': 'NOT_EVALUATED',
        'plan_sha256': plan['plan_sha256'], 'source_bundle_sha256': bundle['bundle_sha256'],
        'inventory': plan_inventory(output)})
    return 0


def verify_report(report, plan, group, root, host_root):
    current = shards.read_catalogue(report, root, host_root)
    original = plan['sources']['catalogue']
    require(current['sources'] == original['sources'] and
            source.catalogue_masks(current) == source.catalogue_masks(original), 'partial catalogue/source bytes/CompileError mask')
    selected = plan['tests']['groups'][group]
    raw = load(report)
    tests = shards.bound_tests(raw, root, selected['methods'], host_root)
    require(Counter(tests.values()) == Counter(c['method'] for c in selected['cases']),
            'unsupported selected report definition multiplicities')
    owners = set(plan['region']['owners'])
    killed, timeouts, scored = set(), set(), []
    for row in current['rows']:
        mutant = row['original']
        status = mutant['status']
        if status == 'CompileError':
            continue
        if row['identity'] in owners:
            require(status in ('Killed', 'Survived', 'NoCoverage', 'Timeout'), 'selected owner unresolved')
            scored.append(status)
            if status == 'Killed':
                require(mutant.get('killedBy') and all(i in tests for i in mutant['killedBy']), 'unbound/out-of-group kill')
                killed.add(row['identity'])
            if status == 'Timeout':
                timeouts.add(row['identity'])
        else:
            require(status == 'Ignored' and mutant.get('statusReason') == shards.FILTER_REASON,
                    'out-of-source owner differs from exact mutate-filter Ignored')
    require(scored, 'no executable selected owners')
    return {'attributed_killed': sorted(killed), 'uncredited_mutant_timeouts': sorted(timeouts),
            'raw_threshold_failed': 100 * sum(s in ('Killed', 'Timeout') for s in scored) / len(scored) <
                configuration(plan, group)['stryker-config']['thresholds']['break']}


def selection_log(text, count):
    # Pinned VsTest flow: initial starts with exactly selected definitions;
    # subsequent coverage + completed final score cannot follow failed initial tests.
    counts = re.findall(r'Number of tests found: (\d+) for project .+\. Initial test run started\.', text)
    coverage = text.find("Capture mutant coverage using 'CoverageBasedTest' mode.")
    initial = text.find('Initial test run started.')
    final = text.find('The final mutation score is ')
    require(counts == [str(count)] and 0 <= initial < coverage < final, 'UNRESOLVED: unsupported/missing successful initial-run representation')


def probe(folder, output, group, root=ROOT):
    output.mkdir(parents=True, exist_ok=False)
    result = {'schema_version': 1, 'scope': SCOPE, 'status': 'UNRESOLVED',
              'qualification': 'NOT_EVALUATED', 'group': group, 'output_root': str(output.resolve()),
              'workspace_root': str(root.resolve()), 'failure_phase': 'preflight'}
    budget, before, initial, runtime = None, None, None, None
    try:
        current = windows_context(root)
        result.update(current)
        env = replay_ci.child_environment(os.environ)
        with replay_ci.isolated_environment(env):
            dotnet, version, sdk_hash = source.sdk(root, env)
            budget = source.Budget(480, env)
            bundle, plan, binary = preflight(folder, root, current, dotnet, version, sdk_hash)
            config = configuration(plan, group)
            result.update(plan_sha256=plan['plan_sha256'], sdk_path=str(dotnet), binary_path=str(binary),
                          inputs=bundle['inputs'], source_bundle_sha256=bundle['bundle_sha256'])
            before = replay.workspace_hashes(root, False)
            def control(label, reserve):
                for verb, args in (('clean', []), ('build', ['--no-restore', '--no-incremental']),
                    ('test', ['--no-restore', '--no-build', '--logger', 'trx', '--results-directory', output / label])):
                    budget.execute([dotnet, verb, root / source.TEST_PROJECT, '-c', 'Release', *args],
                                   root, output, label + '-' + verb, reserve=reserve)
                return trx(output / label)
            result['failure_phase'] = 'baseline'
            initial = plan['tests']['baseline']['cases']
            budget.execute([dotnet, 'restore', root / source.TEST_PROJECT, '--locked-mode'], root, output,
                           'restore', reserve=RESTORE_RESERVE)
            # Even a failed baseline must attempt full restoration after touching runtime.
            initial = plan['tests']['baseline']['cases']
            baseline = control('baseline', RESTORE_RESERVE)
            require(baseline['cases'] == initial, 'full baseline case partition differs')
            runtime = source.binaries(root)
            require(runtime, 'empty runtime inventory')
            replay.write(output / 'baseline-binaries.json', runtime)
            replay.write(output / 'cell-config.json', config)
            require(before == replay.workspace_hashes(root, False), 'baseline changed frozen workspace inputs')
            result['failure_phase'] = 'private'
            record = budget.command([dotnet, binary, '--config-file', output / 'cell-config.json',
                '--output', output / 'mutation', '--log-to-file'], root / 'tooling/quality/storage', output,
                'private-cell', timeout=PRIVATE_SECONDS, reserve=RESTORE_RESERVE)
            require(type(record['exit_code']) is int and record['exit_code'] in (0, 2) and not record['timeout'],
                    'private cell incomplete; raw log=' + record['log'])
            reports = list((output / 'mutation').rglob('mutation-report.json'))
            require(len(reports) == 1, 'missing/ambiguous raw report')
            details = verify_report(reports[0], plan, group, root, str(root))
            require((record['exit_code'] == 2) == details['raw_threshold_failed'], 'raw score/exit mismatch')
            selection_log(Path(record['log']).read_text(encoding='utf-8', errors='replace'),
                          len(plan['tests']['groups'][group]['cases']))
            result.update(details, status='CELL_COMPLETE', failure_phase=None)
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
        result['error'] = str(error)
    finally:
        if initial is not None:
            try:
                require(control('restored', 0)['cases'] == initial, 'full restoration cases differ')
                require(runtime is not None, 'baseline runtime inventory unavailable')
                result['retained_tool_backups'] = source.retain_tool_backups(root, output, runtime)
                restored = source.binaries(root)
                replay.write(output / 'restored-final-binaries.json', restored)
                require(restored == runtime, 'restoration runtime bytes/membership differ')
                result['baseline_restored'] = True
                result['derived_group_view'] = {
                    'status': 'DERIVED', 'cases': plan['tests']['groups'][group]['cases'],
                    'baseline_trx_sha256': trx(output / 'baseline')['trx_sha256'],
                    'restored_trx_sha256': trx(output / 'restored')['trx_sha256']}
            except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
                result.update(status='UNRESOLVED', restoration_error=str(error))
        result['inputs_preserved'] = before is not None and before == replay.workspace_hashes(root, False)
        if not result['inputs_preserved'] or not result.get('baseline_restored'):
            result['status'] = 'UNRESOLVED'
        result['commands'] = budget.records if budget else []
        result['elapsed_seconds'] = time.monotonic() - budget.started if budget else 0
        result['inventory'] = source.inventory(output)
        replay.write(output / 'cell.json', result)
    return 0 if result['status'] == 'CELL_COMPLETE' else 1


def relocated(folder, name, prefix):
    name, prefix = name.replace('\\', '/'), prefix.replace('\\', '/').rstrip('/') + '/'
    require(name.startswith(prefix), 'foreign output root')
    return replay_gate.child(folder, name[len(prefix):])


def validate_cell(folder, plan, bundle, current, root):
    value = load(folder / 'cell.json')
    require(value['schema_version'] == 1 and value['scope'] == SCOPE and value['status'] == 'CELL_COMPLETE' and
            value['qualification'] == 'NOT_EVALUATED' and value['inputs_preserved'] is True and
            value['baseline_restored'] is True and 0 < value['elapsed_seconds'] <= 480, 'incomplete cell/deadline/restoration')
    for key in ('head', 'platform', 'architecture', 'run_id', 'run_attempt', 'trust'):
        require(value[key] == current[key], 'foreign cell ' + key)
    require(value['inputs'] == bundle['inputs'] and value['source_bundle_sha256'] == bundle['bundle_sha256'] and
            value['plan_sha256'] == plan['plan_sha256'], 'foreign plan/input binding')
    require(value['sdk_path'] == bundle['sdk_path'] and
            value['binary_path'].replace('\\', '/').endswith('/source/private/' + bundle['private_binary_relative']),
            'SDK/private execution entry point differs')
    group = value['group']
    require(type(group) is int and group in (0, 1), 'unknown cell')
    # The manifest is excluded from its own inventory; source.inventory excludes bundle.json only.
    actual = source.inventory(folder)
    actual.pop('cell.json', None)
    require(actual == value['inventory'], 'changed/missing/extra cell evidence')
    records = [json.loads(line) for line in (folder / 'commands.jsonl').read_text().splitlines()]
    require(records == value['commands'] and len(records) == 8, 'raw cell ledger differs')
    output, workspace, dotnet = Path(value['output_root']), Path(value['workspace_root']), value['sdk_path']
    expected = [[dotnet, 'restore', str(workspace / source.TEST_PROJECT), '--locked-mode']]
    for label in ('baseline', 'restored'):
        for verb, args in (('clean', []), ('build', ['--no-restore', '--no-incremental']),
            ('test', ['--no-restore', '--no-build', '--logger', 'trx', '--results-directory', str(output / label)])):
            expected.append([dotnet, verb, str(workspace / source.TEST_PROJECT), '-c', 'Release', *args])
    expected.insert(4, [dotnet, value['binary_path'], '--config-file', str(output / 'cell-config.json'),
                        '--output', str(output / 'mutation'), '--log-to-file'])
    for index, (record, args) in enumerate(zip(records, expected)):
        require(record['args'] == args and record['cwd'] == str(workspace / 'tooling/quality/storage' if index == 4 else workspace),
                'changed cell command/options/cwd')
        require(not record['timeout'] and not record.get('termination_error') and type(record['exit_code']) is int and
                record['exit_code'] in ((0, 2) if index == 4 else (0,)) and
                0 <= record['seconds'] <= (PRIVATE_SECONDS if index == 4 else 180), 'failed/timedout cell command')
        log = relocated(folder, record['log'], value['output_root'])
        require(replay.digest(log.read_bytes()) == record['log_sha256'], 'changed raw command log')
    require(load(folder / 'cell-config.json') == configuration(plan, group), 'changed config/extra options')
    for label in ('baseline', 'restored'):
        require(trx(folder / label)['cases'] == plan['tests']['baseline']['cases'], 'omitted/changed full cases')
    require(value['derived_group_view'] == {
        'status': 'DERIVED', 'cases': plan['tests']['groups'][group]['cases'],
        'baseline_trx_sha256': trx(folder / 'baseline')['trx_sha256'],
        'restored_trx_sha256': trx(folder / 'restored')['trx_sha256']}, 'derived group view differs')
    runtime = load(folder / 'baseline-binaries.json')
    require(runtime and load(folder / 'restored-final-binaries.json') == runtime, 'changed runtime membership/bytes')
    for backup in value['retained_tool_backups']:
        require(runtime[backup['original_runtime_file']] == backup['sha256'] ==
            replay.digest(replay_gate.child(folder, backup['evidence_file']).read_bytes()), 'native/runtime backup differs')
    reports = list((folder / 'mutation').rglob('mutation-report.json'))
    require(len(reports) == 1, 'missing/ambiguous raw cell report')
    details = verify_report(reports[0], plan, group, root, value['workspace_root'])
    require((records[4]['exit_code'] == 2) == details['raw_threshold_failed'], 'raw score/exit mismatch')
    selection_log(relocated(folder, records[4]['log'], value['output_root']).read_text(encoding='utf-8', errors='replace'),
                  len(plan['tests']['groups'][group]['cases']))
    return group, details


def account(folder, cells, root=ROOT):
    current = windows_context(root)
    env = replay_ci.child_environment(os.environ)
    with replay_ci.isolated_environment(env):
        dotnet, version, sdk_hash = source.sdk(root, env)
        bundle, plan, _ = preflight(folder, root, current, dotnet, version, sdk_hash)
    require(len(cells) == 2, 'both expected cells mandatory')
    results = [validate_cell(cell, plan, bundle, current, root) for cell in cells]
    require(sorted(g for g, _ in results) == [0, 1], 'missing/overlapping cells')
    return {'schema_version': 1, 'scope': SCOPE, 'status': 'PARTIAL_DIAGNOSTIC', 'qualification': 'NOT_EVALUATED',
        'plan_sha256': plan['plan_sha256'], 'selected_owner_union': plan['region']['owners'],
        'global_fixed_denominator': plan['sources']['original_denominator'],
        'attributed_killed': sorted(set().union(*(set(d['attributed_killed']) for _, d in results))),
        'uncredited_mutant_timeouts': sorted(set().union(*(set(d['uncredited_mutant_timeouts']) for _, d in results))),
        'group_views': 'DERIVED from full unfiltered baseline/restoration TRX; no separate selected execution'}


def summary(folder):
    lines = []
    for name in ('outcome.json', 'cell.json', 'cell-bundle.json', 'source/bundle.json'):
        path = folder / name
        if path.is_file():
            value = load(path)
            lines.append(f"{name}: {value.get('status', 'UNRESOLVED')}; qualification=NOT_EVALUATED; "
                         f"phase={value.get('failure_phase')}; error={value.get('error')}; "
                         f"restoration_error={value.get('restoration_error')}; raw logs={folder}")
    text = '\n\n'.join(lines) or f'UNRESOLVED: missing diagnostic manifest; raw logs={folder}'
    print(text, flush=True)
    if os.environ.get('GITHUB_STEP_SUMMARY'):
        with open(os.environ['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as stream:
            stream.write(text + '\n')
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_subparsers(dest='mode', required=True)
    for mode in ('prepare', 'summary'):
        modes.add_parser(mode).add_argument('output', type=Path)
    probe_args = modes.add_parser('probe')
    probe_args.add_argument('bundle', type=Path)
    probe_args.add_argument('output', type=Path)
    probe_args.add_argument('--group', type=int, choices=(0, 1), required=True)
    accounting = modes.add_parser('account')
    accounting.add_argument('bundle', type=Path)
    accounting.add_argument('output', type=Path)
    accounting.add_argument('cells', type=Path, nargs='*')
    args = parser.parse_args()
    try:
        if args.mode == 'summary':
            return summary(args.output)
        if args.mode == 'prepare':
            return prepare(args.output.resolve())
        if args.mode == 'probe':
            return probe(args.bundle.resolve(), args.output.resolve(), args.group)
        args.output.mkdir(parents=True, exist_ok=True)
        replay.write(args.output / 'outcome.json', account(args.bundle.resolve(), args.cells))
        return 0
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
        args.output.mkdir(parents=True, exist_ok=True)
        replay.write(args.output / 'outcome.json', {'schema_version': 1, 'scope': SCOPE, 'status': 'UNRESOLVED',
            'qualification': 'NOT_EVALUATED', 'failure_phase': args.mode, 'error': str(error)})
        return 1


if __name__ == '__main__':
    sys.exit(main())
