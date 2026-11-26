"""Run the owner workflow through its real supervisor with inert file effects.

This checks artifact routing and failure propagation, never Linux/native behavior.
"""
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
STEP = '      - name: Private accepted-connection owner plain sanitizer and kernel cases'
RUNNER = 'tests/ChunkMaestro.InspectorNativeTests/run_agent_connection_owner.sh'


def workflow_step():
    lines = (ROOT / '.github/workflows/ci.yml').read_text().splitlines()
    start = lines.index(STEP)
    run = lines.index('        run: |', start) + 1
    script = []
    for line in lines[run:]:
        if not line.startswith('          '):
            break
        script.append(line[10:])
    artifact_lines = [line.strip() for line in lines[start:run]
                      if line.strip().startswith('CM_N1_ARTIFACTS: ')]
    if len(artifact_lines) != 1 or not script:
        raise AssertionError('Missing fixed owner artifact path or script')
    upload = lines.index('      - name: Retain native diagnostics including failures')
    if '        if: always()' not in lines[upload:upload + 5]:
        raise AssertionError('Native failure evidence must always upload')
    if '            .artifacts/ci/inspector-metadata' not in lines[upload:upload + 15]:
        raise AssertionError('Missing uploaded inspector directory')
    return '\n'.join(script) + '\n', artifact_lines[0].split(': ', 1)[1]


@unittest.skipUnless(os.name == 'posix' and shutil.which('bash'), 'POSIX CI shell contract')
class OwnerArtifactTests(unittest.TestCase):
    def run_step(self, expected_exit):
        script, artifact_setting = workflow_step()
        runner_lines = (ROOT / RUNNER).read_text().splitlines()
        start = next(index for index, line in enumerate(runner_lines)
                     if line.startswith('root=$(CDPATH='))
        stop = runner_lines.index('set -x', start)
        # Only actual path-selection, mkdir/mktemp and redirection lines are used.
        # No platform guard, compiler, socket, kernel or native code is executed.
        routing = '\n'.join(runner_lines[start:stop]) + '\n'
        command = 'python tooling/ci/command.py'
        self.assertEqual(1, script.count(command))
        script = script.replace(command, shlex.quote(sys.executable) + ' tooling/ci/command.py')
        with tempfile.TemporaryDirectory(prefix='owner-artifacts-') as temporary:
            workspace = Path(temporary) / 'workspace with spaces'
            supervisor = workspace / 'tooling/ci/command.py'
            supervisor.parent.mkdir(parents=True)
            shutil.copyfile(ROOT / 'tooling/ci/command.py', supervisor)
            runner = workspace / RUNNER
            runner.parent.mkdir(parents=True)
            runner.write_text('#!/bin/sh\nset -eu\n' + routing + '''
test -z "${GH_TOKEN-}"
test -z "${CM_N1_UNRELATED-}"
test -n "${CM_N1_ARTIFACTS-}"
printf 'inert route evidence only; NOT native proof\n'
for name in deterministic-plain kernel-plain deterministic-sanitized kernel-sanitized; do
    printf 'synthetic case log\n' > "$build/$name.log"
done
mkdir "$build/lifetime" "$build/coverage"
printf 'synthetic control log\n' > "$build/lifetime/result.log"
printf 'synthetic coverage log\n' > "$build/coverage/coverage.log"
''' + f'exit {expected_exit}\n')
            environment = dict(os.environ)
            destination = artifact_setting.replace('${{ github.workspace }}', str(workspace))
            environment.update(CM_N1_ARTIFACTS=destination,
                               GH_TOKEN='synthetic-credential-marker',
                               CM_N1_UNRELATED='synthetic-unrelated-marker')
            result = subprocess.run(['/bin/bash'], input=script, text=True, cwd=workspace,
                                    env=environment, capture_output=True, timeout=15, check=False)
            # The supervisor creates this empty scratch, independent of logs.
            # A nonzero command intentionally retains it; remove only this test's
            # explicitly reported empty directory after the child was reaped.
            for line in result.stdout.splitlines():
                if line.startswith('Command scratch retained: '):
                    Path(line.split(': ', 1)[1]).rmdir()
            self.assertEqual(expected_exit, result.returncode, result.stdout + result.stderr)
            output = workspace / '.artifacts/ci/inspector-metadata/agent-connection-owner'
            self.assertEqual(output, Path(destination))
            self.assertTrue((output / 'supervisor.log').is_file())
            runs = list(output.glob('run-*'))
            self.assertEqual(1, len(runs), result.stdout + result.stderr)
            expected = {'raw.log', 'deterministic-plain.log', 'kernel-plain.log',
                        'deterministic-sanitized.log', 'kernel-sanitized.log',
                        'lifetime/result.log', 'coverage/coverage.log'}
            actual = {path.relative_to(runs[0]).as_posix()
                      for path in runs[0].rglob('*') if path.is_file()}
            self.assertEqual(expected, actual)
            self.assertIn('NOT native proof', (runs[0] / 'raw.log').read_text())
            self.assertFalse((workspace / '.artifacts/ci/agent-connection-owner').exists())

    def test_success_retains_detailed_logs_under_uploaded_root(self):
        self.run_step(0)

    def test_failure_preserves_exit_and_detailed_logs_under_uploaded_root(self):
        self.run_step(7)


if __name__ == '__main__':
    unittest.main()
