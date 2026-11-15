"""Exact-source, cross-platform storage qualification. Never synthesizes runtime evidence."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[3]
MODULES = ('Domain', 'Application', 'Persistence')
EXCLUDES = ('*.Designer.cs', '*ModelSnapshot.cs')


def source_hashes():
    return {p.relative_to(ROOT).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
            for m in MODULES for p in sorted((ROOT / 'src' / ('ChunkMaestro.' + m)).rglob('*.cs'))
            if not {'obj', 'bin'}.intersection(p.parts)} | {
                'src/ChunkMaestro.Persistence/Native/storage.c': hashlib.sha256((ROOT / 'src/ChunkMaestro.Persistence/Native/storage.c').read_bytes()).hexdigest()}


def run(output):
    # Collection is separate from lead-owned aggregate qualification.
    import time
    import replay
    from replay_ci import child_environment, complete_report, isolated_environment

    output = output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    dotnet = os.environ.get('CHUNKMAESTRO_DOTNET', 'dotnet')
    env = child_environment(os.environ)
    if Path(dotnet).is_absolute():
        env['PATH'] = str(Path(dotnet).parent) + os.pathsep + env.get('PATH', '')
        env['DOTNET_ROOT'] = str(Path(dotnet).parent)
    records = []

    def command(args, cwd=ROOT, timeout=600):
        with isolated_environment(env):
            record = replay.command(args, cwd, output, 'command-%02d' % len(records), env, timeout)
        records.append(record)
        return record

    manifest = {'schema_version': 1, 'head': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
                'platform': platform.system(), 'sources': source_hashes(), 'commands': records,
                'status': 'UNRESOLVED', 'runtime_tests_passed': False,
                'clean': not subprocess.check_output(['git', 'status', '--porcelain', '--untracked-files=normal'], cwd=ROOT, text=True).strip()}
    started = time.monotonic()
    try:
        if platform.system() not in ('Windows', 'Darwin'):
            raise RuntimeError('Linux is an unsupported-controller contract, not storage safety evidence')
        for args in ([dotnet, 'restore', 'tests/ChunkMaestro.PersistenceTests/ChunkMaestro.PersistenceTests.csproj', '--locked-mode'],
                     [dotnet, 'tool', 'restore'],
                     [dotnet, 'restore', 'tooling/quality/storage/MutationTests.csproj', '--locked-mode'],
                     [dotnet, 'test', 'tests/ChunkMaestro.PersistenceTests/ChunkMaestro.PersistenceTests.csproj', '-c', 'Release', '--no-restore',
                      '--collect', 'XPlat Code Coverage', '--settings', 'tooling/quality/storage/coverage.runsettings',
                      '--logger', 'trx', '--results-directory', str(output / 'coverage')]):
            replay.require(command(args))
        runtime_cases(output)
        manifest['runtime_tests_passed'] = True
        for module in MODULES:
            config = {'stryker-config': {'project': 'ChunkMaestro.' + module + '.csproj', 'configuration': 'Release',
                      'test-projects': ['MutationTests.csproj'],
                      'reporters': ['json', 'html'], 'thresholds': {'high': 90, 'low': 80, 'break': 80},
                      'mutate': ['**/*.cs', '!**/Migrations/*.Designer.cs', '!**/Migrations/*ModelSnapshot.cs'],
                      'concurrency': 2}}
            config_path = output / (module + '.json')
            config_path.write_text(json.dumps(config, indent=2), encoding='utf-8')
            directory = output / ('mutation-' + module)
            record = command([dotnet, 'stryker', '--config-file', str(config_path), '--output', str(directory)],
                             ROOT / 'tooling/quality/storage', timeout=2400)
            record['collection'] = complete_report(directory, record, module=module)
        manifest['status'] = 'COLLECTED'
    except (RuntimeError, ValueError, KeyError, OSError, ET.ParseError) as error:
        manifest['error'] = str(error)
    finally:
        manifest['seconds'] = time.monotonic() - started
        (output / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    return 0 if manifest['status'] == 'COLLECTED' else 1


def canonical(path):
    path = path.replace('\\', '/')
    marker = 'src/ChunkMaestro.'
    if marker in path:
        return path[path.index(marker):]
    raise ValueError('Unexpected source path: ' + path)


def mutation_key(file, mutant):
    return json.dumps([canonical(file), mutant['location'], mutant['mutatorName'],
                       mutant.get('replacement')], sort_keys=True)


def runtime_cases(folder):
    reports = list((folder / 'coverage').glob('*.trx'))
    if len(reports) != 1:
        raise ValueError('Expected exactly one runtime TRX')
    root = ET.parse(reports[0])
    counters = root.find('.//{*}Counters')
    results = root.findall('.//{*}UnitTestResult')
    if (counters is None or not results or counters.get('total') != counters.get('passed')
            or int(counters.get('total', '0')) != len(results)
            or any(r.get('outcome') != 'Passed' for r in results)):
        raise ValueError('Runtime tests missing, failing or skipped')
    names = {r.get('testName', '') for r in results}
    required = (
        'BackupPublicationTests.KillAfterProductionStepLeavesOnlyUnpublishedCandidateAndRecoverableOriginal',
        'BackupPublicationTests.PublishedBackupRestoresCommittedWalThroughPublicStore',
        'PopulatedUpgradeTests.PopulatedInitialUpgradePreservesAllMetadataAndCompatibleBackup',
        'PopulatedUpgradeTests.KillAfterNewMigrationDropsPopulatedEndpointsRollsBackAndRecoversSeparately')
    if any(not any(name.endswith(case) for name in names) for case in required):
        raise ValueError('Required populated upgrade/recovery or production backup interruption case missing')


def native_evidence(folder, manifest):
    native = json.loads((folder / 'native.json').read_text(encoding='utf-8'))
    source = ROOT / 'src/ChunkMaestro.Persistence/Native/storage.c'
    runner = ROOT / 'tooling/quality/storage/native.py'
    resources = runner.with_suffix('')
    hashes = {p.name: hashlib.sha256(p.read_bytes()).hexdigest()
              for p in sorted(resources.glob('*')) if p.is_file()}
    if (native['schema_version'] != 2 or native['status'] != 'PASS' or not native['clean']
            or native['head'] != manifest['head'] or native['platform'] != 'Darwin'
            or native['source_sha256'] != manifest['sources']['src/ChunkMaestro.Persistence/Native/storage.c']
            or native['runner_sha256'] != hashlib.sha256(runner.read_bytes()).hexdigest()
            or native['resources'] != hashes):
        raise ValueError('Native C evidence failed, dirty or mismatched')
    if native['coverage_category'] != 'real-filesystem-only':
        raise ValueError('Injected coverage cannot substitute for real filesystem coverage')
    totals = json.loads((folder / 'coverage.json').read_text(encoding='utf-8'))['data'][0]['totals']
    if native['metrics'] != totals:
        raise ValueError('Native metrics differ from LLVM report')
    for label, threshold in (('lines', 90), ('branches', 85)):
        metric = totals[label]
        if metric['count'] <= 0 or not 0 <= metric['covered'] <= metric['count'] or 100 * metric['covered'] / metric['count'] < threshold:
            raise ValueError('Native real filesystem ' + label + ' below threshold')
    if native['runtime']['failed'] != 0 or native['runtime']['passed'] <= 0:
        raise ValueError('Native runtime cases missing or failing')
    if not native['preservation']['source_unchanged'] or not native['preservation']['dylib_unchanged']:
        raise ValueError('Native qualification changed source or production dylib')
    catalogue = json.loads((resources / 'mutations.json').read_text(encoding='utf-8'))
    mutants = native['mutation']['mutants']
    if len(mutants) != len(catalogue) or {m['id'] for m in mutants} != {m[0] for m in catalogue}:
        raise ValueError('Native mutation catalogue incomplete')
    original = source.read_text(encoding='utf-8')
    for mutant in mutants:
        _, before, after = next(entry for entry in catalogue if entry[0] == mutant['id'])
        expected = hashlib.sha256(original.replace(before, after).encode('utf-8')).hexdigest()
        path = (folder / mutant['source']).resolve()
        if not path.is_relative_to(folder.resolve()) or mutant['source_sha256'] != expected or hashlib.sha256(path.read_bytes()).hexdigest() != expected:
            raise ValueError('Native compiled mutation source mismatch')
        if mutant['status'] not in ('Killed', 'Survived', 'Uncovered', 'CompileError'):
            raise ValueError('Native mutation timeout or execution error')
        if mutant['status'] == 'Killed':
            cases = json.loads((folder / mutant['test_log']).read_text(encoding='utf-8'))['cases']
            if not any(not case['passed'] for case in cases):
                raise ValueError('Native mutation kill lacks failed assertion')
    if not mutants or 100 * sum(m['status'] == 'Killed' for m in mutants) / len(mutants) < 80:
        raise ValueError('Native mutation below 80%')


def coverage(folder):
    lines, branches = {}, {}
    reports = list(folder.rglob('coverage.opencover.xml'))
    if not reports or len({hashlib.sha256(p.read_bytes()).hexdigest() for p in reports}) != 1:
        raise ValueError('Expected one unambiguous OpenCover report (identical TRX attachment copies allowed)')
    for module in ET.parse(reports[0]).findall('.//Module'):
        files = {f.attrib['uid']: canonical(f.attrib['fullPath']) for f in module.findall('./Files/File')}
        for method in module.findall('.//Method'):
            name = method.findtext('Name')
            for point in method.findall('./SequencePoints/SequencePoint'):
                key = (files[point.attrib['fileid']], int(point.attrib['sl']))
                lines[key] = lines.get(key, False) or int(point.attrib['vc']) > 0
            for point in method.findall('./BranchPoints/BranchPoint'):
                key = (files[point.attrib['fileid']], name, point.attrib['offset'], point.attrib['path'], point.attrib['offsetend'])
                branches[key] = branches.get(key, False) or int(point.attrib['vc']) > 0
    return lines, branches


def gate(folders, output):
    failures, manifests, all_lines, all_branches = [], [], {}, {}
    mutants = {m: {} for m in MODULES}
    try:
        for folder in folders:
            manifest = json.loads((folder / 'manifest.json').read_text(encoding='utf-8'))
            manifests.append(manifest)
            if not manifest.get('runtime_tests_passed'):
                failures.append(str(folder) + ': runtime tests did not pass')
            if not manifest.get('clean'):
                failures.append(str(folder) + ': collection started from a dirty tree')
            if manifest['sources'] != source_hashes():
                failures.append(str(folder) + ': source hashes differ from checkout')
            runtime_cases(folder)
            lines, branches = coverage(folder)
            for target, incoming in ((all_lines, lines), (all_branches, branches)):
                for key, hit in incoming.items():
                    target[key] = target.get(key, False) or hit
            volume = json.loads((folder / 'volume' / 'volume.json').read_text(encoding='utf-8'))
            if volume['status'] != 'PASS' or volume['head'] != manifest['head'] or volume['platform'] != manifest['platform']:
                failures.append(str(folder) + ': full-volume evidence failed or mismatched')
            if manifest['platform'] == 'Darwin':
                native_evidence(folder / 'native', manifest)
            for module in MODULES:
                reports = list((folder / ('mutation-' + module)).rglob('mutation-report.json'))
                if len(reports) != 1:
                    failures.append(str(folder) + ': missing/ambiguous mutation report for ' + module)
                    continue
                report = json.loads(reports[0].read_text(encoding='utf-8'))
                for file, data in report['files'].items():
                    for mutant in data['mutants']:
                        key = mutation_key(file, mutant)
                        mutants[module].setdefault(key, set()).add(mutant['status'])
        if {m['platform'] for m in manifests} != {'Windows', 'Darwin'} or len(manifests) != 2:
            failures.append('Exactly one Windows and one macOS execution required')
        if len({m['head'] for m in manifests}) != 1 or any(m['head'] != subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip() for m in manifests):
            failures.append('Evidence must match the same exact checkout head')
    except (ValueError, KeyError, OSError, ET.ParseError) as error:
        failures.append(str(error))
    metrics = {}
    for module in MODULES:
        prefix = 'src/ChunkMaestro.' + module + '/'
        metrics[module] = {}
        for label, values, threshold in (('line', all_lines, 90), ('branch', all_branches, 85)):
            selected = [hit for key, hit in values.items() if key[0].startswith(prefix)]
            percent = 100 * sum(selected) / len(selected) if selected else None
            metrics[module][label] = {'covered': sum(selected), 'total': len(selected), 'percent': percent}
            if selected and percent < threshold:
                failures.append(f'{module} {label} below {threshold}%')
        statuses = mutants[module]
        invalid = [s for s in statuses.values() if not s <= {'Killed', 'Survived', 'NoCoverage', 'Timeout', 'CompileError', 'Ignored'}]
        applicable = [s for s in statuses.values() if s != {'CompileError'}]
        killed = sum('Killed' in s for s in applicable)
        percent = 100 * killed / len(applicable) if applicable else None
        metrics[module]['mutation'] = {'killed': killed, 'applicable': len(applicable), 'compile_error_only': len(statuses)-len(applicable), 'percent': percent}
        if invalid or (applicable and percent < 80):
            failures.append(module + ' mutation below 80% or invalid/ignored mutant')
        if not any(key[0].startswith(prefix) for key in all_lines):
            failures.append(module + ': no instrumented lines (must investigate, never infer 100%)')
    result = {'schema_version': 1, 'status': 'FAIL' if failures else 'PASS', 'failures': failures, 'metrics': metrics,
              'note': 'Implementation measurement only; not an independent QA verdict. Full-volume and interruption evidence must also pass.'}
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(result, indent=2))
    return bool(failures)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest='mode', required=True)
    collect = sub.add_parser('run')
    collect.add_argument('output', type=Path)
    combine = sub.add_parser('gate')
    combine.add_argument('folders', nargs='+', type=Path)
    combine.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    sys.exit(run(args.output.resolve()) if args.mode == 'run' else gate(args.folders, args.output))
