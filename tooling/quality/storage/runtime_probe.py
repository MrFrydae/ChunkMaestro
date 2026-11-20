"""Bounded Windows ordinary-suite contention diagnostic; never qualification evidence."""
import argparse
import ctypes
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import time
import xml.etree.ElementTree as ET

import replay
from replay_ci import ENV_KEYS, child_environment

ROOT = Path(__file__).resolve().parents[3]
PROJECT = 'tooling/quality/storage/MutationTests.csproj'
BUDGET_SECONDS = 900
WORK_SECONDS = 870  # Keep thirty seconds inside the whole budget for termination.


def utc():
    return datetime.now(timezone.utc).isoformat()


def instant(value):
    try:
        parsed = datetime.fromisoformat(value.replace('Z', '+00:00'))
        if parsed.tzinfo is None:
            raise ValueError('Timestamp lacks timezone')
        return parsed.timestamp()
    except (TypeError, AttributeError) as error:
        raise ValueError('Invalid timestamp') from error


def inventory(directory):
    if directory.is_symlink() or any(p.is_symlink() for p in directory.rglob('*')):
        raise ValueError('Symlink in diagnostic artifacts')
    return {p.relative_to(directory).as_posix(): replay.digest(p.read_bytes())
            for p in sorted(directory.rglob('*')) if p.is_file() and p.relative_to(directory).as_posix() != 'manifest.json'}


def cases(directory, started, ended):
    reports = list(directory.glob('*.trx'))
    if len(reports) != 1:
        raise ValueError('Expected exactly one fresh full-suite TRX')
    tree = ET.parse(reports[0])
    counters = tree.find('.//{*}Counters')
    definitions = {d.get('id'): d.find('{*}TestMethod') for d in tree.findall('.//{*}UnitTest')}
    rows = []
    for result in tree.findall('.//{*}UnitTestResult'):
        method = definitions.get(result.get('testId'))
        if method is None or result.get('outcome') != 'Passed':
            raise ValueError('Missing definition, failed or skipped test')
        if not all((result.get('testId'), result.get('testName'), method.get('className'), method.get('name'))):
            raise ValueError('Test identity incomplete')
        begin, finish = result.get('startTime'), result.get('endTime')
        if not (instant(started) - 5 <= instant(begin) <= instant(finish) <= instant(ended) + 5):
            raise ValueError('Stale or invalid test timestamps')
        rows.append({'id': result.get('testId'), 'name': result.get('testName'),
                     'class': method.get('className'), 'method': method.get('name'),
                     'start': begin, 'end': finish, 'duration': result.get('duration')})
    if counters is not None and any(int(counters.get(key, '0')) != 0 for key in ('failed', 'notExecuted', 'error', 'timeout', 'aborted', 'inconclusive')):
        raise ValueError('Nonpassing test counter')
    identities = [(r['id'], r['name'], r['class'], r['method']) for r in rows]
    if (not rows or len(set(identities)) != len(rows) or counters is None or
            any(int(counters.get(key, '-1')) != len(rows) for key in ('total', 'executed', 'passed'))):
        raise ValueError('Incomplete, duplicate or empty full suite')
    return sorted(rows, key=lambda row: (row['id'], row['name']))


def recommendation(groups):
    if (len(groups) != 6 or any(type(g.get('wave')) is not int or type(g.get('concurrency')) is not int for g in groups) or
            {(g['wave'], g['concurrency']) for g in groups} != {
            (wave, size) for wave in (1, 2) for size in (1, 2, 4)}):
        raise ValueError('Missing or duplicate diagnostic group')
    rates = {}
    for group in groups:
        seconds = group['seconds']
        if not isinstance(seconds, (int, float)) or not 0 < seconds <= BUDGET_SECONDS:
            raise ValueError('Invalid group duration')
        rates[(group['wave'], group['concurrency'])] = group['completed_tests'] / seconds
    wins = [rates[(wave, 2)] > rates[(wave, 4)] for wave in (1, 2)]
    return {'selected_concurrency': 2 if all(wins) else None, 'two_beats_four': wins,
            'throughput': [{'wave': wave, 'concurrency': size, 'tests_per_second': rates[(wave, size)]}
                           for wave in (1, 2) for size in (1, 2, 4)]}


