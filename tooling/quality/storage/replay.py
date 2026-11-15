"""Private pinned Stryker instrumentation experiment; never an official tool or gate.

No literal C# rewrites. All official catalogue rows retain their original status.
Only freshly collected reports on the same frozen inputs can be supplemented.
"""
import argparse
from collections import Counter, defaultdict
import difflib
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import signal
import subprocess
import sys
import tarfile
import time
import urllib.request
import uuid
import xml.etree.ElementTree as ET

COMMIT = '6e77a3451bac4793e9c839c3ff4055c1b30ca3af'
ARCHIVE_URL = 'https://codeload.github.com/stryker-mutator/stryker-net/tar.gz/' + COMMIT
ARCHIVE_SHA = '80702658bac0989e9416e43239ee9d0ca971cea79628bed2b7b1917edd07b269'
FACTORY = 'src/Stryker.Core/Stryker.Core/MutantFilters/MutantFilterFactory.cs'
PATCHED_FACTORY_SHA = 'ba884d6b60901af8743e3942f12a9237ef8f61c1874029dadc05789858156f5f'
FACTORY_SHA = '203c899e3f5bdd8b0f263b02559ab8f2d7c20754e6bd7c501ac2a1c6b360d3e7'
REMOVE = b'                new IgnoreBlockMutantFilter(),\n'
RESOURCE = Path(__file__).with_suffix('')


def digest(data):
    return hashlib.sha256(data).hexdigest()


def write(path, value):
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + '\n', encoding='utf-8')


def hashes(root):
    return {p.relative_to(root).as_posix(): digest(p.read_bytes()) for p in sorted(root.rglob('*'))
            if p.is_file() and not {'bin', 'obj', '.git'}.intersection(p.relative_to(root).parts)}


def workspace_hashes(root, fixture):
    if fixture:
        values = hashes(root)
        values.pop('.chunkmaestro-replay.lock', None)
        return values
    paths = subprocess.check_output(['git', 'ls-files', '-z'], cwd=root).decode().split('\0')
    return {path: digest((root / path).read_bytes()) for path in sorted(paths) if path}


def patch(data):
    if digest(data) != FACTORY_SHA or data.count(REMOVE) != 1:
        raise ValueError('Pinned factory bytes differ; refusing broader patch')
    return data.replace(REMOVE, b'', 1)


def environment(dotnet):
    env = dict(os.environ)
    env.update(DOTNET_SYSTEM_NET_DISABLEIPV6='1', MSBUILDDISABLENODEREUSE='1',
               DOTNET_ROOT=str(dotnet.parent), PATH=str(dotnet.parent) + os.pathsep + env.get('PATH', ''))
    return env


def command(args, cwd, directory, label, env, timeout):
    """Unique log, process-group timeout. A nonzero exit never means a kill."""
    log = directory / (label + '-' + uuid.uuid4().hex + '.log')
    started = time.monotonic()
    timed_out = False
    with log.open('wb') as stream:
        proc = subprocess.Popen([str(a) for a in args], cwd=cwd, env=env, stdout=stream,
                                stderr=subprocess.STDOUT, start_new_session=os.name != 'nt')
        try:
            code = proc.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            if os.name == 'nt':
                subprocess.run(['taskkill', '/PID', str(proc.pid), '/T', '/F'],
                               stdout=stream, stderr=subprocess.STDOUT, check=False)
            else:
                os.killpg(proc.pid, signal.SIGKILL)
            code = proc.wait()
    result = {'args': [str(a) for a in args], 'cwd': str(cwd), 'exit_code': code,
              'timeout': timed_out, 'seconds': time.monotonic() - started,
              'log': str(log), 'log_sha256': digest(log.read_bytes())}
    with (directory / 'commands.jsonl').open('a', encoding='utf-8') as stream:
        stream.write(json.dumps(result, sort_keys=True) + '\n')
        stream.flush()
        os.fsync(stream.fileno())
    return result


def require(result):
    if result['timeout'] or result['exit_code']:
        raise ValueError('Command failed or timed out; see ' + result['log'])


