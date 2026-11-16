"""Bounded full-suite EventPipe capture. Tracing never grants qualification credit."""
import argparse
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import time
import xml.etree.ElementTree as ET

import replay
import replay_ci
import runtime_probe

ROOT = Path(__file__).resolve().parents[3]
BUDGET_SECONDS = 300
EVENTPIPE = {
    'DOTNET_EnableEventPipe': '1',
    'DOTNET_EventPipeCircularMB': '20',  # Hexadecimal: 32 MiB per managed process.
    'DOTNET_EventPipeConfig': 'Microsoft-Windows-DotNETRuntime:4c14fccbd:5,'
                            'Microsoft-Windows-DotNETRuntimePrivate:4002000b:5,'
                            'Microsoft-DotNETCore-SampleProfiler:0:5',
}


def trace_environment(source, traces):
    env = replay_ci.child_environment(source)
    env.update(EVENTPIPE)
    env['DOTNET_EventPipeOutputPath'] = str(traces / 'process-{pid}.nettrace')
    return env


def run(output, root=ROOT):
    root, output = Path(root).resolve(), Path(output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    started = time.monotonic()
    result = {'category': 'ordinary-suite-cpu-profile-not-qualification', 'status': 'UNRESOLVED',
              'platform': platform.system(), 'start': runtime_probe.utc(), 'commands': [],
              'limitations': ['Sampling may drop events; decoding must assess trace completeness.',
                              'Abruptly killed fixture processes may leave incomplete traces.',
                              'Tracing changes timing; results are not throughput or safety qualification.']}
    before = None
    try:
        if platform.system() != 'Windows':
            raise ValueError('Native Windows required for this diagnostic')
        result['head'] = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip()
        if subprocess.check_output(['git', 'status', '--porcelain'], cwd=root, text=True).strip():
            raise ValueError('Profile requires clean frozen checkout')
        before = replay.workspace_hashes(root, False)
        result['inputs'] = before
        env = replay_ci.child_environment(os.environ)
        dotnet = shutil.which(env.get('CHUNKMAESTRO_DOTNET', 'dotnet'), path=env.get('PATH'))
        if not dotnet:
            raise ValueError('Pinned SDK executable unavailable')
        dotnet = str(Path(dotnet).resolve())

        def execute(args, name, child_env):
            records = []
            result['commands'].append({'label': name, 'processes': records})
            timing = runtime_probe.batch(lambda _: [dotnet] + args, 1, output / name, root, child_env,
                                         started + 270, started + BUDGET_SECONDS, records)
            return records[0], timing

        execute(['--version'], 'sdk-version', env)
        version = (output / 'sdk-version/run-1/process.log').read_text(encoding='utf-8-sig').strip()
        if version != json.loads((root / 'global.json').read_bytes())['sdk']['version']:
            raise ValueError('Selected SDK differs from pin')
        result['sdk_version'] = version
        project = str(root / runtime_probe.PROJECT)
        execute(['restore', project, '--locked-mode'], 'restore', env)
        execute(['build', project, '-c', 'Release', '--no-restore'], 'build', env)
        traces = output / 'traces'
        traces.mkdir()
        child_env = trace_environment(env, traces)
        result['trace_environment'] = {key: child_env[key] for key in (*EVENTPIPE, 'DOTNET_EventPipeOutputPath')}
        results = output / 'profile/run-1/results'
        record, timing = execute(['test', project, '-c', 'Release', '--no-build', '--no-restore',
                                  '--logger', 'trx', '--results-directory', str(results)], 'profile', child_env)
        result['cases'] = runtime_probe.cases(results, record['start'], record['end'])
        result['test_count'] = len(result['cases'])
        result['timing'] = timing
        result['traces'] = [{'file': p.relative_to(output).as_posix(), 'bytes': p.stat().st_size,
                             'sha256': replay.digest(p.read_bytes())} for p in sorted(traces.glob('*.nettrace'))]
        if not result['traces'] or not any(t['bytes'] > 32 for t in result['traces']):
            raise ValueError('No nonempty EventPipe output; no CPU profile established')
        result['status'] = 'COLLECTED_DIAGNOSTIC'
    except (OSError, ValueError, KeyError, TimeoutError, ET.ParseError, subprocess.SubprocessError, KeyboardInterrupt) as error:
        result['error'] = str(error)
    finally:
        result.update(end=runtime_probe.utc(), seconds=time.monotonic() - started,
                      inputs_preserved=before is not None and before == replay.workspace_hashes(root, False))
        if not result['inputs_preserved'] or result['seconds'] > BUDGET_SECONDS:
            result['status'] = 'UNRESOLVED'
        replay.write(output / 'result.json', result)
        replay.write(output / 'manifest.json', runtime_probe.inventory(output))
    if result['status'] == 'COLLECTED_DIAGNOSTIC' and time.monotonic() - started > BUDGET_SECONDS:
        result.update(status='UNRESOLVED', error='Whole profile deadline exceeded during artifact sealing',
                      seconds=time.monotonic() - started, end=runtime_probe.utc())
        replay.write(output / 'result.json', result)
        replay.write(output / 'manifest.json', runtime_probe.inventory(output))
    print(json.dumps({k: result.get(k) for k in ('status', 'error', 'test_count', 'seconds')}, indent=2))
    return 0 if result['status'] == 'COLLECTED_DIAGNOSTIC' else 1


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('output', type=Path)
    raise SystemExit(run(parser.parse_args().output))