class Metrics:
    """Windows counters for the launched dotnet process only, not descendants."""
    def __init__(self, pid):
        self.handle = None
        self.error = 'NOT MEASURED: process counters unavailable'
        if os.name == 'nt':
            self.api = ctypes.WinDLL('kernel32', use_last_error=True)
            self.api.OpenProcess.restype = ctypes.c_void_p
            self.handle = self.api.OpenProcess(0x410, False, pid)

    def finish(self):
        result = {'scope': 'root dotnet process only; excludes child test hosts',
                  'cpu_seconds': None, 'peak_working_set_bytes': None, 'io': None}
        if self.handle:
            class Io(ctypes.Structure):
                _fields_ = [(name, ctypes.c_ulonglong) for name in
                            ('read_operations', 'write_operations', 'other_operations',
                             'read_bytes', 'write_bytes', 'other_bytes')]
            class Memory(ctypes.Structure):
                _fields_ = [('size', ctypes.c_ulong), ('faults', ctypes.c_ulong)] + [
                    (name, ctypes.c_size_t) for name in ('peak', 'working', 'peak_paged', 'paged',
                                                       'peak_nonpaged', 'nonpaged', 'pagefile', 'peak_pagefile')]
            handle = ctypes.c_void_p(self.handle)
            ticks = [ctypes.c_ulonglong() for _ in range(4)]
            io = Io()
            memory = Memory()
            memory.size = ctypes.sizeof(memory)
            try:
                if self.api.GetProcessTimes(handle, *[ctypes.byref(t) for t in ticks]):
                    result['cpu_seconds'] = (ticks[2].value + ticks[3].value) / 10000000
                if self.api.GetProcessIoCounters(handle, ctypes.byref(io)):
                    result['io'] = {name: getattr(io, name) for name, _ in io._fields_}
                psapi = ctypes.WinDLL('psapi', use_last_error=True)
                if psapi.GetProcessMemoryInfo(handle, ctypes.byref(memory), memory.size):
                    result['peak_working_set_bytes'] = memory.peak
            finally:
                self.api.CloseHandle(handle)
                self.handle = None
        result['unavailable'] = [key for key in ('cpu_seconds', 'peak_working_set_bytes', 'io') if result[key] is None]
        return result


def terminate(processes, deadline):
    running = [proc for proc in processes if proc.poll() is None]
    if running and os.name == 'nt':
        args = ['taskkill']
        for proc in running:
            args += ['/PID', str(proc.pid)]
        try:
            subprocess.run(args + ['/T', '/F'], timeout=max(0.1, min(10, deadline - time.monotonic())),
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False,
                           env=child_environment(os.environ))
        except (OSError, subprocess.TimeoutExpired):
            pass
    for proc in running:
        if proc.poll() is None:
            proc.kill()
    for proc in running:
        try:
            proc.wait(timeout=max(0.1, min(5, deadline - time.monotonic())))
        except subprocess.TimeoutExpired:
            raise ValueError('Process exit unconfirmed after bounded termination')


