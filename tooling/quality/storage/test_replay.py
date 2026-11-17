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
from types import SimpleNamespace
from unittest.mock import MagicMock
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).parent))
import replay


class ReplayContracts(unittest.TestCase):
    def simulated_command(self, label='official', updates=None, exit_at=None, deadline=900, args=None):
        """Synthetic clock/process; never a runtime or performance acceptance claim."""
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        clock = [0]
        proc = MagicMock()
        running = [True]
        fired = set()
        def wait(timeout):
            self.assertGreaterEqual(timeout, 0)
            if not running[0]:
                return -9
            clock[0] += timeout
            for stamp, source in (updates or {}).items():
                if stamp <= clock[0] and stamp not in fired:
                    fired.add(stamp)
                    if source == 'stdout':
                        path = next(root.glob(label + '-*.log'))
                        with path.open('ab') as stream:
                            stream.write(b'new console activity\n')
                    else:
                        path = root / 'mutation/logs/log-fixture.txt'
                        path.parent.mkdir(parents=True, exist_ok=True)
                        with path.open('ab') as stream:
                            stream.write(b'2026-11-16T00:00:00Z [DBG] "Runner 1": Test session finished.\n')
            if exit_at is not None and clock[0] >= exit_at:
                running[0] = False
                return 0
            raise subprocess.TimeoutExpired('synthetic', timeout)
        proc.wait.side_effect = wait
        proc.poll.side_effect = lambda: None if running[0] else -9
        proc.kill.side_effect = lambda: running.__setitem__(0, False)
        output = io.StringIO()
        with patch.object(replay.time, 'monotonic', side_effect=lambda: clock[0]), \
                patch.object(replay.subprocess, 'Popen', return_value=proc), \
                patch.object(replay, 'os', SimpleNamespace(name='posix',
                    killpg=lambda *a: proc.kill(), fsync=replay.os.fsync)), \
                contextlib.redirect_stdout(output):
            result = replay.command(args or ['fixture', '--output', root / 'mutation'],
                                    root, root, label, {}, deadline)
        return result, output.getvalue(), proc

    def test_mutation_inactivity_stops_with_reason_and_finite_cleanup(self):
        result, output, proc = self.simulated_command()
        self.assertTrue(result['timeout'])
        self.assertEqual(result['timeout_reason'], 'log-inactivity')
        self.assertEqual(result['seconds'], 600)
        self.assertEqual(result['last_log_update_age_seconds'], 600)
        self.assertEqual(result['observed_completed_test_sessions'], 0)
        self.assertIn('reason=log-inactivity', output)
        self.assertIn('remaining=300s', output)
        self.assertIn('last_log_update_age=600s', output)
        self.assertIn('raw_log=', output)
        self.assertIn('collection is incomplete', output)
        proc.kill.assert_called_once()
        with self.assertRaises(ValueError):
            replay.require(result)

    def test_stdout_or_main_debug_log_changes_reset_mutation_inactivity(self):
        for source in ('stdout', 'details'):
            with self.subTest(source=source):
                result, output, proc = self.simulated_command(updates={300: source}, exit_at=750)
                self.assertFalse(result['timeout'])
                self.assertEqual(result['seconds'], 750)
                self.assertEqual(result['observed_completed_test_sessions'], int(source == 'details'))
                self.assertNotIn('STOP official', output)
                self.assertIn('last_log_update_age=0s', output)
                proc.kill.assert_not_called()

    def test_ordinary_commands_keep_absolute_deadline_and_quiet_tests_are_not_called_stalled(self):
        result, output, proc = self.simulated_command(label='baseline-test', exit_at=630)
        self.assertFalse(result['timeout'])
        self.assertIsNone(result['inactivity_limit_seconds'])
        self.assertIn('inactivity_limit=disabled', output)
        proc.kill.assert_not_called()
        result, output, proc = self.simulated_command(deadline=45)
        self.assertTrue(result['timeout'])
        self.assertEqual(result['timeout_reason'], 'absolute-deadline')
        self.assertEqual(result['seconds'], 45)
        self.assertIn('reason=absolute-deadline', output)
        proc.kill.assert_called_once()

    def test_generated_qualify_labels_still_enable_mutation_watchdog(self):
        result, output, _ = self.simulated_command(label='command-04', args=['dotnet', 'stryker'])
        self.assertTrue(result['timeout'])
        self.assertEqual(result['timeout_reason'], 'log-inactivity')
        self.assertIn('STOP command-04', output)

    def test_debug_log_session_count_observes_new_bytes_once_without_claiming_mutant_counts(self):
        result, output, _ = self.simulated_command(updates={30: 'details', 60: 'details'}, exit_at=90)
        self.assertFalse(result['timeout'])
        self.assertEqual(result['observed_completed_test_sessions'], 2)
        self.assertIn('Test session finished', result['last_observed_event'])
        self.assertIn('observed_completed_test_sessions=2', output)
        self.assertNotIn('completed_mutants=', output)

    def test_actual_quiet_mutation_process_is_killed_at_inactivity_not_absolute_deadline(self):
        with tempfile.TemporaryDirectory() as folder, \
                patch.object(replay, 'MUTATION_INACTIVITY_SECONDS', 0.05), \
                contextlib.redirect_stdout(io.StringIO()):
            root = Path(folder)
            result = replay.command([sys.executable, '-c', 'import time; time.sleep(30)'],
                                    root, root, 'official', dict(replay.os.environ), 3)
        self.assertTrue(result['timeout'])
        self.assertEqual(result['timeout_reason'], 'log-inactivity')
        self.assertIsNotNone(result['exit_code'])
        self.assertLess(result['seconds'], 3)
        with self.assertRaisesRegex(ValueError, 'reason=log-inactivity'):
            replay.require(result)

    def test_running_command_emits_heartbeat_without_extending_deadline(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            proc = MagicMock()
            waits = []
            raw = b'\x1b[31m.S.T\x1b[0m\r'
            details = root / 'mutation' / 'logs' / 'log-fixture.txt'
            detail_bytes = b'old detail\n' * 1000 + b'\x1b[32mTesting mutant 42\x1b[0m\r'
            def wait(timeout):
                waits.append(timeout)
                if len(waits) == 1:
                    next(root.glob('heartbeat-*.log')).write_bytes(raw)
                    details.parent.mkdir(parents=True)
                    details.write_bytes(detail_bytes)
                    raise subprocess.TimeoutExpired('fixture', 30)
                return 0
            proc.wait.side_effect = wait
            output = io.StringIO()
            with patch.object(replay.subprocess, 'Popen', return_value=proc), \
                    patch.object(replay.time, 'monotonic', side_effect=[0, 0, 30, 30, 30, 31]), \
                    contextlib.redirect_stdout(output):
                result = replay.command(['fixture', '--output', root / 'mutation'], root, root, 'heartbeat', {}, 60)
            self.assertFalse(result['timeout'])
            self.assertIn('WAIT heartbeat', output.getvalue())
            self.assertIn('END heartbeat', output.getvalue())
            self.assertIn('OUTPUT .S.T', output.getvalue())
            self.assertIn('PROGRESS ', output.getvalue())
            self.assertIn('Testing mutant 42', output.getvalue())
            self.assertLess(len(output.getvalue()), 3000)
            self.assertEqual(details.read_bytes(), detail_bytes)
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
