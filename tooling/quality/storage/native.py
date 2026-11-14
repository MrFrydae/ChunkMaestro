"""Measure actual macOS C helper execution; never substitute .NET coverage for C."""
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[3]


def run(output):
    output.mkdir(parents=True, exist_ok=False)
    dotnet = os.environ.get('CHUNKMAESTRO_DOTNET', 'dotnet')
    source = ROOT / 'src/ChunkMaestro.Persistence/Native/storage.c'
    library = ROOT / 'tests/ChunkMaestro.PersistenceTests/bin/Release/net10.0/libchunkmaestro_storage.dylib'
    result = {'status': 'FAIL', 'platform': platform.system(), 'source_sha256': hashlib.sha256(source.read_bytes()).hexdigest(),
              'head': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
              'mutation': 'UNQUALIFIED: Stryker.NET cannot mutate C; native mutation evidence is required separately'}
    saved = output / 'original.dylib'
    try:
        if platform.system() != 'Darwin':
            raise RuntimeError('Native helper currently targets macOS only')
        with (output / 'commands.log').open('w') as log:
            def call(args, env=None):
                subprocess.run(args, cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
            call(['xcrun', 'clang', '--version'])
            call([dotnet, 'build', 'tests/ChunkMaestro.PersistenceTests/ChunkMaestro.PersistenceTests.csproj', '-c', 'Release', '--no-restore'])
            shutil.copyfile(library, saved)
            call(['xcrun', 'clang', '-Wall', '-Wextra', '-Werror', '-dynamiclib', '-fprofile-instr-generate', '-fcoverage-mapping', str(source), '-o', str(library)])
            env = dict(os.environ, LLVM_PROFILE_FILE=str(output / '%p-%m.profraw'))
            call([dotnet, 'test', 'tests/ChunkMaestro.PersistenceTests/ChunkMaestro.PersistenceTests.csproj', '-c', 'Release', '--no-build', '--no-restore', '--logger', 'trx', '--results-directory', str(output / 'tests')], env)
            profiles = list(output.glob('*.profraw'))
            if not profiles:
                raise RuntimeError('No executed native profiles')
            merged = output / 'merged.profdata'
            call(['xcrun', 'llvm-profdata', 'merge', '-sparse', *map(str, profiles), '-o', str(merged)])
            with (output / 'coverage.json').open('w') as report:
                subprocess.run(['xcrun', 'llvm-cov', 'export', str(library), '-instr-profile=' + str(merged)], stdout=report, stderr=log, check=True)
            data = json.loads((output / 'coverage.json').read_text())
            totals = data['data'][0]['totals']
            result['metrics'] = totals
            result['status'] = 'PASS' if totals['lines']['percent'] >= 90 and totals['branches']['percent'] >= 85 else 'FAIL'
    except (OSError, RuntimeError, subprocess.CalledProcessError) as error:
        result['error'] = str(error)
    finally:
        if saved.exists():
            shutil.copyfile(saved, library)
        (output / 'native.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result, indent=2))
    return result['status'] != 'PASS'


if __name__ == '__main__':
    sys.exit(run(Path(sys.argv[1]).resolve()))
