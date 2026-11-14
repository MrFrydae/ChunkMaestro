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
    output.mkdir(parents=True, exist_ok=False)
    dotnet = os.environ.get('CHUNKMAESTRO_DOTNET', 'dotnet')
    env = dict(os.environ)
    # Stryker launches dotnet/MSBuild itself.
    if Path(dotnet).is_absolute():
        env['PATH'] = str(Path(dotnet).parent) + os.pathsep + env.get('PATH', '')
        env['DOTNET_ROOT'] = str(Path(dotnet).parent)
    records = []

    def command(args, cwd=ROOT):
        with (output / ('command-%02d.log' % len(records))).open('w') as log:
            code = subprocess.run(args, cwd=cwd, env=env, stdout=log, stderr=subprocess.STDOUT).returncode
        records.append({'args': args, 'exit_code': code})
        return code

    manifest = {'schema_version': 1, 'head': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
                'platform': platform.system(), 'sources': source_hashes(), 'commands': records,
                'clean': not subprocess.check_output(['git', 'status', '--porcelain', '--untracked-files=normal'], cwd=ROOT, text=True).strip()}
    try:
        if platform.system() not in ('Windows', 'Darwin'):
            raise RuntimeError('Linux is an unsupported-controller contract, not storage safety evidence')
        for args in ([dotnet, 'restore', 'tests/ChunkMaestro.PersistenceTests/ChunkMaestro.PersistenceTests.csproj', '--locked-mode'],
                     [dotnet, 'tool', 'restore'],
                     [dotnet, 'restore', 'tooling/quality/storage/MutationTests.csproj', '--locked-mode'],
                     [dotnet, 'test', 'tests/ChunkMaestro.PersistenceTests/ChunkMaestro.PersistenceTests.csproj', '-c', 'Release', '--no-restore',
                      '--collect', 'XPlat Code Coverage', '--settings', 'tooling/quality/storage/coverage.runsettings',
                      '--logger', 'trx', '--results-directory', str(output / 'coverage')]):
            if command(args):
                raise RuntimeError('Restore/build/runtime test failed; see command log')
        trx = list((output / 'coverage').glob('*.trx'))
        if len(trx) != 1:
            raise RuntimeError('Expected exactly one runtime TRX')
        counters = ET.parse(trx[0]).find('.//{*}Counters')
        if counters is None or int(counters.attrib['total']) <= 0 or counters.attrib['total'] != counters.attrib['passed']:
            raise RuntimeError('Runtime cases missing, failing or skipped')
        for module in MODULES:
            config = {'stryker-config': {'project': 'ChunkMaestro.' + module + '.csproj', 'configuration': 'Release',
                      'reporters': ['json', 'html'], 'thresholds': {'high': 90, 'low': 80, 'break': 80},
                      'mutate': ['**/*.cs', '!**/Migrations/*.Designer.cs', '!**/Migrations/*ModelSnapshot.cs'],
                      'concurrency': 2}}
            config_path = output / (module + '.json')
            config_path.write_text(json.dumps(config, indent=2))
            command([dotnet, 'stryker', '--config-file', str(config_path), '--output', str(output / ('mutation-' + module))], ROOT / 'tooling/quality/storage')
        manifest['runtime_tests_passed'] = True
    except (RuntimeError, OSError) as error:
        manifest['error'] = str(error)
        manifest['runtime_tests_passed'] = False
    finally:
        (output / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    return 0 if manifest.get('runtime_tests_passed') and all(c['exit_code'] == 0 for c in records) else 1


def canonical(path):
    path = path.replace('\\', '/')
    marker = 'src/ChunkMaestro.'
    if marker in path:
        return path[path.index(marker):]
    raise ValueError('Unexpected source path: ' + path)


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
            manifest = json.loads((folder / 'manifest.json').read_text())
            manifests.append(manifest)
            if not manifest.get('runtime_tests_passed'):
                failures.append(str(folder) + ': runtime tests did not pass')
            if not manifest.get('clean'):
                failures.append(str(folder) + ': collection started from a dirty tree')
            if manifest['sources'] != source_hashes():
                failures.append(str(folder) + ': source hashes differ from checkout')
            lines, branches = coverage(folder)
            for target, incoming in ((all_lines, lines), (all_branches, branches)):
                for key, hit in incoming.items():
                    target[key] = target.get(key, False) or hit
            volume = json.loads((folder / 'volume' / 'volume.json').read_text())
            if volume['status'] != 'PASS' or volume['head'] != manifest['head'] or volume['platform'] != manifest['platform']:
                failures.append(str(folder) + ': full-volume evidence failed or mismatched')
            if manifest['platform'] == 'Darwin':
                native = json.loads((folder / 'native' / 'native.json').read_text())
                if native['status'] != 'PASS' or native['head'] != manifest['head'] or native['source_sha256'] != manifest['sources']['src/ChunkMaestro.Persistence/Native/storage.c']:
                    failures.append('Native C coverage failed or mismatched')
            lines, branches = coverage(folder)
            for target, incoming in ((all_lines, lines), (all_branches, branches)):
                for key, hit in incoming.items():
                    target[key] = target.get(key, False) or hit
            for module in MODULES:
                reports = list((folder / ('mutation-' + module)).rglob('mutation-report.json'))
                if len(reports) != 1:
                    failures.append(str(folder) + ': missing/ambiguous mutation report for ' + module)
                    continue
                report = json.loads(reports[0].read_text())
                for file, data in report['files'].items():
                    for mutant in data['mutants']:
                        key = json.dumps([file.replace('\\', '/'), mutant['location'], mutant['mutatorName'], mutant.get('replacement')], sort_keys=True)
                        mutants[module].setdefault(key, set()).add(mutant['status'])
        if {m['platform'] for m in manifests} != {'Windows', 'Darwin'} or len(manifests) != 2:
            failures.append('Exactly one Windows and one macOS execution required')
        if len({m['head'] for m in manifests}) != 1 or any(m['head'] != subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip() for m in manifests):
            failures.append('Evidence must match the same exact checkout head')
    except (ValueError, KeyError, OSError, ET.ParseError) as error:
        failures.append(str(error))
    failures.append('Native C mutation is outside Stryker.NET; no applicable native mutation qualification yet')
    failures.append('Populated supported-version upgrade and production backup partial-publication interruption require integrated core migration/hooks; not yet qualified')
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
    output.write_text(json.dumps(result, indent=2) + '\n')
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
