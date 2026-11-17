"""Fresh official-tool-only toy collection. No qualification or private build."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import replay
import replay_ci


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', type=Path, required=True)
    parser.add_argument('--test-case-filter')
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[4]
    output = Path(tempfile.mkdtemp(prefix='cm-initial-events-'))
    print(output, flush=True)
    fixture = output / 'fixture'
    shutil.copytree(repo / 'tooling/quality/storage/replay/fixture', fixture)
    tests = fixture / 'Tests/CounterTests.cs'
    tests.write_text(tests.read_text().replace('[Fact]\n    public void NumberReturnsOne()', '[Theory]\n    [InlineData(1)]\n    [InlineData(2)]\n    public void NumberReturnsOne(int row)').replace('Assert.Equal(1, counter.Number());', 'Assert.True(row > 0);\n        if (row == 2)\n        {\n            System.Threading.Thread.Sleep(1800);\n        }\n        Assert.Equal(1, counter.Number());'))
    (fixture / 'global.json').write_bytes((repo / 'global.json').read_bytes())
    (fixture / 'NuGet.Config').write_text('<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>')
    home = output / 'home'
    (home / '.nuget').mkdir(parents=True)
    (home / '.nuget/packages').symlink_to(Path.home() / '.nuget/packages', target_is_directory=True)
    env = replay_ci.child_environment({})
    env.update(HOME=str(home), PATH=str(args.dotnet.parent) + ':/usr/bin:/bin', DOTNET_ROOT=str(args.dotnet.parent), TMPDIR='/tmp/')
    deadline = time.monotonic() + 300
    records = []
    def execute(label, argv, allowed=(0,)):
        record = replay.command([str(x) for x in argv], fixture, output, label, env, min(100, deadline-time.monotonic()))
        records.append(record)
        replay.write(output / 'commands.json', records)
        if record['timeout'] or record['exit_code'] not in allowed:
            raise RuntimeError(label + ' failed; retained evidence at ' + str(output))
    execute('sdk', [args.dotnet, '--version'])
    execute('restore', [args.dotnet, 'restore', 'Tests/Tests.csproj', '--locked-mode'])
    execute('baseline', [args.dotnet, 'test', 'Tests/Tests.csproj', '-c', 'Release', '--no-restore', '--logger', 'trx', '--results-directory', output / 'baseline'])
    tool = Path('/Users/derek/.nuget/packages/dotnet-stryker/5.0.0/tools/net10.0/any/Stryker.CLI.dll')
    for label in ('passing', 'skipped'):
        if label == 'skipped':
            tests.write_text(tests.read_text().replace('[Fact]', '[Fact(Skip = "runtime negative fixture")]')
                             .replace('[Theory]', '[Theory(Skip = "runtime negative fixture")]'))
        config = fixture / (label + '.json')
        replay.write(config, {'stryker-config': {'project': 'Lib/Lib.csproj', 'test-projects': ['Tests/Tests.csproj'], 'configuration': 'Release', 'reporters': ['json'], 'concurrency': 1, 'mutate': ['**/Counter.cs'], 'break-on-initial-test-failure': True}})
        if args.test_case_filter:
            configured = json.loads(config.read_bytes())
            configured['stryker-config']['test-case-filter'] = args.test_case_filter
            replay.write(config, configured)
        replay.write(output / (label + '-source-sha256.json'), replay.hashes(fixture))
        execute(label, [args.dotnet, tool, '--config-file', config, '--output', output / label, '--diag', '--log-to-file', '--verbosity', 'trace'], (0, 2))
    replay.write(output / 'inventory.json', {'tool': str(tool), 'tool_sha256': replay.digest(tool.read_bytes()), 'sdk_sha256': replay.digest(args.dotnet.read_bytes()), 'sources': replay.hashes(fixture), 'runner_sha256': replay.digest(Path(__file__).read_bytes())})
    replay.write(output / 'artifact-sha256.json', {str(p.relative_to(output)): replay.digest(p.read_bytes()) for p in output.rglob('*') if p.is_file() and 'home' not in p.relative_to(output).parts})


if __name__ == '__main__':
    main()
