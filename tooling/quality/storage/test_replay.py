"""Synthetic refusal/parser contracts only: these are NOT runtime acceptance."""
import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest
import contextlib
import io
import subprocess
from unittest.mock import MagicMock
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).parent))
import replay


class ReplayContracts(unittest.TestCase):
    def test_running_command_emits_heartbeat_without_extending_deadline(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            proc = MagicMock()
            waits = []
            raw = b'\x1b[31m.S.T\x1b[0m\r'
            def wait(timeout):
                waits.append(timeout)
                if len(waits) == 1:
                    next(root.glob('heartbeat-*.log')).write_bytes(raw)
                    raise subprocess.TimeoutExpired('fixture', 30)
                return 0
            proc.wait.side_effect = wait
            output = io.StringIO()
            with patch.object(replay.subprocess, 'Popen', return_value=proc), \
                    patch.object(replay.time, 'monotonic', side_effect=[0, 0, 30, 30, 30, 31]), \
                    contextlib.redirect_stdout(output):
                result = replay.command(['fixture'], root, root, 'heartbeat', {}, 60)
            self.assertFalse(result['timeout'])
            self.assertIn('WAIT heartbeat', output.getvalue())
            self.assertIn('END heartbeat', output.getvalue())
            self.assertIn('OUTPUT .S.T', output.getvalue())
            self.assertNotIn('\x1b', output.getvalue())
            self.assertEqual(next(root.glob('heartbeat-*.log')).read_bytes(), raw)
            self.assertEqual([call.kwargs['timeout'] for call in proc.wait.call_args_list], [30, 30])

    def test_windows_failed_termination_never_enters_unbounded_wait(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            proc = MagicMock()
            proc.wait.side_effect = subprocess.TimeoutExpired('fixture', 0)
            proc.poll.return_value = None
            with patch.object(replay.subprocess, 'Popen', return_value=proc), \
                    patch.object(replay.subprocess, 'run', side_effect=subprocess.TimeoutExpired('taskkill', 15)) as kill, \
                    patch.object(replay.os, 'name', 'nt'), contextlib.redirect_stdout(io.StringIO()):
                result = replay.command(['fixture'], root, root, 'termination', {}, 0)
            self.assertTrue(result['timeout'])
            self.assertIsNone(result['exit_code'])
            self.assertIn('termination_error', result)
            self.assertEqual(kill.call_args.kwargs['timeout'], 15)
            proc.kill.assert_called_once()
            self.assertTrue(all('timeout' in call.kwargs for call in proc.wait.call_args_list))
            with self.assertRaises(ValueError):
                replay.require(result)

    def test_utf16_end_exclusive_crlf_and_astral(self):
        source = 'a😀\r\n{\r\n}\n'
        self.assertEqual(5, replay.offset(source, {'line': 2, 'column': 1}))
        self.assertEqual(9, replay.offset(source, {'line': 3, 'column': 2}))
        with self.assertRaises(UnicodeDecodeError):
            replay.offset(source, {'line': 1, 'column': 3})
        with self.assertRaises(ValueError):
            replay.offset(source, {'line': 4, 'column': 2})

    def test_patch_is_exact_one_registration_only(self):
        source = b'prefix\n' + replay.REMOVE + b'suffix\n'
        with patch.object(replay, 'FACTORY_SHA', replay.digest(source)):
            self.assertEqual(b'prefix\nsuffix\n', replay.patch(source))
            with self.assertRaises(ValueError):
                replay.patch(source + b'changed')
        with self.assertRaises(ValueError):
            replay.patch(source)

    def test_catalogue_preserves_duplicates_and_raw_statuses(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder).resolve()
            (root / 'File.cs').write_bytes(b'{}\r\n')
            mutant = {'id': '0', 'status': 'Ignored', 'location': {
                'start': {'line': 1, 'column': 1}, 'end': {'line': 1, 'column': 3}},
                'mutatorName': 'Block removal mutation', 'replacement': '{}'}
            data = {'files': {'File.cs': {'source': '{}\r\n', 'mutants': [mutant, mutant]}}}
            report = root / 'report.json'
            replay.write(report, data)
            rows = replay.catalogue(report, root)
            self.assertEqual(2, len(rows))
            self.assertEqual([0, 2], rows[0]['span_utf16'])
            self.assertEqual('Ignored', rows[1]['original']['status'])
            self.assertEqual(['UNRESOLVED_IDENTITY'] * 2,
                             [r['supplemental_status'] for r in replay.supplement(rows, rows)])
            (root / 'File.cs').write_bytes(b'{}\n')
            with self.assertRaises(ValueError):
                replay.catalogue(report, root)

    def test_supplement_never_credits_timeout_compileerror_or_unattributed_kill(self):
        original = [{'identity': 'a', 'original': {'status': 'Ignored'}, 'report_sha256': 'original'}]
        for status in ['Timeout', 'CompileError', 'Ignored', 'Killed']:
            patched = copy.deepcopy(original)
            patched[0]['original']['status'] = status
            rows = list(replay.supplement(original, patched))
            self.assertTrue(rows[0]['supplemental_status'].startswith('UNRESOLVED_'))
            self.assertEqual('Ignored', rows[0]['original']['status'])
        patched[0]['original']['killedBy'] = ['test-id']
        self.assertEqual('Killed', list(replay.supplement(original, patched))[0]['supplemental_status'])

    def test_missing_or_skipped_baseline_refused(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            with self.assertRaises(ValueError):
                replay.test_cases(root)
            (root / 'result.trx').write_text('<TestRun><Counters total="0" executed="0" passed="0"/></TestRun>')
            with self.assertRaises(ValueError):
                replay.test_cases(root)

    def test_timeout_is_recorded_and_not_success(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            result = replay.command([sys.executable, '-c', 'import time; time.sleep(30)'],
                                    root, root, 'synthetic-timeout', dict(replay.os.environ), 0.05)
            self.assertTrue(result['timeout'])
            with self.assertRaises(ValueError):
                replay.require(result)
            self.assertEqual(1, len((root / 'commands.jsonl').read_text().splitlines()))

    def test_report_path_traversal_refused(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder).resolve()
            report = root / 'report.json'
            replay.write(report, {'files': {'../outside.cs': {'source': '{}', 'mutants': []}}})
            with self.assertRaises(ValueError):
                replay.catalogue(report, root)


if __name__ == '__main__':
    unittest.main()
