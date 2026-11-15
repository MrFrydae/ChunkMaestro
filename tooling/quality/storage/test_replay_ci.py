"""Disposable adapter safety fixtures, never platform qualification evidence."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import replay
import replay_ci as ci
import qualify


class AdapterTests(unittest.TestCase):
    def test_child_process_environment_is_an_explicit_allowlist(self):
        source = dict(os.environ, GITHUB_TOKEN='sentinel', AWS_SECRET_ACCESS_KEY='sentinel',
                      SSH_AUTH_SOCK='sentinel', NUGET_AUTH_TOKEN='sentinel',
                      NuGetPackageSourceCredentials_private='sentinel',
                      DOTNET_STARTUP_HOOKS='sentinel', PYTHONPATH='sentinel',
                      DOTNET_CLI_TELEMETRY_OPTOUT='0')
        env = ci.child_environment(source)
        result = subprocess.check_output([sys.executable, '-c',
            'import os,json;print(json.dumps(sorted(os.environ)))'], env=env, text=True)
        keys = json.loads(result)
        self.assertLessEqual(set(keys), set(env) | {'LC_CTYPE', '__CF_USER_TEXT_ENCODING'})  # Runtime-added locale metadata.
        self.assertEqual(env['DOTNET_CLI_TELEMETRY_OPTOUT'], '1')
        with ci.isolated_environment(env):
            self.assertEqual(replay.environment(Path(sys.executable))['DOTNET_CLI_TELEMETRY_OPTOUT'], '1')
            self.assertNotIn('GITHUB_TOKEN', os.environ)

    def context(self):
        env = {'GITHUB_REPOSITORY': ci.REPOSITORY, 'GITHUB_ACTOR_ID': str(ci.OWNER_ID),
               'GITHUB_EVENT_NAME': 'pull_request'}
        event = {'sender': {'id': ci.OWNER_ID}, 'pull_request': {'user': {'id': ci.OWNER_ID},
                 'head': {'sha': 'abc', 'repo': {'full_name': ci.REPOSITORY}},
                 'base': {'repo': {'full_name': ci.REPOSITORY}}}}
        return env, event

    def test_trusted_context_rejects_fork_other_author_stale_head_and_privileged_events(self):
        env, event = self.context()
        self.assertEqual(ci.trusted_context(env, event, 'abc')['head'], 'abc')
        for changed in (dict(env, GITHUB_ACTOR_ID='1'), dict(env, GITHUB_EVENT_NAME='pull_request_target')):
            with self.assertRaises(ValueError):
                ci.trusted_context(changed, event, 'abc')
        with self.assertRaises(ValueError):
            ci.trusted_context(env, event, 'def')
        event['pull_request']['user']['id'] = 1
        with self.assertRaises(ValueError):
            ci.trusted_context(env, event, 'abc')
        event['pull_request']['user']['id'] = ci.OWNER_ID
        event['pull_request']['head']['repo']['full_name'] = 'fork/ChunkMaestro'
        with self.assertRaises(ValueError):
            ci.trusted_context(env, event, 'abc')
        env.update(GITHUB_EVENT_NAME='workflow_dispatch', GITHUB_SHA='abc')
        self.assertEqual(ci.trusted_context(env, event, 'abc')['event'], 'workflow_dispatch')

    def report(self, root, status):
        source = root / 'src/ChunkMaestro.Persistence/Example.cs'
        source.parent.mkdir(parents=True, exist_ok=True)
        source.write_bytes(b'false\n')
        directory = root / 'report'
        directory.mkdir(exist_ok=True)
        mutant = {'id': '1', 'status': status, 'mutatorName': 'Boolean', 'replacement': 'true',
                  'location': {'start': {'line': 1, 'column': 1}, 'end': {'line': 1, 'column': 6}}}
        (directory / 'mutation-report.json').write_text(json.dumps({'files': {
            str(source): {'source': 'false\n', 'mutants': [mutant]}}}))
        return directory

    def test_threshold_code_requires_complete_report_and_matching_score(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder).resolve()
            directory = self.report(root, 'Survived')
            record = {'exit_code': 2, 'timeout': False}
            with self.assertRaises(ValueError):
                ci.complete_report(directory, record, root, module='Application')
            self.assertEqual(ci.complete_report(directory, record, root)['status'], 'collected-threshold-fail')
            for bad in ({'exit_code': 1, 'timeout': False}, {'exit_code': 0, 'timeout': False},
                        {'exit_code': 2, 'timeout': True}):
                with self.assertRaises(ValueError):
                    ci.complete_report(directory, bad, root)
            for status in ('Pending', 'Ignored'):
                self.report(root, status)
                with self.assertRaises(ValueError):
                    ci.complete_report(directory, record, root)
            self.report(root, 'Killed')
            self.assertEqual(ci.complete_report(directory, {'exit_code': 0, 'timeout': False}, root)['status'], 'collected')
            with self.assertRaises(ValueError):
                ci.complete_report(directory, record, root)

            duplicate = directory / 'duplicate'
            duplicate.mkdir()
            (duplicate / 'mutation-report.json').write_bytes((directory / 'mutation-report.json').read_bytes())
            with self.assertRaises(ValueError):
                ci.complete_report(directory, {'exit_code': 0, 'timeout': False}, root)
            (duplicate / 'mutation-report.json').unlink()
            (directory / 'mutation-report.json').unlink()
            with self.assertRaises(ValueError):
                ci.complete_report(directory, record, root)

    def test_timeout_matches_upstream_exit_but_is_not_a_killed_observation(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder).resolve()
            directory = self.report(root, 'Timeout')
            self.assertEqual(ci.complete_report(directory, {'exit_code': 0, 'timeout': False}, root)['status'], 'collected')
            self.assertEqual(json.loads((directory / 'mutation-report.json').read_bytes())['files'][str(root / 'src/ChunkMaestro.Persistence/Example.cs')]['mutants'][0]['status'], 'Timeout')
            with self.assertRaises(ValueError):
                ci.complete_report(directory, {'exit_code': 2, 'timeout': False}, root)

    def test_empty_application_requires_full_source_inventory_and_successful_exit(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder).resolve()
            source = root / 'src/ChunkMaestro.Application/IFleetInventory.cs'
            source.parent.mkdir(parents=True)
            source.write_bytes(b'interface I {}\n')
            directory = root / 'report'
            directory.mkdir()
            report = directory / 'mutation-report.json'
            report.write_text(json.dumps({'files': {str(source): {'source': source.read_text(), 'mutants': []}}}))
            self.assertEqual(ci.complete_report(directory, {'exit_code': 0, 'timeout': False}, root, module='Application')['status'], 'collected-no-applicable-mutants')
            for module, code in (('Persistence', 0), ('Application', 2)):
                with self.assertRaises(ValueError):
                    ci.complete_report(directory, {'exit_code': code, 'timeout': False}, root, module=module)
            report.write_text('{"files": {}}')
            with self.assertRaises(ValueError):
                ci.complete_report(directory, {'exit_code': 0, 'timeout': False}, root, module='Application')

    def test_relocation_preserves_bytes_and_absolute_provenance(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder).resolve()
            source = root / 'original with spaces'
            source.mkdir()
            (source / 'bin').mkdir()
            (source / 'bin/tool.dll').write_bytes(b'\x00\xff')
            (source / 'commands.jsonl').write_bytes(b'{"cwd":"C:\\\\runner\\\\work"}\r\n')
            before = ci.inventory(source)
            self.assertEqual(ci.preserve(source, root / 'artifact'), before)
            self.assertEqual(ci.inventory(root / 'artifact'), before)

    def test_untrusted_context_refuses_before_build_and_retains_failure(self):
        with tempfile.TemporaryDirectory() as folder, patch.dict(os.environ, {'GITHUB_EVENT_PATH': str(Path(folder) / 'event')}, clear=True):
            (Path(folder) / 'event').write_text('{}')
            with patch.object(replay, 'build_private') as build:
                self.assertEqual(ci.collect(Path(folder) / 'out'), 1)
                build.assert_not_called()
            self.assertEqual(json.loads((Path(folder) / 'out/replay-collection.json').read_text())['status'], 'UNRESOLVED')

    def test_build_failure_preserves_job_owned_artifacts_without_rewriting(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder).resolve()
            env, event = self.context()
            env['GITHUB_EVENT_PATH'] = str(root / 'event')
            (root / 'event').write_text(json.dumps(event))
            dotnet = Path(sys.executable).resolve()
            env['CHUNKMAESTRO_DOTNET'] = str(dotnet)
            def git_or_sdk(args, **kwargs):
                if args[:2] == ['git', 'rev-parse']:
                    return 'abc'
                if args[0] == 'git':
                    return ''
                return json.loads((ci.ROOT / 'global.json').read_bytes())['sdk']['version']
            def fail_build(output, executable):
                output.mkdir()
                (output / 'build.log').write_bytes(b'actual fixture failure\r\n')
                raise ValueError('fixture build failure')
            with patch.dict(os.environ, env), patch.object(ci.subprocess, 'check_output', side_effect=git_or_sdk), \
                    patch.object(ci.platform, 'system', return_value='Darwin'), \
                    patch.object(replay, 'build_private', side_effect=fail_build):
                self.assertEqual(ci.collect(root / 'out'), 1)
            result = json.loads((root / 'out/replay-collection.json').read_bytes())
            self.assertEqual(result['status'], 'UNRESOLVED')
            self.assertEqual((root / 'out/replay-private/build.log').read_bytes(), b'actual fixture failure\r\n')
            original = Path(result['original_temporary_root'])
            self.assertEqual(ci.inventory(original / 'replay-private'), result['relocated']['replay-private'])
            import shutil
            shutil.rmtree(original)  # only this test's disposable fixture

    def test_actual_host_path_alias_and_source_mismatch(self):
        with tempfile.TemporaryDirectory(prefix='replay-path with spaces-') as folder:
            root = Path(folder).resolve()
            directory = self.report(root, 'Survived')
            record = {'exit_code': 2, 'timeout': False}
            ci.complete_report(directory, record, Path(folder))
            (root / 'src/ChunkMaestro.Persistence/Example.cs').write_bytes(b'true\n')
            with self.assertRaises(ValueError):
                ci.complete_report(directory, record, root)

    def test_qualify_failure_preserves_actual_command_log_and_manifest(self):
        with tempfile.TemporaryDirectory() as folder, patch.object(qualify.platform, 'system', return_value='Darwin'):
            output = Path(folder) / 'evidence'
            original = replay.command
            def fail(args, cwd, directory, label, env, timeout):
                return original([sys.executable, '-c', 'print("failure evidence");raise SystemExit(7)'],
                                cwd, directory, label, env, timeout)
            with patch.object(replay, 'command', side_effect=fail):
                self.assertEqual(qualify.run(output), 1)
            manifest = json.loads((output / 'manifest.json').read_text())
            self.assertEqual(manifest['status'], 'UNRESOLVED')
            self.assertEqual(manifest['commands'][0]['exit_code'], 7)
            self.assertIn('failure evidence', Path(manifest['commands'][0]['log']).read_text())


if __name__ == '__main__':
    unittest.main()
