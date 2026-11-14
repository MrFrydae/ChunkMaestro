#!/usr/bin/env python3
"""Repository commands; Python 3.9+, .NET SDK from global.json. No shell evaluation."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
SOLUTION = ROOT / 'ChunkMaestro.slnx'


def projects():
    return sorted(p for base in ('src', 'tests', 'tooling/build')
                  for p in (ROOT / base).rglob('*.csproj')
                  if not {'bin', 'obj'}.intersection(p.relative_to(ROOT).parts))


def sync(check=False):
    root = ET.Element('Solution')
    for project in projects():
        ET.SubElement(root, 'Project', Path=project.relative_to(ROOT).as_posix())
    ET.indent(root, space='  ')
    expected = ET.tostring(root, encoding='unicode') + '\n'
    if check:
        if not SOLUTION.exists() or SOLUTION.read_text() != expected:
            raise RuntimeError('Solution differs from local project glob; run sync and commit it.')
    else:
        SOLUTION.write_text(expected)


def dotnet(*args, capture=False):
    exe = os.environ.get('CHUNKMAESTRO_DOTNET') or shutil.which('dotnet')
    if not exe:
        raise RuntimeError('Install the SDK pinned in global.json or set CHUNKMAESTRO_DOTNET.')
    command = [exe, *map(str, args)]
    print('+ ' + ' '.join(command), flush=True)
    environment = os.environ.copy()
    environment['DOTNET_ROOT'] = str(Path(exe).resolve().parent)
    result = subprocess.run(command, cwd=ROOT, check=True, text=True, env=environment,
                            stdout=subprocess.PIPE if capture else None)
    return result.stdout.strip() if capture else None


def sdk():
    expected = json.loads((ROOT / 'global.json').read_text())['sdk']['version']
    actual = dotnet('--version', capture=True)
    if actual != expected:
        raise RuntimeError(f'Expected .NET SDK {expected}; got {actual}.')


def restore(update=False):
    if not update:
        for project in projects():
            if not project.with_name('packages.lock.json').is_file():
                raise RuntimeError(f'Missing lock: {project}; run update-locks and commit it.')
    dotnet('restore', SOLUTION, '--force-evaluate' if update else '--locked-mode')


def build():
    dotnet('build', SOLUTION, '--no-restore', '-c', 'Release')


def test(allow_absent=False):
    tests = [p for p in projects() if dotnet('msbuild', p, '-getProperty:IsTestProject',
                                           capture=True).lower() == 'true']
    if not tests:
        message = 'NOT RUN: no .NET test projects exist. No product test evidence.'
        print(message, flush=True)
        if not allow_absent:
            raise RuntimeError(message)
        return
    for index, project in enumerate(tests):
        output = ROOT / '.artifacts' / 'tests' / str(index)
        if output.exists():
            shutil.rmtree(output)
        dotnet('test', project, '--no-restore', '--no-build', '-c', 'Release',
               '--logger', 'trx', '--results-directory', output)
        results = list(output.glob('*.trx'))
        if not results:
            raise RuntimeError(f'No TRX produced: {project}')
        for result in results:
            counters = ET.parse(result).find('.//{*}Counters')
            if counters is None or int(counters.get('executed', '0')) == 0:
                raise RuntimeError(f'Zero tests executed: {result}')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['sync', 'restore', 'update-locks', 'build',
                                          'format', 'test', 'verify', 'run'])
    parser.add_argument('--project', help='Explicit native executable project path for run')
    args = parser.parse_args()
    if args.command == 'sync':
        sync()
        return
    sdk()
    sync(check=True)
    if args.command in ('restore', 'update-locks'):
        restore(update=args.command == 'update-locks')
    elif args.command == 'build':
        build()
    elif args.command == 'format':
        dotnet('format', SOLUTION, '--verify-no-changes', '--no-restore')
    elif args.command == 'test':
        test()
    elif args.command == 'run':
        project = (ROOT / (args.project or 'src/ChunkMaestro.Desktop/ChunkMaestro.Desktop.csproj')).resolve()
        if project not in projects() or 'src' not in project.relative_to(ROOT).parts:
            raise RuntimeError('No such native product project; supply --project src/.../*.csproj.')
        dotnet('run', '--project', project, '--no-restore', '-c', 'Release')
    else:
        restore()
        build()
        dotnet('format', SOLUTION, '--verify-no-changes', '--no-restore')
        test(allow_absent=True)


if __name__ == '__main__':
    try:
        main()
    except (RuntimeError, subprocess.CalledProcessError) as error:
        print(f'ERROR: {error}', file=sys.stderr)
        sys.exit(1)
