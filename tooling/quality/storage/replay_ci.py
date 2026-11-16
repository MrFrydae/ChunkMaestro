"""Trusted hosted collection adapter. COLLECTED is never qualification PASS."""
import argparse
from contextlib import contextmanager
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import tempfile
import time

import replay

ROOT = Path(__file__).resolve().parents[3]
OWNER_ID = 38875853
REPOSITORY = 'MrFrydae/ChunkMaestro'
REPORTER_SHA = '751fb010242b0b44e32d84fe7fe0b9ff1da182823b94f59f5c52b001fcfc163b'
# Explicit noncredential machine/runtime keys only. No prefix-based inheritance.
ENV_KEYS = frozenset(('PATH', 'SystemRoot', 'SYSTEMROOT', 'WINDIR', 'COMSPEC',
                     'PATHEXT', 'TEMP', 'TMP', 'TMPDIR', 'HOME', 'USERPROFILE',
                     'HOMEDRIVE', 'HOMEPATH', 'LOCALAPPDATA', 'APPDATA', 'PROGRAMFILES',
                     'ProgramFiles', 'ProgramFiles(x86)', 'ProgramW6432',
                     'LANG', 'LC_ALL', 'TZ', 'NUMBER_OF_PROCESSORS',
                     'PROCESSOR_ARCHITECTURE', 'PROCESSOR_IDENTIFIER',
                     'DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_ROOT_ARM64',
                     'CHUNKMAESTRO_DOTNET'))


def child_environment(source):
    env = {key: value for key, value in source.items() if key in ENV_KEYS}
    env.update(DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1',
               DOTNET_SYSTEM_NET_DISABLEIPV6='1', MSBUILDDISABLENODEREUSE='1',
               NUGET_XMLDOC_MODE='skip')
    return env


@contextmanager
def isolated_environment(env):
    previous = dict(os.environ)
    os.environ.clear()
    os.environ.update(env)
    try:
        yield
    finally:
        os.environ.clear()
        os.environ.update(previous)


def trusted_context(env, event, head):
    if (env.get('GITHUB_REPOSITORY') != REPOSITORY or
            env.get('GITHUB_ACTOR_ID') != str(OWNER_ID) or
            event.get('sender', {}).get('id') != OWNER_ID):
        raise ValueError('Private execution requires verified repository owner context')
    if env.get('GITHUB_EVENT_NAME') == 'pull_request':
        pr = event.get('pull_request', {})
        if (pr.get('user', {}).get('id') != OWNER_ID or
                pr.get('head', {}).get('repo', {}).get('full_name') != REPOSITORY or
                pr.get('base', {}).get('repo', {}).get('full_name') != REPOSITORY or
                pr.get('head', {}).get('sha') != head):
            raise ValueError('Private execution requires same-repository owner-authored exact head')
    elif env.get('GITHUB_EVENT_NAME') != 'workflow_dispatch' or env.get('GITHUB_SHA') != head:
        raise ValueError('Unsupported or mismatched private execution context')
    return {'repository': REPOSITORY, 'actor_id': OWNER_ID,
            'event': env['GITHUB_EVENT_NAME'], 'head': head}


def complete_report(directory, record, root=ROOT, threshold=80, module=None):
    """Validate terminal, source-bound reports; code 2 only means threshold failure."""
    if record['timeout'] or record['exit_code'] not in (0, 2):
        raise ValueError('Mutation execution failed or timed out')
    reports = list(directory.rglob('mutation-report.json'))
    if len(reports) != 1:
        raise ValueError('Missing or ambiguous mutation report')
    rows = replay.catalogue(reports[0], root.resolve())
    if module and any(not row['file'].startswith('src/ChunkMaestro.' + module + '/') for row in rows):
        raise ValueError('Mutation report targets a different module')
    statuses = [row['original']['status'] for row in rows]
    if not statuses:
        # The interface-only Application module has instrumented lines but no
        # mutation candidates. Require its complete frozen source inventory;
        # this is measured N/A, never an assumed 100% mutation score.
        files = json.loads(reports[0].read_bytes())['files']
        expected = {p.resolve() for p in (root / 'src/ChunkMaestro.Application').glob('*.cs')}
        actual = {Path(name.replace('\\', '/')).resolve() if Path(name.replace('\\', '/')).is_absolute()
                  else (root / name).resolve() for name in files}
        if module != 'Application' or not expected or actual != expected or record['exit_code'] != 0:
            raise ValueError('Empty mutation report lacks complete Application inventory')
        return {'status': 'collected-no-applicable-mutants', 'report': str(reports[0]),
                'sha256': replay.digest(reports[0].read_bytes())}
    if any(s not in ('Killed', 'Survived', 'NoCoverage', 'CompileError', 'Ignored', 'Timeout') for s in statuses):
        raise ValueError('Empty or incomplete mutation report')
    # Match only Stryker's exit semantics here: it counts timeouts as detected.
    # The qualification gate separately credits actual Killed outcomes only.
    scored = [s for s in statuses if s not in ('CompileError', 'Ignored')]
    if not scored:
        raise ValueError('No executable mutation score')
    below = 100 * sum(s in ('Killed', 'Timeout') for s in scored) / len(scored) < threshold
    if (record['exit_code'] == 2) != below:
        raise ValueError('Mutation threshold exit and report disagree')
    return {'status': 'collected-threshold-fail' if below else 'collected',
            'report': str(reports[0]), 'sha256': replay.digest(reports[0].read_bytes())}


