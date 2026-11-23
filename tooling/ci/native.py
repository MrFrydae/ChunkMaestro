"""Direct C helper filesystem/failure tests; compile once per test mode."""
import json
import os
from pathlib import Path
import subprocess
import sys

from command import child_environment
from storage import ROOT, digest_inputs
from evidence import inventory


def main(output):
    output.mkdir(parents=True, exist_ok=False)
    env = child_environment(os.environ)
    env['LLVM_PROFILE_FILE'] = str(output.resolve() / '%p.profraw')
    source = ROOT / 'src/ChunkMaestro.Persistence/Native/storage.c'
    fixtures = ROOT / 'tooling/quality/storage/native'

    def run(args, result=None):
        print('+ ' + ' '.join(map(str, args)), flush=True)
        if result:
            with (output / result).open('w') as stream:
                subprocess.run(list(map(str, args)), check=True, env=env, stdout=stream)
        else:
            subprocess.run(list(map(str, args)), check=True, env=env)

    library = output / 'helper.dylib'
    run(['xcrun', 'clang', '-Wall', '-Wextra', '-Werror', '-dynamiclib',
         '-fprofile-instr-generate', '-fcoverage-mapping', source, '-o', library])
    run([sys.executable, fixtures / 'scenarios.py', library.resolve()], 'filesystem.json')
    profiles = list(output.glob('*.profraw'))
    if not profiles:
        raise ValueError('Missing native execution profiles')
    run(['xcrun', 'llvm-profdata', 'merge', '-sparse', *profiles, '-o', output / 'coverage.profdata'])
    run(['xcrun', 'llvm-cov', 'export', library, '-instr-profile=' + str(output / 'coverage.profdata')], 'coverage.json')
    run(['xcrun', 'clang', '-Wall', '-Wextra', '-Werror', '-DCM_FAULT_IMPLEMENTATION',
         '-c', fixtures / 'faults.c', '-o', output / 'faults.o'])
    run(['xcrun', 'clang', '-Wall', '-Wextra', '-Werror', '-dynamiclib',
         '-include', fixtures / 'faults.h', source, output / 'faults.o', '-o', output / 'injected.dylib'])
    run([sys.executable, fixtures / 'injected.py', (output / 'injected.dylib').resolve()], 'injected.json')
    (output / 'execution.json').write_text(json.dumps({
        'head': subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip(),
        'inputs': digest_inputs(), 'system': 'Darwin',
        'files': inventory(output, exclude=('execution.json',)),
        'scope': 'real filesystem plus separately labeled injected OS errors; no mutation score'}, indent=2) + '\n')
    return 0


if __name__ == '__main__':
    sys.exit(main(Path(sys.argv[1])))
