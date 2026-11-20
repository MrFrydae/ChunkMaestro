"""Bounded exact-source Darwin C coverage and genuine compiled mutation evidence.

Never edits the source or the production/test dylib. Each process uses a private
compiled library. Real filesystem coverage is the acceptance metric. Injected
OS failure coverage is a separate category and cannot inflate it.
"""
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[3]
RESOURCES = Path(__file__).resolve().with_suffix('')


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run(output):
    output.mkdir(parents=True, exist_ok=False)
    source = ROOT / 'src/ChunkMaestro.Persistence/Native/storage.c'
    library = ROOT / 'tests/ChunkMaestro.PersistenceTests/bin/Release/net10.0/libchunkmaestro_storage.dylib'
    original = source.read_bytes()
    original_library = digest(library) if library.exists() else None
    commands = []
    result = {'schema_version': 2, 'status': 'FAIL', 'platform': platform.system(),
              'architecture': platform.machine(), 'source_sha256': digest(source),
              'head': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
              'commands': commands, 'runner_sha256': digest(Path(__file__)),
              'clean': not subprocess.check_output(['git', 'status', '--porcelain', '--untracked-files=normal'], cwd=ROOT, text=True).strip(),
              'thresholds': {'line': 90, 'branch': 85, 'mutation': 80},
              'resources': {p.name: digest(p) for p in sorted(RESOURCES.glob('*')) if p.is_file()},
              'limitations': ['macOS host architecture only; no Windows or Intel claim',
                              'Implementation measurement, not independent QA',
                              'No privileged owner changes or secondary filesystem are used']}

    def call(args, name, env=None, stdout=None):
        record = {'args': list(map(str, args)), 'log': name + '.log', 'timeout_seconds': 60}
        commands.append(record)
        with (output / record['log']).open('w') as log:
            try:
                completed = subprocess.run(args, cwd=ROOT, env=env, stdout=stdout or log,
                                           stderr=log, timeout=60)
                record['exit_code'] = completed.returncode
            except subprocess.TimeoutExpired:
                record['exit_code'] = None
                record['timeout'] = True
        return record.get('exit_code')

    def compile_c(path, target, name, coverage=False):
        return call(['xcrun', 'clang', '-Wall', '-Wextra', '-Werror', '-dynamiclib',
                     *(['-fprofile-instr-generate', '-fcoverage-mapping'] if coverage else []),
                     str(path), '-o', str(target)], name)

    try:
        if platform.system() != 'Darwin':
            raise RuntimeError('Native helper currently targets macOS only')
        (output / 'original-storage.c').write_bytes(original)
        if library.exists():
            (output / 'original.dylib').write_bytes(library.read_bytes())
        if call(['xcrun', 'clang', '--version'], 'compiler') != 0:
            raise RuntimeError('Compiler unavailable')
        baseline = output / 'baseline.dylib'
        if compile_c(source, baseline, 'baseline-compile', True) != 0:
            raise RuntimeError('Baseline compilation failed')
        env = dict(os.environ, LLVM_PROFILE_FILE=str(output / '%p-%m.profraw'))
        suite = [sys.executable, str(RESOURCES / 'scenarios.py')]
        if call([*suite, str(baseline)], 'real-scenarios', env) != 0:
            raise RuntimeError('Real filesystem baseline failed')
        cases = json.loads((output / 'real-scenarios.log').read_text())
        result['runtime'] = {'category': cases['category'], 'passed': len(cases['cases']),
                             'failed': 0, 'report': 'real-scenarios.log'}
        profiles = sorted(output.glob('*.profraw'))
        if not profiles:
            raise RuntimeError('No executed LLVM profiles')
        merged = output / 'merged.profdata'
        if call(['xcrun', 'llvm-profdata', 'merge', '-sparse', *map(str, profiles), '-o', str(merged)], 'merge') != 0:
            raise RuntimeError('Profile merge failed')
        with (output / 'coverage.json').open('w') as report:
            if call(['xcrun', 'llvm-cov', 'export', str(baseline), '-instr-profile=' + str(merged)], 'coverage', stdout=report) != 0:
                raise RuntimeError('Coverage export failed')
        data = json.loads((output / 'coverage.json').read_text())['data'][0]
        if len(data['files']) != 1 or Path(data['files'][0]['filename']).resolve() != source:
            raise RuntimeError('Coverage source mismatch')
        result['metrics'] = data['totals']
        result['coverage_category'] = 'real-filesystem-only'
        result['uncovered_branches'] = [b for b in data['files'][0]['branches'] if b[4] == 0 or b[5] == 0]
        injected = output / 'injected'
        injected.mkdir()
        faults = injected / 'faults.o'
        if call(['xcrun', 'clang', '-Wall', '-Wextra', '-Werror', '-DCM_FAULT_IMPLEMENTATION',
                 '-c', str(RESOURCES / 'faults.c'), '-o', str(faults)], 'faults-compile') != 0:
            raise RuntimeError('Fault wrapper compilation failed')
        fault_library = injected / 'helper.dylib'
        if call(['xcrun', 'clang', '-Wall', '-Wextra', '-Werror', '-dynamiclib',
                 '-fprofile-instr-generate', '-fcoverage-mapping', '-include', str(RESOURCES / 'faults.h'),
                 str(source), str(faults), '-o', str(fault_library)], 'injected-compile') != 0:
            raise RuntimeError('Injected helper compilation failed')
        fault_env = dict(os.environ, LLVM_PROFILE_FILE=str(injected / '%p-%m.profraw'))
        if call([sys.executable, str(RESOURCES / 'injected.py'), str(fault_library)],
                'injected-scenarios', fault_env) != 0:
            raise RuntimeError('Injected failure assertions failed')
        combined = injected / 'combined.profdata'
        if call(['xcrun', 'llvm-profdata', 'merge', '-sparse',
                 *map(str, profiles), *map(str, sorted(injected.glob('*.profraw'))), '-o', str(combined)],
                'injected-merge') != 0:
            raise RuntimeError('Injected profile merge failed')
        with (output / 'combined-coverage.json').open('w') as report:
            if call(['xcrun', 'llvm-cov', 'export', str(baseline), '-instr-profile=' + str(combined)],
                    'combined-coverage', stdout=report) != 0:
                raise RuntimeError('Combined coverage export failed')
        result['controlled_failure_coverage'] = {
            'category': 'real-filesystem-plus-injected-OS-failure-or-metadata',
            'metrics': json.loads((output / 'combined-coverage.json').read_text())['data'][0]['totals'],
            'injected_cases': len(json.loads((output / 'injected-scenarios.log').read_text())['cases']),
            'report': 'injected-scenarios.log',
            'counts_as_real_OS_runtime_coverage': False}
        records = []
        result['mutation'] = {'engine': 'compiled-C-text-mutations-v1', 'mutants': records,
                              'denominator_policy': 'all catalogue entries including compile errors and timeouts; no waivers'}
        text = original.decode()
        for index, (name, before, after) in enumerate(json.loads((RESOURCES / 'mutations.json').read_text())):
            if text.count(before) != 1:
                raise RuntimeError('Mutation must select exactly one source site: ' + name)
            folder = output / ('mutant-%02d-%s' % (index, name))
            folder.mkdir()
            mutated = folder / 'storage.c'
            mutated.write_text(text.replace(before, after))
            target = folder / 'helper.dylib'
            entry = {'id': name, 'original': before, 'replacement': after,
                     'source_sha256': digest(mutated), 'source': str(mutated.relative_to(output)),
                     'line': text[:text.index(before)].count('\n') + 1,
                     'column': len(text[:text.index(before)].rsplit('\n', 1)[-1]) + 1}
            records.append(entry)
            compile_name = folder.name + '-compile'
            if compile_c(mutated, target, compile_name) != 0:
                entry['status'] = 'CompileError'
                continue
            test_name = folder.name + '-tests'
            code = call([*suite, str(target)], test_name)
            entry['test_log'] = test_name + '.log'
            if code is None:
                entry['status'] = 'Timeout'
            elif code == 0:
                position = (entry['line'], entry['column'])
                regions = [r for f in data['functions'] for r in f['regions']
                           if (r[0], r[1]) <= position < (r[2], r[3]) and r[5] == 0 and r[7] == 0]
                smallest = min(regions, key=lambda r: (r[2] - r[0], r[3] - r[1])) if regions else None
                covered = smallest is not None and smallest[4] > 0
                entry['status'] = 'Survived' if covered else 'Uncovered'
            else:
                # Crashes/infrastructure errors are not assertion kills.
                try:
                    report = json.loads((output / entry['test_log']).read_text())
                    failed = [c['name'] for c in report['cases'] if not c['passed']]
                    entry['failed_cases'] = failed
                    entry['status'] = 'Killed' if failed else 'ExecutionError'
                except (ValueError, KeyError):
                    entry['status'] = 'ExecutionError'
        counts = {s: sum(m['status'] == s for m in records) for s in
                  ('Killed', 'Survived', 'Uncovered', 'CompileError', 'Timeout', 'ExecutionError')}
        percent = 100 * counts['Killed'] / len(records) if records else 0
        result['mutation'].update({'counts': counts, 'total': len(records), 'percent': percent})
        totals = result['metrics']
        controlled = result['controlled_failure_coverage']['metrics']
        result['qualification'] = {
            'real_filesystem': totals['lines']['percent'] >= 90 and totals['branches']['percent'] >= 85,
            'controlled_failure': controlled['lines']['percent'] >= 90 and controlled['branches']['percent'] >= 85,
            'mutation': percent >= 80 and not counts['ExecutionError'] and not counts['Timeout']}
        result['failures'] = [name + ': threshold or execution requirement failed'
                              for name, passed in result['qualification'].items() if not passed]
        result['status'] = 'PASS' if not result['failures'] else 'FAIL'
    except (OSError, RuntimeError, subprocess.CalledProcessError, ValueError) as error:
        result['error'] = str(error)
    finally:
        result['preservation'] = {'source_unchanged': source.read_bytes() == original,
                                  'original_dylib_sha256': original_library,
                                  'dylib_unchanged': (digest(library) if library.exists() else None) == original_library}
        if not all(result['preservation'][k] for k in ('source_unchanged', 'dylib_unchanged')):
            result['status'] = 'FAIL'
        (output / 'native.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({k: v for k, v in result.items() if k not in ('commands', 'mutation', 'uncovered_branches')}, indent=2))
    return result['status'] != 'PASS'


if __name__ == '__main__':
    sys.exit(run(Path(sys.argv[1]).resolve()))
