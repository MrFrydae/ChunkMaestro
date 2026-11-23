"""Local stage orchestration and failure retention, not product execution."""
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import local
import os


class LocalTests(unittest.TestCase):
    def test_actions_refusal_precedes_every_collection_stage(self):
        with tempfile.TemporaryDirectory() as temporary, \
             patch.dict(os.environ, {'GITHUB_ACTIONS': 'true'}), \
             patch.object(local.platform, 'system', return_value='Windows'), \
             patch.object(local.subprocess, 'check_output', side_effect=['', 'a' * 40]) as git, \
             patch.object(local.qualify, 'run', return_value=1) as coverage, \
             patch.object(local.replay_ci, 'collect') as replay, \
             patch.object(local.volume, 'run') as volume, \
             patch.object(local.native, 'run') as native:
            output = Path(temporary) / 'must-not-exist'
            with self.assertRaisesRegex(ValueError, 'GitHub Actions'):
                local.collect(output)
            for stage in (coverage, replay, volume, native): stage.assert_not_called()
            git.assert_not_called()
            self.assertFalse(output.exists())

    def test_private_local_mode_refuses_actions_or_wrong_owner_before_tool_build(self):
        for hosted in (True, False):
            with tempfile.TemporaryDirectory() as temporary, \
                 patch.dict(os.environ, {'GITHUB_ACTIONS': 'true' if hosted else 'false'}), \
                 patch.object(local.replay_ci.subprocess, 'check_output',
                              side_effect=['a' * 40, json.dumps({'id': 123})]), \
                 patch.object(local.replay, 'build_private') as build:
                output = Path(temporary) / 'refused'
                self.assertEqual(1, local.replay_ci.collect(output, local=True))
                build.assert_not_called()
                result = json.loads((output / 'replay-collection.json').read_text())
                self.assertEqual('UNRESOLVED', result['status'])

    def test_actions_also_refuses_check_and_receipt_before_io_or_gate(self):
        with tempfile.TemporaryDirectory() as temporary, \
             patch.dict(os.environ, {'GITHUB_ACTIONS': 'true'}), \
             patch.object(local.qualify, 'run') as runtime, \
             patch.object(local.qualify, 'gate') as gate:
            output = Path(temporary) / 'must-not-exist'
            for invoke in (lambda: local.check(output),
                           lambda: local.receipt(output / 'windows', output / 'macos', output)):
                with self.assertRaisesRegex(ValueError, 'GitHub Actions'): invoke()
            runtime.assert_not_called(); gate.assert_not_called()
            self.assertFalse(output.exists())

    def test_unsupported_or_dirty_checkout_never_starts_collection(self):
        for system, dirty in (('Linux', ''), ('Windows', ' M tracked.cs')):
            with patch.object(local.platform, 'system', return_value=system), \
                 patch.object(local.subprocess, 'check_output', return_value=dirty), \
                 patch.object(local.qualify, 'run') as execute:
                with self.assertRaises(ValueError): local.collect(Path('/unused'))
                execute.assert_not_called()

    def test_failure_retains_stage_and_never_retries_or_starts_volume(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = (Path(temporary) / 'new').resolve()
            with patch.dict(os.environ, {'GITHUB_ACTIONS': 'false'}), \
                 patch.object(local.platform, 'system', return_value='Windows'), \
                 patch.object(local.subprocess, 'check_output', side_effect=['', 'a' * 40]), \
                 patch.object(local.qualify, 'run', return_value=0) as coverage, \
                 patch.object(local.replay_ci, 'collect', return_value=1) as replay, \
                 patch.object(local.volume, 'run') as volume:
                self.assertEqual(1, local.collect(output))
                coverage.assert_called_once(); replay.assert_called_once_with(output, local=True)
                volume.assert_not_called()
            result = json.loads((output / 'local-session.json').read_text())
            self.assertEqual('UNRESOLVED', result['status'])
            self.assertEqual('persistence-replay', result['phase'])

    def test_failed_gate_cannot_produce_pass_receipt(self):
        with tempfile.TemporaryDirectory() as temporary, \
             patch.dict(os.environ, {'GITHUB_ACTIONS': 'false'}), \
             patch.object(local.qualify, 'gate', return_value=1):
            output = Path(temporary) / 'receipt'
            with self.assertRaises(ValueError):
                local.receipt(Path(temporary) / 'windows', Path(temporary) / 'macos', output)
            self.assertFalse((output / 'attestation.md').exists())
