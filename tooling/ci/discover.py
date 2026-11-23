"""VSTest discovery only: bind method/Theory identities before direct execution."""
import argparse
import json
import os
from pathlib import Path
import platform
import subprocess

from command import child_environment
from storage import ROOT, plan, PLAN


def discover(group, output):
    system = platform.system()
    classes = plan(system)[group]
    output.mkdir(parents=True, exist_ok=False)
    filter_text = '|'.join('FullyQualifiedName~ChunkMaestro.PersistenceTests.' + name + '.' for name in classes)
    args = [os.environ.get('CHUNKMAESTRO_DOTNET', 'dotnet'), 'test',
            'tests/ChunkMaestro.PersistenceTests/ChunkMaestro.PersistenceTests.csproj',
            '-c', 'Release', '--no-build', '--no-restore', '--list-tests', '--filter', filter_text]
    print('+ ' + ' '.join(args), flush=True)
    env = child_environment(os.environ)
    env['DOTNET_CLI_UI_LANGUAGE'] = 'en-US'
    result = subprocess.run(args, cwd=ROOT, env=env, capture_output=True, text=True, timeout=60)
    (output / 'discovery.log').write_text(result.stdout + result.stderr)
    if result.returncode:
        raise ValueError('VSTest discovery failed; inspect discovery.log')
    prefix = 'ChunkMaestro.PersistenceTests.'
    names = [line.strip() for line in result.stdout.splitlines() if line.strip().startswith(prefix)]
    if not names or len(names) != len(set(names)):
        raise ValueError('Empty or duplicate discovered case inventory')
    counts = json.loads(PLAN.read_text())['expected_cases'][system]
    for name in classes:
        if sum(case.startswith(prefix + name + '.') for case in names) != counts[name]:
            raise ValueError('Discovered cases differ from the explicit test plan')
    if len(names) != sum(counts[name] for name in classes):
        raise ValueError('Discovery includes unexpected classes')
    report = {'head': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
              'system': system, 'group': group, 'cases': names}
    (output / 'case-inventory.json').write_text(json.dumps(report, indent=2) + '\n')
    print('Discovered ' + str(len(names)) + ' exact test cases; no tests executed in discovery.', flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--group', required=True)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    discover(args.group, args.output)