def inventory(folder):
    return {p.relative_to(folder).as_posix(): replay.digest(p.read_bytes())
            for p in sorted(folder.rglob('*')) if p.is_file()}


def preserve(source, destination):
    """Copy unaltered bytes, including logs/bin/obj; paths in provenance stay original."""
    before = inventory(source)
    shutil.copytree(source, destination)
    if inventory(destination) != before:
        raise ValueError('Artifact relocation changed bytes')
    return before


def replay_configuration(system):
    return 'tooling/quality/storage/replay/persistence-windows.json' if system == 'Windows' else 'tooling/quality/storage/replay/persistence.json'


def collect(output):
    output = output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    started = time.monotonic()
    result = {'schema_version': 1, 'status': 'UNRESOLVED', 'platform': platform.system(),
              'architecture': platform.machine(), 'private_identity': 'ChunkMaestro-private-stryker5-no-block-filter'}
    temporary = None
    try:
        head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
        event = json.loads(Path(os.environ['GITHUB_EVENT_PATH']).read_bytes())
        result['trust'] = trusted_context(os.environ, event, head)
        if platform.system() not in ('Darwin', 'Windows'):
            raise ValueError('Unsupported controller platform')
        if subprocess.check_output(['git', 'status', '--porcelain', '--untracked-files=normal'], cwd=ROOT, text=True).strip():
            raise ValueError('Private execution requires clean exact head')
        temporary = Path(tempfile.mkdtemp(prefix='chunkmaestro-replay-')).resolve()
        env = child_environment(os.environ)
        result['environment_keys'] = sorted(env)  # names only, never values
        with isolated_environment(env):
            dotnet = Path(shutil.which(os.environ.get('CHUNKMAESTRO_DOTNET', 'dotnet')) or '').resolve()
            if not dotnet.is_file():
                raise ValueError('Pinned SDK executable unavailable')
            version = subprocess.check_output([str(dotnet), '--version'], cwd=ROOT, text=True).strip()
            if version != json.loads((ROOT / 'global.json').read_bytes())['sdk']['version']:
                raise ValueError('SDK pin mismatch')
            result['sdk_version'] = version
            private = temporary / 'replay-private'
            provenance = replay.build_private(private, dotnet)
            data = json.loads(provenance.read_bytes())
            generated = 'src/Stryker.Core/Stryker.Core/Reporters/Html/Files/dist/mutation-test-elements.js'
            if data['source_after_build'].get(generated) != REPORTER_SHA:
                raise ValueError('Generated reporter JavaScript differs from reviewed bytes')
            packages = Path(os.environ.get('USERPROFILE' if os.name == 'nt' else 'HOME', '')) / '.nuget/packages'
            version = json.loads((ROOT / '.config/dotnet-tools.json').read_bytes())['tools']['dotnet-stryker']['version']
            official = packages / 'dotnet-stryker' / version / 'tools/net10.0/any/Stryker.CLI.dll'
            # Enforce terminal reports before the runner proceeds to the next tool.
            original_command = replay.command
            def checked_command(*args, **kwargs):
                record = original_command(*args, **kwargs)
                if args[3] in ('official', 'private-patched'):
                    complete_report(temporary / 'replay' / args[3], record, module='Persistence')
                return record
            replay.command = checked_command
            try:
                code = replay.run(temporary / 'replay', ROOT, dotnet, provenance, official,
                                  ROOT / replay_configuration(platform.system()),
                                  ROOT / 'tooling/quality/storage/MutationTests.csproj',
                                  timeout=5400 if platform.system() == 'Windows' else 3600,
                                  private_timeout=7200 if platform.system() == 'Windows' else None,
                                  mutation_directory=ROOT / 'tooling/quality/storage')
            finally:
                replay.command = original_command
            if code:
                raise ValueError('Replay collection unresolved; retained runner evidence')
            result['status'] = 'COLLECTED'
    except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
        result['error'] = str(error)
        print('[storage] Replay UNRESOLVED: ' + str(error), flush=True)
    finally:
        if temporary:
            result['original_temporary_root'] = str(temporary)
            try:
                result['relocated'] = {name: preserve(temporary / name, output / name)
                                       for name in ('replay', 'replay-private') if (temporary / name).exists()}
            except (OSError, ValueError) as error:
                result.update(status='UNRESOLVED', preservation_error=str(error))
            # Retain originals on failure for diagnosis; never retry a failed collection.
            if result['status'] == 'COLLECTED':
                shutil.rmtree(temporary)
        result['seconds'] = time.monotonic() - started
        replay.write(output / 'replay-collection.json', result)
    return 0 if result['status'] == 'COLLECTED' else 1


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('output', type=Path)
    sys.exit(collect(parser.parse_args().output))