def build_private(output, dotnet, archive=None):
    if sys.version_info < (3, 12):
        raise ValueError('Private source extraction requires Python 3.12+ data-filter support')
    output = output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    archive_path = output / 'official-source.tar.gz'
    if archive:
        archive_path.write_bytes(archive.read_bytes())
    else:
        with urllib.request.urlopen(ARCHIVE_URL, timeout=60) as response:
            archive_path.write_bytes(response.read())
    if digest(archive_path.read_bytes()) != ARCHIVE_SHA:
        raise ValueError('Official source archive SHA256 differs')
    with tarfile.open(archive_path) as package:
        package.extractall(output, filter='data')
    source = output / ('stryker-net-' + COMMIT)
    original = hashes(source)
    factory = source / FACTORY
    before = factory.read_bytes()
    factory.write_bytes(patch(before))
    after = hashes(source)
    if {key for key in original if original[key] != after[key]} != {FACTORY}:
        raise ValueError('Unexpected source delta')
    (output / 'filter.patch').write_text(''.join(difflib.unified_diff(
        before.decode().splitlines(True), factory.read_text().splitlines(True),
        fromfile='a/' + FACTORY, tofile='b/' + FACTORY)), encoding='utf-8')
    env = environment(dotnet)
    result = command([dotnet, 'build', source / 'src/Stryker.CLI/Stryker.CLI/Stryker.CLI.csproj',
                      '-c', 'Release', '--no-incremental', '--force', '-p:RestoreLockedMode=true'],
                     source, output, 'private-build', env, 600)
    binary = source / 'src/Stryker.CLI/Stryker.CLI/bin/Release/Stryker.CLI.dll'
    provenance = {'schema_version': 1, 'identity': 'ChunkMaestro-private-stryker5-no-block-filter',
                  'official_tool': False, 'builder_sha256': digest(Path(__file__).read_bytes()), 'upstream_commit': COMMIT, 'archive_url': ARCHIVE_URL,
                  'archive_sha256': ARCHIVE_SHA, 'source_before': original, 'source_after': after,
                  'patch_sha256': digest((output / 'filter.patch').read_bytes()),
                  'factory_before_sha256': digest(before), 'factory_after_sha256': digest(factory.read_bytes()),
                  'sdk_executable_sha256': digest(dotnet.read_bytes()),
                  'dotnet_info': subprocess.check_output([str(dotnet), '--info'], env=env, text=True),
                  'compiler_files': {str(p): digest(p.read_bytes()) for p in sorted((dotnet.parent / 'sdk').glob('*/Roslyn/bincore/*')) if p.is_file()},
                  'build': result, 'binary': str(binary),
                  'dependencies_and_outputs': {str(p.relative_to(source)): digest(p.read_bytes())
                                               for p in sorted(source.rglob('*')) if p.is_file() and
                                               (p.name in ('project.assets.json', 'packages.lock.json') or 'bin' in p.parts)}}
    write(output / 'provenance.json', provenance)
    require(result)
    built = hashes(source)
    generated = set(built) - set(after)
    if (any(built.get(k) != v for k, v in after.items()) or
            generated != {'src/Stryker.Core/Stryker.Core/Reporters/Html/Files/dist/mutation-test-elements.js'}):
        raise ValueError('Unexpected build change to third-party source/locks/resources')
    provenance['source_after_build'] = built
    write(output / 'provenance.json', provenance)
    return output / 'provenance.json'


def offset(source, position):
    # JSON positions are one-based UTF16 columns, exclusive ends. Do not use splitlines
    # (it treats Unicode separators as lines that Roslyn does not).
    boundaries = list(re.finditer(r'\r\n|\r|\n', source))
    starts = [0] + [match.end() for match in boundaries]
    ends = [match.start() for match in boundaries] + [len(source)]
    line, column = position['line'] - 1, position['column'] - 1
    if not 0 <= line < len(starts):
        raise ValueError('Location outside source')
    text = source[starts[line]:ends[line]].encode('utf-16-le')
    if not 0 <= column <= len(text) // 2:
        raise ValueError('Location outside source')
    text[:column * 2].decode('utf-16-le')
    return len(source[:starts[line]].encode('utf-16-le')) // 2 + column



