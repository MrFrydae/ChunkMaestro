"""Integration regression: a failing Release-only test must fail verify, never skip."""
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]


def main():
    with tempfile.TemporaryDirectory(prefix='chunkmaestro-release-test-') as temporary:
        root = Path(temporary)
        for name in ('global.json', 'Directory.Build.props', 'Directory.Build.targets',
                     'Directory.Packages.props', 'NuGet.Config', '.editorconfig'):
            shutil.copyfile(ROOT / name, root / name)
        tooling = root / 'tooling/build'
        tooling.mkdir(parents=True)
        shutil.copyfile(ROOT / 'tooling/build/build.py', tooling / 'build.py')
        shutil.copytree(ROOT / 'tooling/build/StyleCheck', tooling / 'StyleCheck',
                        ignore=shutil.ignore_patterns('bin', 'obj'))
        tests = root / 'tests/Release Only'
        tests.mkdir(parents=True)
        # Byte writes preserve repository LF on Windows too; text mode translates to CRLF.
        (tests / 'Regression.csproj').write_bytes('''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <IsTestProject Condition="'$(Configuration)' == 'Release'">true</IsTestProject>
    <IsTestProject Condition="'$(Configuration)' != 'Release'">false</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
  </ItemGroup>
</Project>
'''.encode('utf-8'))
        (tests / 'RegressionTests.cs').write_bytes('''namespace Regression;

public sealed class RegressionTests
{
    [Xunit.Fact]
    public void ReleaseOnlyFailureMustNotBeSkipped() =>
        Xunit.Assert.Fail("deliberate Release-only regression fixture");
}
'''.encode('utf-8'))
        command = [sys.executable, str(tooling / 'build.py')]
        environment = {**os.environ, 'MSBUILDDISABLENODEREUSE': '1'}
        subprocess.run([*command, 'sync'], cwd=root, check=True, env=environment)
        subprocess.run([*command, 'update-locks'], cwd=root, check=True,
                       env={**environment, 'CI': 'false'})
        result = subprocess.run([*command, 'verify'], cwd=root, capture_output=True, text=True, env=environment)
        evidence = ROOT / '.artifacts/build'
        evidence.mkdir(parents=True, exist_ok=True)
        (evidence / 'release-only-failure.log').write_text(result.stdout + result.stderr, encoding='utf-8')
        if result.returncode == 0 or 'deliberate Release-only regression fixture' not in result.stdout + result.stderr:
            raise RuntimeError('Release-only failing test was not executed and rejected; see fixture log.')
        print('PASS: verify executes the real Release-only xUnit suite and fails on its deliberate assertion.')


if __name__ == '__main__':
    main()