def batch(args_for, count, folder, root, env, work_deadline, final_deadline, records):
    folder.mkdir(parents=True, exist_ok=False)
    active = []
    started = time.monotonic()
    group_start = utc()
    try:
        for index in range(count):
            if time.monotonic() >= work_deadline:
                raise TimeoutError('Whole diagnostic work deadline reached')
            run = folder / ('run-' + str(index + 1))
            (run / 'results').mkdir(parents=True)
            log = (run / 'process.log').open('wb')
            args = [str(a) for a in args_for(run / 'results')]
            record = {'args': args, 'directory': run.name, 'start': utc(), 'exit_code': None}
            records.append(record)
            try:
                proc = subprocess.Popen(args, cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT,
                                        creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if os.name == 'nt' else 0,
                                        start_new_session=os.name != 'nt')
            except BaseException:
                log.close()
                raise
            active.append((proc, log, Metrics(proc.pid), record))
        pending = set(range(len(active)))
        while pending:
            if time.monotonic() >= work_deadline:
                raise TimeoutError('Whole diagnostic work deadline reached')
            for index in list(pending):
                proc, _, _, record = active[index]
                code = proc.poll()
                if code is not None:
                    record.update(exit_code=code, end=utc())
                    pending.remove(index)
                    if code:
                        raise ValueError('Ordinary test/build command failed')
            if pending:
                time.sleep(0.02)
        ended = utc()
        return {'start': group_start, 'end': ended, 'seconds': instant(ended) - instant(group_start),
                'monotonic_seconds': time.monotonic() - started}
    finally:
        try:
            terminate([item[0] for item in active], final_deadline)
        finally:
            for proc, log, metrics, record in active:
                record.setdefault('end', utc())
                record['exit_code'] = proc.poll()
                record['resources'] = metrics.finish()
                log.close()


def _validate(output, root, expected_head):
    """Reopen exact artifact bytes and source bindings; never execute artifacts."""
    output, root = Path(output), Path(root)
    actual_inventory = inventory(output)
    declared = json.loads((output / 'manifest.json').read_bytes())
    if declared != actual_inventory:
        raise ValueError('Diagnostic artifact inventory differs')
    if any(p.is_symlink() for p in output.rglob('*')):
        raise ValueError('Symlink in diagnostic artifacts')
    result = json.loads((output / 'result.json').read_bytes())
    pin = json.loads((root / 'global.json').read_bytes())['sdk']['version']
    if (result.get('schema_version') != 1 or result.get('platform') != 'Windows' or
            result.get('head') != expected_head or result.get('sdk_version') != pin or
            result.get('inputs') != replay.workspace_hashes(root, False) or
            not result.get('inputs_preserved') or not result.get('clean') or
            result.get('status') != 'RECOMMENDED' or not 0 < result.get('seconds', 0) <= BUDGET_SECONDS):
        raise ValueError('Diagnostic incomplete or source/platform/head/SDK mismatch')
    if abs(instant(result['end']) - instant(result['start']) - result['seconds']) > 1:
        raise ValueError('Whole duration differs from recorded timestamps')
    allowed = set(ENV_KEYS) | set(child_environment({}))
    if not set(result['environment_keys']) <= allowed:
        raise ValueError('Unexpected child environment keys')
    if (output / 'sdk-version/run-1/process.log').read_text(encoding='utf-8-sig').strip() != pin:
        raise ValueError('Actual SDK version log differs from pin')
    groups = result['groups']
    recommendation(groups)  # Reject traversal/noninteger/duplicate topology before paths.
    identity = None
    all_dirs = set()
    original_root = result['root'].replace('\\', '/').rstrip('/')
    original_output = result['output'].replace('\\', '/').rstrip('/')
    initial = [(['--version'], 'sdk-version'), (['--info'], 'sdk-info'),
               (['restore', original_root + '/' + PROJECT, '--locked-mode'], 'restore'),
               (['build', original_root + '/' + PROJECT, '-c', 'Release', '--no-restore'], 'build')]
    if len(result['commands']) != len(initial):
        raise ValueError('Missing SDK/restore/build command')
    for record, (args, name) in zip(result['commands'], initial):
        expected = [result['dotnet']] + args
        if (record['exit_code'] != 0 or record['directory'] != 'run-1' or
                [a.replace('\\', '/') for a in record['args']] != [a.replace('\\', '/') for a in expected]):
            raise ValueError('SDK/restore/build command differs or failed')
        if not (output / name / 'run-1/process.log').is_file():
            raise ValueError('Initialization log absent')
    for group in groups:
        if not (instant(result['start']) <= instant(group['start']) <= instant(group['end']) <= instant(result['end'])):
            raise ValueError('Group falls outside whole diagnostic timestamps')
        if instant(group['end']) - instant(group['start']) != group['seconds']:
            raise ValueError('Group duration differs from timestamps')
        runs = group['runs']
        name = f"wave-{group['wave']}-c{group['concurrency']}"
        if len(runs) != group['concurrency']:
            raise ValueError('Missing group process')
        total = 0
        for index, record in enumerate(runs):
            relative = name + '/run-' + str(index + 1)
            if relative in all_dirs or record['directory'] != 'run-' + str(index + 1):
                raise ValueError('Duplicate or invalid process directory')
            all_dirs.add(relative)
            expected = [result['dotnet'], 'test', original_root + '/' + PROJECT, '-c', 'Release',
                        '--no-build', '--no-restore', '--logger', 'trx', '--results-directory',
                        original_output + '/' + relative + '/results']
            if [a.replace('\\', '/') for a in record['args']] != [a.replace('\\', '/') for a in expected] or record['exit_code'] != 0:
                raise ValueError('Filtered, changed or failed command')
            if not (instant(group['start']) <= instant(record['start']) <= instant(record['end']) <= instant(group['end'])):
                raise ValueError('Process falls outside group timestamps')
            if not (output / relative / 'process.log').is_file():
                raise ValueError('Process log absent')
            actual = cases(output / relative / 'results', record['start'], record['end'])
            ids = [(r['id'], r['name'], r['class'], r['method']) for r in actual]
            if identity is None:
                identity = ids
            if ids != identity or record['cases'] != actual:
                raise ValueError('Full-suite identities or per-test observations differ')
            total += len(actual)
        if group['completed_tests'] != total:
            raise ValueError('Group test count differs')
    if len(all_dirs) != 14 or recommendation(groups) != result['recommendation'] or result['recommendation']['selected_concurrency'] != 2:
        raise ValueError('Missing runs or recommendation not reproduced')
    return result


