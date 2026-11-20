"""Fresh disposable mechanism experiment; <=360s total, <=180s per command.

No product execution, historical binaries, retries, or qualification verdict.
"""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import replay
import replay_ci
import shards
import test_groups as groups


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', type=Path, required=True)
    parser.add_argument('--archive', type=Path, required=True)
    parser.add_argument('--fixture-source', type=Path, required=True,
                        help='Prior fixture SOURCE only; no reports or binaries copied')
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[4]
    output = Path(tempfile.mkdtemp(prefix='cm-test-group-proof-'))
    print(output, flush=True)
    replay.write(output / 'launcher.json', {'argv': sys.argv, 'source_hashes': {
        str(p): replay.digest(p.read_bytes()) for p in (Path(__file__), Path(groups.__file__), Path(replay.__file__))}})
    deadline = time.monotonic() + 360
    commands = []
    original_command = replay.command

    def command(arguments, cwd, directory, label, env, timeout):
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise ValueError('whole experiment budget exhausted')
        record = original_command(arguments, cwd, directory, label, env, min(180, timeout, remaining))
        commands.append(record)
        replay.write(output / 'commands.json', commands)
        if label == 'private-build':
            replay.require(record)
        return record

    replay.command = command
    original_check_output = subprocess.check_output

    def bounded_metadata(arguments, **kwargs):
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise ValueError('whole experiment budget exhausted before metadata')
        kwargs['timeout'] = min(kwargs.get('timeout', 180), 180, remaining)
        return original_check_output(arguments, **kwargs)

    subprocess.check_output = bounded_metadata
    env = replay_ci.child_environment({})
    env.update(PATH=str(args.dotnet.parent) + ':/usr/bin:/bin', DOTNET_ROOT=str(args.dotnet.parent))
    # Disposable HOME with public package cache only; never inherited credentials/config.
    home = output / 'home'
    home.mkdir()
    (home / '.nuget').mkdir()
    (home / '.nuget/packages').symlink_to(Path.home() / '.nuget/packages', target_is_directory=True)
    env['HOME'] = str(home)
    env['TMPDIR'] = '/tmp/'
    with replay_ci.isolated_environment(env):
        provenance_path = replay.build_private(output / 'private', args.dotnet, args.archive)
        provenance = json.loads(provenance_path.read_bytes())
        binary = Path(provenance['binary'])
        fixture = output / 'fixture'
        shutil.copytree(repo / 'tooling/quality/storage/replay/fixture', fixture)
        # Copy bytes only from the earlier nested/compile-error fixture, not its evidence.
        for relative in ('Lib/Counter.cs', 'Lib/Nested/Extra.cs', 'Tests/CounterTests.cs'):
            target = fixture / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes((args.fixture_source / relative).read_bytes())
        tests = fixture / 'Tests/CounterTests.cs'
        tests.write_text(tests.read_text().replace('    [Fact]\n    public void NumberReturnsOne()',
                        '    [Theory]\n    [InlineData(1)]\n    [InlineData(2)]\n    public void NumberReturnsOne(int row)')
                        .replace('Assert.Equal(1, counter.Number());', 'Assert.True(row > 0);\n        Assert.Equal(1, counter.Number());'))
        (fixture / 'global.json').write_bytes((repo / 'global.json').read_bytes())
        (fixture / 'NuGet.Config').write_text('<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>')
        source_inputs = groups.inventory(fixture, [p.relative_to(fixture).as_posix() for p in fixture.rglob('*') if p.is_file()])

        def execute(label, arguments, allowed=(0,)):
            result = command([str(a) for a in arguments], fixture, output, label, env, 180)
            if result['timeout'] or result['exit_code'] not in allowed:
                raise ValueError('failed command: ' + label)
            return result

        execute('restore', [args.dotnet, 'restore', 'Tests/Tests.csproj', '--locked-mode'])

        def baseline(label, filter_value=None):
            arguments = [args.dotnet, 'test', 'Tests/Tests.csproj', '-c', 'Release', '--no-restore',
                         '--logger', 'trx', '--results-directory', output / label]
            if filter_value:
                arguments += ['--filter', filter_value]
            execute(label, arguments)
            paths = list((output / label).glob('*.trx'))
            assert len(paths) == 1
            return paths[0]

        full_baseline = baseline('baseline')
        runtime = {str(p.relative_to(fixture)): replay.digest(p.read_bytes())
                   for p in fixture.glob('*/bin/Release/**/*') if p.is_file()}
        base = {'stryker-config': {'project': 'Lib/Lib.csproj', 'test-projects': ['Tests/Tests.csproj'],
                                  'configuration': 'Release', 'reporters': ['json'], 'concurrency': 1,
                                  'mutate': shards.BASE_PATTERNS}}
        official = Path('/Users/derek/.nuget/packages/dotnet-stryker/5.0.0/tools/net10.0/any/Stryker.CLI.dll')

        def mutate(label, tool, config):
            path = fixture / (label + '.json')
            replay.write(path, config)
            result = execute(label, [args.dotnet, tool, '--config-file', path, '--output', output / label,
                                     '--log-to-file'], (0, 2))
            reports = list((output / label).rglob('mutation-report.json'))
            assert len(reports) == 1
            return {'report': str(reports[0]), 'report_sha256': replay.digest(reports[0].read_bytes()),
                    'config': config, 'exit_code': result['exit_code'], 'timeout': False, 'cancelled': False}

        original = mutate('official', official, base)
        full = mutate('private-full', binary, base)
        catalogue = shards.read_catalogue(original['report'], fixture)
        private_catalogue = shards.read_catalogue(full['report'], fixture)
        binding = {'head': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=repo).decode().strip(),
                   'sdk_version': subprocess.check_output([args.dotnet, '--version'], cwd=fixture, env=env, timeout=10).decode().strip(),
                   'sdk_sha256': replay.digest(args.dotnet.read_bytes()), 'tool_sha256': replay.digest(binary.read_bytes()),
                   'upstream_commit': replay.COMMIT, 'platform': sys.platform}
        value = groups.plan(catalogue, base, binding, groups.read_trx(full_baseline), source_inputs, 2, True)
        replay.write(output / 'plan.json', value)
        records = []
        for group in value['groups']:
            prefix = 'group-' + str(group['id'])
            selected = baseline(prefix + '-baseline', group['filter'])
            record = mutate(prefix, binary, groups.configuration(value, group['id']))
            restored = baseline(prefix + '-restored', group['filter'])
            record.update(group_id=group['id'], plan_sha256=value['plan_sha256'], baseline=str(selected),
                          restored=str(restored), test_definitions='selected')
            records.append(record)
        restored = baseline('restored')
        result = groups.validate(value, records, fixture, binding, original['report'], full_baseline, restored)
        assert private_catalogue['sources'] == catalogue['sources']
        assert {r['identity'] for r in private_catalogue['rows']} == {r['identity'] for r in catalogue['rows']}
        assert {r['identity'] for r in private_catalogue['rows'] if r['original']['status'] == 'CompileError'} == {
            r['identity'] for r in catalogue['rows'] if r['original']['status'] == 'CompileError'}
        full_tests = shards.bound_tests(json.loads(Path(full['report']).read_bytes()), fixture,
                                       {c['method'] for c in value['baseline']['cases']})
        for row in private_catalogue['rows']:
            if row['original']['status'] == 'Killed':
                assert row['original'].get('killedBy') and all(t in full_tests for t in row['original']['killedBy'])
        assert result['attributed_killed'] == sorted(r['identity'] for r in private_catalogue['rows'] if r['original']['status'] == 'Killed')
        assert sum(r['original']['status'] == 'CompileError' for r in catalogue['rows']) == 2
        assert groups.inventory(fixture, source_inputs) == source_inputs
        assert all(replay.digest((fixture / p).read_bytes()) == digest for p, digest in runtime.items())
        # A byte-identical Stryker backup is still an extra runtime member: retain and explicitly inventory it.
        final_runtime = {str(p.relative_to(fixture)): replay.digest(p.read_bytes())
                         for p in fixture.glob('*/bin/Release/**/*') if p.is_file()}
        extras = set(final_runtime) - set(runtime)
        assert all(p.endswith('.stryker-unchanged') and p.removesuffix('.stryker-unchanged') in runtime and
                   final_runtime[p] == runtime[p.removesuffix('.stryker-unchanged')] for p in extras)
        preserved = output / 'verified-backups'
        preserved.mkdir()
        for index, name in enumerate(sorted(extras)):
            backup = fixture / name
            shutil.copyfile(backup, preserved / (str(index) + '.backup'))
            assert replay.digest((preserved / (str(index) + '.backup')).read_bytes()) == final_runtime[name]
            backup.unlink()
        exact_runtime = {str(p.relative_to(fixture)): replay.digest(p.read_bytes())
                         for p in fixture.glob('*/bin/Release/**/*') if p.is_file()}
        assert exact_runtime == runtime
        result.update(final_exact_runtime=exact_runtime, baseline_cases=len(value['baseline']['cases']), compile_errors=2,
                      original_entries=len(catalogue['rows']), environment_keys=sorted(env),
                      private_provenance_sha256=replay.digest(provenance_path.read_bytes()),
                      official_sha256=replay.digest(official.read_bytes()), runtime_before=runtime,
                      runtime_after=final_runtime, extra_backup_members=sorted(extras))
        replay.write(output / 'records.json', records)
        replay.write(output / 'result.json', result)
        replay.write(output / 'artifact-sha256.json', replay.hashes(output))
        print(json.dumps(result), flush=True)


if __name__ == '__main__':
    main()
