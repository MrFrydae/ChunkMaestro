"""Bounded historical-private-tool mechanism proof, never qualification.

Usage: python3 .../run.py --dotnet PATH --provenance PATH --prior PATH
Writes only a fresh disposable directory; stops at first failed/timeout command.
"""
import argparse
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tempfile
import tarfile
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import replay
import replay_ci
import shards


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', type=Path, required=True)
    parser.add_argument('--provenance', type=Path, required=True)
    parser.add_argument('--prior', type=Path, required=True)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[4]
    output = Path(tempfile.mkdtemp(prefix='cm-shard-proof-'))
    print(output, flush=True)
    provenance = json.loads(args.provenance.read_bytes())
    # Historical diagnostic contract: verify ALL original recorded tool files,
    # archive and sole patched source. Preserve advisories in original build log.
    source = args.provenance.parent / ('stryker-net-' + replay.COMMIT)
    assert provenance['upstream_commit'] == replay.COMMIT
    assert provenance['archive_sha256'] == replay.ARCHIVE_SHA
    assert replay.digest((args.provenance.parent / 'official-source.tar.gz').read_bytes()) == replay.ARCHIVE_SHA
    with tarfile.open(args.provenance.parent / 'official-source.tar.gz') as archive:
        archived = {'/'.join(Path(m.name).parts[1:]): replay.digest(archive.extractfile(m).read())
                    for m in archive if m.isfile()}
    assert archived == provenance['source_before']
    after = provenance['source_after']
    assert after.keys() == archived.keys()
    assert {k for k in archived if archived[k] != after[k]} == {replay.FACTORY}
    assert archived[replay.FACTORY] == replay.FACTORY_SHA
    built = provenance['source_after_build']
    generated = 'src/Stryker.Core/Stryker.Core/Reporters/Html/Files/dist/mutation-test-elements.js'
    assert set(built) - set(after) == {generated}
    assert all(built[k] == v for k, v in after.items())
    assert built[generated] == replay_ci.REPORTER_SHA
    assert replay.hashes(source) == built
    assert provenance['identity'] == 'ChunkMaestro-private-stryker5-no-block-filter'
    assert provenance['build']['exit_code'] == 0 and not provenance['build']['timeout']
    assert replay.digest(Path(provenance['build']['log']).read_bytes()) == provenance['build']['log_sha256']
    assert provenance['factory_after_sha256'] == replay.PATCHED_FACTORY_SHA
    assert replay.digest((source / replay.FACTORY).read_bytes()) == replay.PATCHED_FACTORY_SHA
    for name, digest in provenance['dependencies_and_outputs'].items():
        assert replay.digest((source / name).read_bytes()) == digest
    for name, digest in provenance['compiler_files'].items():
        assert replay.digest(Path(name).read_bytes()) == digest
    assert replay.digest(args.dotnet.read_bytes()) == provenance['sdk_executable_sha256']
    fixture = output / 'fixture'
    shutil.copytree(repo / 'tooling/quality/storage/replay/fixture', fixture)
    # Reuse original nested fixture and all tests, including the fifth assertion.
    prior = json.loads((args.prior / 'private-full.report.json').read_bytes())
    for name, file in prior['files'].items():
        (fixture / 'Lib' / Path(name).name).write_bytes(file['source'].encode())
    shutil.copyfile(args.prior / 'fixture/Tests/CounterTests.cs', fixture / 'Tests/CounterTests.cs')
    extra = fixture / 'Lib/Extra.cs'
    code = extra.read_text().replace('public static int Three()', '''// Astral 😀 before the original-node locations.
    public static bool Conditional(out int value)
    {
        if (true)
        {
            value = 1;
        }
        else
        {
            value = 2;
        }
        return true;
    }

    public static bool Positive(object input)
    {
        if (input is int number && number > 0)
        {
            return true;
        }
        return false;
    }

    public static int Three()''')
    extra.write_bytes(code.replace('\n', '\r\n').encode())
    tests = fixture / 'Tests/CounterTests.cs'
    tests.write_text(tests.read_text().replace('Assert.Equal(3, Extra.Three());', '''Assert.Equal(3, Extra.Three());
        Assert.True(Extra.Conditional(out int value));
        Assert.Equal(1, value);
        Assert.True(Extra.Positive(1));
        Assert.False(Extra.Positive(-1));'''))
    (fixture / 'global.json').write_bytes((repo / 'global.json').read_bytes())
    env = replay_ci.child_environment({})
    env.update(DOTNET_ROOT=str(args.dotnet.parent), PATH=str(args.dotnet.parent) + os.pathsep + '/usr/bin:/bin')
    commands = []

    def execute(label, arguments, allowed=(0,)):
        log = output / (label + '.log')
        start = time.monotonic()
        record = {'label': label, 'args': [str(a) for a in arguments], 'timeout': False, 'cancelled': False}
        try:
            with log.open('wb') as stream:
                process = subprocess.Popen(record['args'], cwd=fixture, env=env, stdout=stream,
                                           stderr=subprocess.STDOUT, start_new_session=True)
                try:
                    record['exit_code'] = process.wait(timeout=150)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait(timeout=5)
                    raise
        except subprocess.TimeoutExpired:
            record.update(timeout=True, exit_code=None)
            raise
        finally:
            record.update(seconds=time.monotonic() - start, log_sha256=replay.digest(log.read_bytes()))
            commands.append(record)
            (output / 'commands.json').write_bytes(shards.canonical(commands))
        assert record['exit_code'] in allowed, record
        return record

    assert subprocess.check_output([args.dotnet, '--version'], cwd=fixture, env=env).decode().strip() == '10.0.404'
    execute('restore', [args.dotnet, 'restore', 'Tests/Tests.csproj', '--locked-mode'])
    execute('baseline', [args.dotnet, 'test', 'Tests/Tests.csproj', '-c', 'Release', '--no-restore',
                         '--logger', 'trx', '--results-directory', output / 'baseline'])
    baseline = replay.test_cases(output / 'baseline')
    binary = Path(provenance['binary'])
    base = {'stryker-config': {'project': 'Lib/Lib.csproj', 'test-projects': ['Tests/Tests.csproj'],
                             'configuration': 'Release', 'reporters': ['json'], 'concurrency': 1,
                             'mutate': shards.BASE_PATTERNS}}

    def mutate(label, config):
        path = fixture / (label + '.json')
        path.write_bytes(shards.canonical(config))
        record = execute(label, [args.dotnet, binary, '--config-file', path, '--output', output / label, '--log-to-file'], allowed=(0, 2))
        reports = list((output / label).rglob('mutation-report.json'))
        assert len(reports) == 1
        record.update(report=str(reports[0]), report_sha256=replay.digest(reports[0].read_bytes()), config=config)
        return record

    full = mutate('full', base)
    catalogue = shards.read_catalogue(full['report'], fixture)
    binding = {'head': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=repo).decode().strip(),
               'sdk_version': '10.0.404', 'sdk_sha256': replay.digest(args.dotnet.read_bytes()),
               'tool_sha256': replay.digest(binary.read_bytes()), 'upstream_commit': replay.COMMIT, 'platform': sys.platform}
    plan = shards.plan(catalogue, base, binding, target=8)
    assert plan['invocations'] <= 12, 'bounded pilot refuses excess invocations'
    (output / 'plan.json').write_bytes(shards.canonical(plan))
    records = []
    for region in plan['regions']:
        record = mutate('region-' + str(region['id']), shards.configuration(plan, region['id']))
        record.update(region_id=region['id'], plan_sha256=plan['plan_sha256'])
        records.append(record)
    result = shards.validate(plan, records, fixture, binding, full['report'], {'.'.join(c[0]) for c in baseline})
    expected_kills = sorted(r['identity'] for r in catalogue['rows'] if r['original']['status'] == 'Killed')
    assert result['attributed_killed'] == expected_kills
    execute('restored', [args.dotnet, 'test', 'Tests/Tests.csproj', '-c', 'Release', '--no-restore',
                         '--logger', 'trx', '--results-directory', output / 'restored'])
    assert replay.test_cases(output / 'restored') == baseline
    result.update(compile_errors=sum(r['original']['status'] == 'CompileError' for r in catalogue['rows']),
                  baseline_cases=len(baseline), equal_span_groups=sum(len(n['identities']) > 1 for n in flatten(shards.forest(catalogue['rows']))),
                  provenance_sha256=replay.digest(args.provenance.read_bytes()), environment_keys=sorted(env))
    assert result['compile_errors'] > 0, 'compile-error proof unresolved'
    assert result['equal_span_groups'] > 0, 'equal-span proof unresolved'
    (output / 'records.json').write_bytes(shards.canonical(records))
    (output / 'result.json').write_bytes(shards.canonical(result))
    (output / 'artifact-sha256.json').write_bytes(shards.canonical(replay.hashes(output)))
    print(json.dumps(result), flush=True)


def flatten(nodes):
    for node in nodes:
        yield node
        yield from flatten(node['children'])


if __name__ == '__main__':
    main()
