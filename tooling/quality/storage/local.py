"""Explicit local storage qualification; no hosted runners, labels or workflow triggers."""
import argparse
import hashlib
import json
import os
import platform
from pathlib import Path
import subprocess
import sys
import time

import qualify
import replay
import replay_ci
import native
import volume


def require_local():
    if os.environ.get('GITHUB_ACTIONS', '').lower() == 'true':
        raise ValueError('Local storage commands cannot run inside GitHub Actions; no stage started.')


def check(output):
    require_local()
    return qualify.run(output, coverage_only=True)


def collect(output):
    require_local()
    if platform.system() not in ('Windows', 'Darwin'):
        raise ValueError('Collect on a real Windows or macOS machine, never substitute Linux evidence.')
    if subprocess.check_output(['git', 'status', '--porcelain'], cwd=qualify.ROOT, text=True).strip():
        raise ValueError('Commit changes before exact-head local qualification.')
    output = output.resolve()
    if output.exists():
        raise ValueError('Use a new output directory; prior evidence must not be overwritten.')
    started = time.monotonic()
    session = {'head': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=qualify.ROOT, text=True).strip(),
               'status': 'UNRESOLVED', 'phase': 'coverage-and-domain-application', 'qualification': 'NOT_EVALUATED'}
    stages = [('coverage-and-domain-application', lambda: qualify.run(output)),
              ('persistence-replay', lambda: replay_ci.collect(output, local=True)),
              ('full-volume', lambda: volume.run(output / 'volume'))]
    if platform.system() == 'Darwin':
        stages.append(('native', lambda: native.run(output / 'native')))
    try:
        for phase, execute in stages:
            session['phase'] = phase
            print('[storage-local] START ' + phase + '; raw evidence: ' + str(output), flush=True)
            if execute():
                raise ValueError('Local stage failed: ' + phase + '; inspect retained raw evidence; no automatic retry.')
        session['status'] = 'COLLECTED'
    except (ValueError, OSError, subprocess.SubprocessError) as error:
        session['error'] = str(error)
    except KeyboardInterrupt:
        session['error'] = 'Interrupted by operator; evidence remains incomplete.'
    finally:
        session['seconds'] = time.monotonic() - started
        output.mkdir(parents=True, exist_ok=True)
        replay.write(output / 'local-session.json', session)
        print(json.dumps(session), flush=True)
    return 0 if session['status'] == 'COLLECTED' else 1


def receipt(windows, macos, output):
    require_local()
    sys.path.insert(0, str(qualify.ROOT / 'tooling/build'))
    import storage_attestation
    folders = [windows.resolve(), macos.resolve()]
    output = output.resolve()
    if output.exists() or any(output.is_relative_to(folder) for folder in folders):
        raise ValueError('Use a new receipt directory outside the immutable platform evidence.')
    output.mkdir(parents=True)
    if qualify.gate(folders, output / 'gate.json'):
        raise ValueError('Local raw-evidence gate failed; no PASS attestation produced.')
    result = json.loads((output / 'gate.json').read_text(encoding='utf-8'))
    artifacts = []
    for folder in folders:
        manifest = json.loads((folder / 'manifest.json').read_text(encoding='utf-8'))
        inventory = {p.relative_to(folder).as_posix(): replay.file_digest(p)
                     for p in sorted(folder.rglob('*')) if p.is_file()}
        encoded = (json.dumps(inventory, sort_keys=True, indent=2) + '\n').encode()
        (output / (manifest['platform'] + '-inventory.json')).write_bytes(encoded)
        artifacts.append({'platform': manifest['platform'], 'location': str(folder),
                          'inventory_sha256': hashlib.sha256(encoded).hexdigest()})
    head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=qualify.ROOT, text=True).strip()
    report = {'schema_version': 1, 'head': head, 'status': 'PASS', 'failures': [],
              'collection': 'local', 'metrics': result['metrics'], 'artifacts': artifacts}
    storage_attestation.validate(report, head)
    replay.write(output / 'receipt.json', report)
    (output / 'attestation.md').write_text(storage_attestation.MARKER + '\n```json\n' +
        json.dumps(report, indent=2) + '\n```\n', encoding='utf-8')
    print('Prepared ' + str(output / 'attestation.md') + '; lead must inspect raw evidence before publication.')
    return 0


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='mode', required=True)
    for name in ('check', 'collect'):
        command = sub.add_parser(name)
        command.add_argument('output', type=Path)
    command = sub.add_parser('receipt')
    command.add_argument('--windows', type=Path, required=True)
    command.add_argument('--macos', type=Path, required=True)
    command.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    try:
        sys.exit(receipt(args.windows, args.macos, args.output) if args.mode == 'receipt' else
                 check(args.output) if args.mode == 'check' else collect(args.output))
    except (ValueError, OSError, KeyError) as error:
        print('[storage-local] BLOCKED: ' + str(error), file=sys.stderr)
        sys.exit(1)