def validate(output, root, expected_head):
    try:
        return _validate(output, root, expected_head)
    except (TypeError, AttributeError, IndexError, KeyError) as error:
        raise ValueError('Malformed diagnostic artifact') from error


def run(output, root=ROOT):
    root, output = Path(root).resolve(), Path(output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    started = time.monotonic()
    result = {'schema_version': 1, 'category': 'ordinary-suite-runtime-diagnostic-not-qualification',
              'status': 'INCONCLUSIVE', 'start': utc(), 'platform': platform.system(), 'architecture': platform.machine(),
              'os': platform.system() + ' ' + platform.release() + ' ' + platform.version(), 'processor_count': os.cpu_count(), 'root': str(root), 'output': str(output),
              'image': {k: os.environ.get(k) for k in ('ImageOS', 'ImageVersion', 'RUNNER_ARCH')},
              'environment_keys': [], 'commands': [], 'groups': []}
    before = None
    try:
        if platform.system() != 'Windows':
            raise ValueError('Native Windows required; other platforms provide no runtime evidence')
        env = child_environment(os.environ)
        result['head'] = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True, env=env, timeout=30).strip()
        result['clean'] = not subprocess.check_output(['git', 'status', '--porcelain'], cwd=root, text=True, env=env, timeout=30).strip()
        if not result['clean']:
            raise ValueError('Diagnostic requires clean frozen checkout')
        before = replay.workspace_hashes(root, False)
        result['inputs'] = before
        env = child_environment(os.environ)
        result['environment_keys'] = sorted(env)
        dotnet = shutil.which(env.get('CHUNKMAESTRO_DOTNET', 'dotnet'), path=env.get('PATH'))
        if not dotnet:
            raise ValueError('Pinned dotnet not found')
        result['dotnet'] = str(Path(dotnet).resolve())
        def execute(args, name):
            return batch(lambda _: [result['dotnet']] + args, 1, output / name, root, env,
                         started + WORK_SECONDS, started + BUDGET_SECONDS, result['commands'])
        execute(['--version'], 'sdk-version')
        version = (output / 'sdk-version/run-1/process.log').read_text(encoding='utf-8-sig').strip()
        if version != json.loads((root / 'global.json').read_bytes())['sdk']['version']:
            raise ValueError('Selected SDK differs from pin')
        result['sdk_version'] = version
        execute(['--info'], 'sdk-info')
        execute(['restore', str(root / PROJECT), '--locked-mode'], 'restore')
        execute(['build', str(root / PROJECT), '-c', 'Release', '--no-restore'], 'build')
        for wave in (1, 2):
            for concurrency in (1, 2, 4):
                group = {'wave': wave, 'concurrency': concurrency, 'runs': []}
                result['groups'].append(group)
                group.update(batch(lambda results: [result['dotnet'], 'test', str(root / PROJECT), '-c', 'Release',
                                  '--no-build', '--no-restore', '--logger', 'trx', '--results-directory', str(results)],
                                   concurrency, output / f'wave-{wave}-c{concurrency}', root, env,
                                   started + WORK_SECONDS, started + BUDGET_SECONDS, group['runs']))
                for index, record in enumerate(group['runs']):
                    record['cases'] = cases(output / f'wave-{wave}-c{concurrency}' / f'run-{index + 1}' / 'results', record['start'], record['end'])
                group['completed_tests'] = sum(len(r['cases']) for r in group['runs'])
        result['recommendation'] = recommendation(result['groups'])
        if result['recommendation']['selected_concurrency'] == 2:
            result['status'] = 'RECOMMENDED'
        else:
            result['error'] = 'Concurrency two did not beat four in both waves'
    except (OSError, ValueError, KeyError, TimeoutError, ET.ParseError, subprocess.SubprocessError, KeyboardInterrupt) as error:
        result['error'] = str(error)
    finally:
        result['seconds'] = time.monotonic() - started
        result['end'] = utc()
        result['inputs_preserved'] = before is not None and before == replay.workspace_hashes(root, False)
        if not result['inputs_preserved'] or result['seconds'] > BUDGET_SECONDS:
            result['status'] = 'INCONCLUSIVE'
        replay.write(output / 'result.json', result)
        replay.write(output / 'manifest.json', inventory(output))
    if result['status'] == 'RECOMMENDED':
        try:
            validate(output, root, result['head'])
        except (ValueError, KeyError, OSError, ET.ParseError) as error:
            result.update(status='INCONCLUSIVE', error=str(error))
            replay.write(output / 'result.json', result)
            replay.write(output / 'manifest.json', inventory(output))
    # Include artifact sealing and final validation in the whole-diagnostic bound.
    result.update(seconds=time.monotonic() - started, end=utc())
    if result['seconds'] > BUDGET_SECONDS:
        result.update(status='INCONCLUSIVE', error='Whole diagnostic deadline exceeded')
    replay.write(output / 'result.json', result)
    replay.write(output / 'manifest.json', inventory(output))
    if result['status'] == 'RECOMMENDED' and time.monotonic() - started > BUDGET_SECONDS:
        result.update(status='INCONCLUSIVE', error='Artifact sealing exceeded whole diagnostic deadline',
                      seconds=time.monotonic() - started, end=utc())
        replay.write(output / 'result.json', result)
        replay.write(output / 'manifest.json', inventory(output))
    print(json.dumps({k: result.get(k) for k in ('status', 'error', 'seconds', 'recommendation')}, indent=2))
    return 0 if result['status'] == 'RECOMMENDED' else 1


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('output', type=Path)
    raise SystemExit(run(parser.parse_args().output))