def catalogue(report, root):
    raw = report.read_bytes()
    rows = []
    for name, file in json.loads(raw)['files'].items():
        source = file['source']
        if re.search(r'^\s*#line\b', source, re.M):
            raise ValueError('Mapped source needs Roslyn resolution')
        path = Path(name.replace('\\', '/'))
        if not path.is_absolute():
            path = root / path
        path = path.resolve()
        if not path.is_relative_to(root):
            raise ValueError('Report path escapes workspace')
        if path.read_bytes() != source.encode('utf-8'):
            raise ValueError('Report source bytes differ (including BOM/newlines): ' + str(path))
        relative = path.relative_to(root).as_posix()
        for mutant in file['mutants']:
            start, end = (offset(source, mutant['location'][k]) for k in ('start', 'end'))
            if end < start:
                raise ValueError('Reversed span')
            span = source.encode('utf-16-le')[start * 2:end * 2]
            identity = [relative, digest(source.encode()), mutant['location'], mutant['mutatorName'], mutant['replacement']]
            rows.append({'ordinal': len(rows), 'identity': digest(json.dumps(identity, sort_keys=True).encode()),
                         'file': relative, 'source_sha256': digest(source.encode()), 'original': mutant,
                         'span_utf16': [start, end], 'span_sha256': digest(span),
                         'report_sha256': digest(raw), 'supplemental_status': 'NOT_RUN'})
    return rows


def test_cases(directory):
    reports = list(directory.glob('*.trx'))
    if len(reports) != 1:
        raise ValueError('Expected one full-suite TRX')
    tree = ET.parse(reports[0])
    counters = tree.find('.//{*}Counters')
    results = tree.findall('.//{*}UnitTestResult')
    definitions = {d.get('id'): (d.find('{*}TestMethod').get('className'),
                                d.find('{*}TestMethod').get('name'))
                   for d in tree.findall('.//{*}UnitTest')}
    cases = sorted((definitions[r.get('testId')], r.get('testName'), r.get('outcome')) for r in results)
    if (counters is None or not cases or len(cases) != int(counters.get('total', '0'))
            or int(counters.get('executed', '0')) != len(cases)
            or int(counters.get('passed', '0')) != len(cases) or any(c[2] != 'Passed' for c in cases)):
        raise ValueError('Full baseline missing, skipped or failed')
    return cases


def supplement(original, patched):
    by_key = defaultdict(list)
    originals = Counter(row['identity'] for row in original)
    for row in patched:
        by_key[row['identity']].append(row)
    for row in original:
        row = dict(row)
        matches = by_key[row['identity']]
        if originals[row['identity']] != 1 or len(matches) != 1:
            row['supplemental_status'] = 'UNRESOLVED_IDENTITY'
        else:
            match = matches[0]
            row['patched_original'] = match['original']
            row['patched_report_sha256'] = match['report_sha256']
            status = match['original']['status']
            row['supplemental_status'] = status if status in ('Killed', 'Survived', 'NoCoverage') else 'UNRESOLVED_' + status
            if status == 'Killed' and not match['original'].get('killedBy'):
                row['supplemental_status'] = 'UNRESOLVED_NO_FAILED_TEST'
        yield row


