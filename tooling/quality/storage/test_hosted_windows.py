"""Synthetic dispatch/deadline regressions; never Windows qualification."""
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

import hosted_windows as hosted
import local
import replay_ci


class HostedWindowsTests(unittest.TestCase):
    def test_shared_workspace_output_is_rejected_before_any_child_or_creation(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            env = {'LOCALAPPDATA': str(root / 'profile'), 'GITHUB_RUN_ID': '123'}
            with patch.dict(os.environ, env, clear=True), \
                 patch.object(hosted, 'context', return_value={'run_id': '123'}), \
                 patch.object(hosted.replay, 'command') as execute:
                expected = hosted.output_directory()
                self.assertEqual(root / 'profile/ChunkMaestroQualification/storage-windows-123', expected)
                with self.assertRaises(ValueError):
                    hosted.collect(root / 'shared-runner-temp', 'a' * 40, hosted.AUTHORIZATION)
                execute.assert_not_called()
                self.assertFalse((root / 'shared-runner-temp').exists())

    def environment(self, root):
        head = 'a' * 40
        event = root / 'event.json'
        event.write_text(json.dumps({'sender': {'id': replay_ci.OWNER_ID},
            'inputs': {'head': head, 'authorization': hosted.AUTHORIZATION}}))
        return {'GITHUB_EVENT_PATH': str(event), 'GITHUB_ACTIONS': 'true',
            'GITHUB_REPOSITORY': replay_ci.REPOSITORY, 'GITHUB_ACTOR_ID': str(replay_ci.OWNER_ID),
            'GITHUB_EVENT_NAME': 'workflow_dispatch', 'GITHUB_SHA': head, 'GITHUB_REF': 'refs/heads/test',
            'GITHUB_WORKFLOW_REF': replay_ci.REPOSITORY + '/' + hosted.WORKFLOW + '@refs/heads/test',
            'GITHUB_WORKFLOW_SHA': head, 'GITHUB_RUN_ID': '123', 'GITHUB_RUN_ATTEMPT': '1',
            'RUNNER_ENVIRONMENT': 'github-hosted'}

    def test_trust_rejects_wrong_owner_head_platform_host_event_rerun_or_authorization(self):
        with tempfile.TemporaryDirectory() as temporary:
            env = self.environment(Path(temporary))
            for key, value in [(None, None), ('GITHUB_ACTOR_ID', '1'), ('GITHUB_SHA', 'b' * 40),
                ('GITHUB_EVENT_NAME', 'pull_request'), ('GITHUB_ACTIONS', 'false'),
                ('RUNNER_ENVIRONMENT', 'self-hosted'), ('GITHUB_RUN_ATTEMPT', '2'),
                ('GITHUB_WORKFLOW_SHA', 'b' * 40), ('GITHUB_WORKFLOW_REF', 'another-workflow')]:
                changed = dict(env)
                if key: changed[key] = value
                with patch.dict(os.environ, changed, clear=True), \
                     patch.object(hosted.platform, 'system', return_value='Windows'), \
                     patch.object(hosted.subprocess, 'check_output', side_effect=['', 'a' * 40]):
                    if key:
                        with self.assertRaises(ValueError): hosted.context('a' * 40, hosted.AUTHORIZATION)
                    else:
                        self.assertEqual('123', hosted.context('a' * 40, hosted.AUTHORIZATION)['run_id'])
                        with self.assertRaises(ValueError): hosted.context('a' * 40, 'wrong')
            with patch.dict(os.environ, env, clear=True), patch.object(hosted.platform, 'system', return_value='Darwin'):
                with self.assertRaises(ValueError): hosted.context('a' * 40, hosted.AUTHORIZATION)

    def test_failure_preserves_partial_evidence_and_never_retries_or_starts_volume(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / 'new'
            records = [{'exit_code': 0, 'timeout': False}, {'exit_code': 1, 'timeout': True}]
            def command(args, cwd, directory, label, env, timeout):
                self.assertEqual('true', env['GITHUB_ACTIONS'])
                self.assertTrue(Path(env['TMP']).is_relative_to(output.resolve()))
                (Path(env['TMP']) / 'partial.log').write_text('retained partial raw output')
                return records.pop(0)
            with patch.object(hosted, 'context', return_value={'run_id': '123'}), \
                 patch.object(hosted, 'output_directory', return_value=output.resolve()), \
                 patch.object(hosted.replay, 'command', side_effect=command) as execute, \
                 patch.dict(os.environ, {'GITHUB_ACTIONS': 'true'}):
                self.assertEqual(1, hosted.collect(output, 'a' * 40, hosted.AUTHORIZATION))
                self.assertEqual(2, execute.call_count)
                with self.assertRaises(FileExistsError): hosted.collect(output, 'a' * 40, hosted.AUTHORIZATION)
            session = json.loads((output / 'hosted-session.json').read_bytes())
            self.assertEqual('UNRESOLVED', session['status'])
            self.assertEqual('NOT_EVALUATED', session['qualification'])
            self.assertTrue((output / 'scratch/partial.log').exists())

    def test_success_is_collection_only_and_budget_is_fixed(self):
        self.assertEqual(2880, hosted.TOTAL_SECONDS)
        self.assertEqual((('coverage-and-small-modules', 2880), ('persistence-replay', 2880),
                          ('full-volume', 180)), hosted.STAGES)
        with tempfile.TemporaryDirectory() as temporary, \
             patch.object(hosted, 'output_directory', return_value=(Path(temporary) / 'new').resolve()), \
             patch.object(hosted, 'context', return_value={}), \
             patch.object(hosted.replay, 'command', return_value={'exit_code': 0, 'timeout': False}):
            output = Path(temporary) / 'new'
            self.assertEqual(0, hosted.collect(output, 'a' * 40, hosted.AUTHORIZATION))
            session = json.loads((output / 'hosted-session.json').read_bytes())
            self.assertEqual('COLLECTED', session['status'])
            self.assertEqual('NOT_EVALUATED', session['qualification'])

    def test_slow_initial_stage_borrows_unused_budget_without_extending_total(self):
        # Synthetic elapsed times, not Windows execution: an initial stage over
        # eight minutes must not be stopped while collection time remains.
        with tempfile.TemporaryDirectory() as temporary, \
             patch.object(hosted, 'output_directory', return_value=(Path(temporary) / 'new').resolve()), \
             patch.object(hosted, 'context', return_value={}), \
             patch.object(hosted.time, 'monotonic', side_effect=[0, 0, 600, 2700, 2760]), \
             patch.object(hosted.replay, 'command', return_value={'exit_code': 0, 'timeout': False}) as command:
            output = Path(temporary) / 'new'
            self.assertEqual(0, hosted.collect(output, 'a' * 40, hosted.AUTHORIZATION))
            self.assertEqual([2880, 2280, 180], [call.args[-1] for call in command.call_args_list])
            session = json.loads((output / 'hosted-session.json').read_bytes())
            self.assertEqual(2760, session['seconds'])
            self.assertEqual([2880, 2280, 180], [stage['deadline_seconds'] for stage in session['stages']])
            self.assertEqual('COLLECTED', session['status'])
            self.assertEqual('NOT_EVALUATED', session['qualification'])

    def test_unresolved_session_refuses_receipt_before_raw_gate(self):
        with tempfile.TemporaryDirectory() as temporary, \
             patch.dict(os.environ, {'GITHUB_ACTIONS': 'false'}), \
             patch.object(local.subprocess, 'check_output', return_value='a' * 40), \
             patch.object(local.qualify, 'gate') as gate:
            sys.path.insert(0, str(hosted.qualify.ROOT / 'tooling/build'))
            import storage_attestation
            session = Path(temporary) / 'session.json'
            session.write_text(json.dumps({'trust': {}, 'status': 'UNRESOLVED'}))
            with patch.object(storage_attestation, 'hosted_windows'), self.assertRaises(ValueError):
                local.receipt(Path(temporary) / 'win', Path(temporary) / 'mac',
                              Path(temporary) / 'receipt', session)
            gate.assert_not_called()

    def test_receipt_accepts_both_local_or_mixed_only_after_gate_and_matching_run(self):
        sys.path.insert(0, str(hosted.qualify.ROOT / 'tooling/build'))
        import storage_attestation
        head = 'a' * 40
        trust = {'repository': replay_ci.REPOSITORY, 'actor_id': replay_ci.OWNER_ID,
                 'head': head, 'event': 'local'}
        hosted_trust = dict(trust, event='workflow_dispatch', execution='github-hosted',
            authorization=hosted.AUTHORIZATION, run_id='123', run_attempt=1, workflow=hosted.WORKFLOW,
            workflow_sha=head, run_url='https://github.com/' + replay_ci.REPOSITORY + '/actions/runs/123')
        metrics = {module: {'line': {'covered': 90, 'total': 100, 'percent': 90},
            'branch': {'covered': 85, 'total': 100, 'percent': 85},
            'mutation': {'killed': 80, 'applicable': 100, 'percent': 80}}
            for module in ('Domain', 'Application', 'Persistence')}
        for mixed, wrong_run, fail_gate in ((False, False, False), (True, False, False),
                                            (True, True, False), (True, False, True)):
            with tempfile.TemporaryDirectory() as temporary, \
                 patch.dict(os.environ, {'GITHUB_ACTIONS': 'false'}), \
                 patch.object(local.subprocess, 'check_output', return_value=head):
                root = Path(temporary)
                win, mac = root / 'win', root / 'mac'
                for folder, system in ((win, 'Windows'), (mac, 'Darwin')):
                    folder.mkdir()
                    (folder / 'manifest.json').write_text(json.dumps({'platform': system}))
                    collection = {'trust': dict(trust)}
                    if mixed and system == 'Windows':
                        collection.update(trust={k: hosted_trust[k] for k in trust}, hosted_context=dict(hosted_trust))
                        if wrong_run: collection['hosted_context']['run_id'] = '456'
                    (folder / 'replay-collection.json').write_text(json.dumps(collection))
                session = root / 'hosted-session.json'
                session.write_text(json.dumps({'schema_version': 1, 'head': head, 'platform': 'Windows',
                    'trust': hosted_trust, 'status': 'COLLECTED', 'qualification': 'NOT_EVALUATED',
                    'stages': [{'stage': name, 'timeout': False, 'exit_code': 0} for name, _ in hosted.STAGES]}))
                def gate(folders, output):
                    output.write_text(json.dumps({'metrics': metrics}))
                    return int(fail_gate)
                output = root / 'receipt'
                with patch.object(local.qualify, 'gate', side_effect=gate) as check:
                    if wrong_run or fail_gate:
                        with self.assertRaises(ValueError): local.receipt(win, mac, output, session if mixed else None)
                        self.assertFalse((output / 'attestation.md').exists())
                    else:
                        self.assertEqual(0, local.receipt(win, mac, output, session if mixed else None))
                        report = json.loads((output / 'receipt.json').read_bytes())
                        storage_attestation.validate(report, head)
                        self.assertEqual('local-macos-hosted-windows' if mixed else 'local', report['collection'])
                    check.assert_called_once()

    def test_total_deadline_refuses_next_stage(self):
        with tempfile.TemporaryDirectory() as temporary, \
             patch.object(hosted, 'output_directory', return_value=(Path(temporary) / 'new').resolve()), \
             patch.object(hosted, 'context', return_value={}), \
             patch.object(hosted.time, 'monotonic', side_effect=[0, hosted.TOTAL_SECONDS + 1, hosted.TOTAL_SECONDS + 1]), \
             patch.object(hosted.replay, 'command') as command:
            output = Path(temporary) / 'new'
            self.assertEqual(1, hosted.collect(output, 'a' * 40, hosted.AUTHORIZATION))
            command.assert_not_called()
            self.assertEqual('UNRESOLVED', json.loads((output / 'hosted-session.json').read_bytes())['status'])