def run(output, root, dotnet, provenance_path, official, config, test_project, fixture=False, timeout=600,
        mutation_directory=None):
    root, output = root.resolve(), output.resolve()
    config, test_project = config.resolve(), test_project.resolve()
    provenance_path, official = provenance_path.resolve(), official.resolve()
    mutation_directory = (mutation_directory or root).resolve()
    if not mutation_directory.is_relative_to(root) or not mutation_directory.is_dir():
        raise ValueError('Mutation discovery directory must be inside the frozen workspace')
    if not config.is_relative_to(root) or not test_project.is_relative_to(root):
        raise ValueError('Configuration and test project must be frozen workspace inputs')
    if output.is_relative_to(root):
        raise ValueError('Use an external artifact directory')
    output.mkdir(parents=True, exist_ok=False)
    lock = root / '.chunkmaestro-replay.lock'
    fd = os.open(lock, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    os.close(fd)
    before = None
    baseline_cases = None
    result = {'schema_version': 1, 'category': 'fixture-only' if fixture else 'production-experiment',
              'status': 'UNRESOLVED', 'platform': platform.platform(), 'architecture': platform.machine(),
              'mutation_directory': mutation_directory.relative_to(root).as_posix(),
              'thresholds': {'line': 90, 'branch': 85, 'mutation': 80}}
    env = environment(dotnet)
    def execute(args, label):
        record = command(args, root, output, label, env, timeout)
        require(record)
    def baseline(label):
        execute([dotnet, 'clean', test_project, '-c', 'Release'], label + '-clean')
        execute([dotnet, 'build', test_project, '-c', 'Release', '--no-restore', '--no-incremental'], label + '-build')
        execute([dotnet, 'test', test_project, '-c', 'Release', '--no-build', '--no-restore',
                 '--logger', 'trx', '--results-directory', output / label], label + '-test')
        cases = test_cases(output / label)
        write(output / (label + '-binaries.json'), {str(p.relative_to(root)): digest(p.read_bytes())
              for p in sorted(root.rglob('*')) if p.is_file() and 'bin' in p.relative_to(root).parts})
        return cases
    try:
        provenance = json.loads(provenance_path.read_bytes())
        if provenance['identity'] != 'ChunkMaestro-private-stryker5-no-block-filter' or provenance['archive_sha256'] != ARCHIVE_SHA:
            raise ValueError('Unexpected private tool identity')
        for path, expected in provenance['compiler_files'].items():
            if digest(Path(path).read_bytes()) != expected:
                raise ValueError('Private-build compiler changed')
        result['official_tool_files'] = hashes(official.parent)
        result['sdk_version'] = subprocess.check_output([str(dotnet), '--version'], cwd=root, env=env, text=True).strip()
        pin = root / 'global.json'
        if pin.exists() and result['sdk_version'] != json.loads(pin.read_text())['sdk']['version']:
            raise ValueError('SDK differs from workspace pin')
        binary = Path(provenance['binary'])
        source = binary.parents[5]
        if (provenance['upstream_commit'] != COMMIT or provenance['factory_before_sha256'] != FACTORY_SHA
                or digest((source / FACTORY).read_bytes()) != PATCHED_FACTORY_SHA
                or provenance['build']['exit_code'] != 0 or provenance['build']['timeout']):
            raise ValueError('Private tool patch/build provenance invalid')
        if digest((provenance_path.parent / 'official-source.tar.gz').read_bytes()) != ARCHIVE_SHA:
            raise ValueError('Private source archive changed')
        if digest((provenance_path.parent / 'filter.patch').read_bytes()) != provenance['patch_sha256']:
            raise ValueError('Private patch changed')
        # Validate every built dependency, not just the entry point.
        for path, expected in provenance['dependencies_and_outputs'].items():
            if digest((source / path).read_bytes()) != expected:
                raise ValueError('Private tool dependency changed')
        if provenance['source_after_build'] != hashes(source):
            raise ValueError('Private upstream source changed')
        if not fixture:
            if subprocess.check_output(['git', 'status', '--porcelain', '--untracked-files=normal'], cwd=root, text=True).strip().replace('?? .chunkmaestro-replay.lock', '').strip():
                raise ValueError('Production requires clean exact-head workspace')
            result['head'] = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip()
        else:
            result['head'] = None
        before = workspace_hashes(root, fixture)
        if config.relative_to(root).as_posix() not in before or test_project.relative_to(root).as_posix() not in before:
            raise ValueError('Configuration and project must be inventoried inputs')
        result.update(inputs=before, runner_sha256=digest(Path(__file__).read_bytes()), config_sha256=digest(config.read_bytes()),
                      official_binary_sha256=digest(official.read_bytes()),
                      provenance_sha256=digest(provenance_path.read_bytes()),
                      sdk_sha256=digest(dotnet.read_bytes()))
        if result['sdk_sha256'] != provenance['sdk_executable_sha256']:
            raise ValueError('SDK changed since private build')
        write(output / 'binding.json', result)
        execute([dotnet, 'restore', test_project, '--locked-mode'], 'restore')
        baseline_cases = baseline('baseline')
        result['baseline_cases'] = baseline_cases
        frozen = config.read_bytes()
        for label, tool in [('official', official), ('private-patched', binary)]:
            if config.read_bytes() != frozen:
                raise ValueError('Configuration changed')
            mutation_command = command([dotnet, tool, '--config-file', config, '--output', output / label, '--log-to-file'],
                                       mutation_directory, output, label, env, timeout)
            # Pinned Stryker ExitCodes.BreakThresholdViolated is 2. Retain the
            # failed threshold while collecting its report; never turn it into PASS.
            if mutation_command['timeout'] or mutation_command['exit_code'] not in (0, 2):
                require(mutation_command)
            result.setdefault('tool_threshold_failures', {})[label] = mutation_command['exit_code'] == 2
            reports = list((output / label).rglob('mutation-report.json'))
            if len(reports) != 1:
                raise ValueError('Missing or ambiguous mutation report')
            (output / (label + '.json')).write_bytes(reports[0].read_bytes())
            if label == 'official':
                original = catalogue(output / 'official.json', root)
                write(output / 'catalogue.json', original)
                result['official_report_sha256'] = digest((output / 'official.json').read_bytes())
                result['fixed_applicable_denominator'] = sum(r['original']['status'] != 'CompileError' for r in original)
                result['original_counts'] = dict(Counter(r['original']['status'] for r in original))
                with (output / 'ledger.jsonl').open('x', encoding='utf-8') as stream:
                    for row in original:
                        stream.write(json.dumps(row, sort_keys=True) + '\n')
                    stream.flush()
                    os.fsync(stream.fileno())
        patched = catalogue(output / 'private-patched.json', root)
        write(output / 'patched-catalogue.json', patched)
        result['fixed_applicable_denominator'] = sum(r['original']['status'] != 'CompileError' for r in original)
        result['original_counts'] = dict(Counter(r['original']['status'] for r in original))
        with (output / 'ledger.jsonl').open('a', encoding='utf-8') as stream:
            for row in supplement(original, patched):
                stream.write(json.dumps(row, sort_keys=True) + '\n')
                stream.flush()
                os.fsync(stream.fileno())
        result['status'] = 'COLLECTED'  # deliberately never PASS or gate qualification
    except (OSError, ValueError, KeyError, ET.ParseError) as error:
        result['error'] = str(error)
    finally:
        if baseline_cases is not None:
            try:
                if baseline('restored') != baseline_cases:
                    raise ValueError('Restoration baseline identities differ')
                result['baseline_restored'] = True
            except (OSError, ValueError, KeyError, ET.ParseError) as error:
                result['restoration_error'] = str(error)
                result['status'] = 'UNRESOLVED'
        if result.get('official_report_sha256') and digest((output / 'official.json').read_bytes()) != result['official_report_sha256']:
            result['error'] = 'Immutable official report changed'
            result['status'] = 'UNRESOLVED'
        if before is not None:
            after = workspace_hashes(root, fixture)
            result['inputs_preserved'] = before == after
            if before != after:
                result['status'] = 'UNRESOLVED'
        lock.unlink(missing_ok=True)
        result['artifacts'] = hashes(output)
        write(output / 'result.json', result)
    return 0 if result['status'] == 'COLLECTED' else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', type=Path, default=Path(os.environ.get('CHUNKMAESTRO_DOTNET', 'dotnet')))
    sub = parser.add_subparsers(dest='mode', required=True)
    build = sub.add_parser('build-private')
    build.add_argument('output', type=Path)
    build.add_argument('--archive', type=Path)
    collect = sub.add_parser('run')
    for name in ('output', 'root', 'provenance', 'official', 'config', 'test-project'):
        collect.add_argument('--' + name, type=Path, required=True)
    collect.add_argument('--fixture', action='store_true')
    collect.add_argument('--timeout', type=int, default=600)
    collect.add_argument('--mutation-directory', type=Path)
    args = parser.parse_args()
    if args.mode == 'build-private':
        print(build_private(args.output, args.dotnet.resolve(), args.archive))
        return 0
    return run(args.output, args.root, args.dotnet.resolve(), args.provenance, args.official,
               args.config, args.test_project, args.fixture, args.timeout, args.mutation_directory)


if __name__ == '__main__':
    sys.exit(main())
