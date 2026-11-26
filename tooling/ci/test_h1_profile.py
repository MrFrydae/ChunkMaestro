"""Inert reader/adapter tests. These do not emulate a native Linux profile."""
import base64
import contextlib
import io
import json
import os
from pathlib import Path
import signal
import sys
import tempfile
import unittest
from unittest import mock

import h1_profile as profile


class ReaderTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory()
        self.addCleanup(self.scratch.cleanup)
        self.root = Path(self.scratch.name).resolve()
        self.file = self.root / 'inert'
        self.file.write_bytes(b'fixture')

    def test_exact_limit_and_hash_input(self):
        self.assertEqual(b'fixture', profile.bounded_read(self.file, profile.Budget(), 7))

    def test_oversize_refused(self):
        with self.assertRaises(profile.Refusal):
            profile.bounded_read(self.file, profile.Budget(), 6)

    def test_symlink_refused(self):
        link = self.root / 'link'
        link.symlink_to(self.file)
        with self.assertRaises(OSError):
            profile.bounded_read(link, profile.Budget())

    def test_fifo_refused_without_blocking(self):
        fifo = self.root / 'fifo'
        os.mkfifo(fifo)
        with self.assertRaises(profile.Refusal):
            profile.bounded_read(fifo, profile.Budget())

    def test_count_limit(self):
        with self.assertRaises(profile.Refusal):
            profile.bounded_read(self.file, profile.Budget(count=0))

    def test_aggregate_limit(self):
        with self.assertRaises(profile.Refusal):
            profile.bounded_read(self.file, profile.Budget(total=6))

    def test_expired(self):
        with self.assertRaises(profile.Refusal):
            profile.bounded_read(self.file, profile.Budget(seconds=-1))

    def test_missing(self):
        with self.assertRaises(FileNotFoundError):
            profile.bounded_read(self.root / 'missing', profile.Budget())

    def test_directory_refused(self):
        with self.assertRaises(profile.Refusal):
            profile.bounded_read(self.root, profile.Budget())

    def test_installed_root_escape(self):
        with self.assertRaises(profile.Refusal):
            profile.trusted(self.file)

    def test_relative_installed_path(self):
        with self.assertRaises(profile.Refusal):
            profile.trusted(Path('relative'))

    def test_evidence_exclusive_and_recovery_uses_new_namespace(self):
        target = self.root / 'output'
        profile.output_namespace(target)
        sentinel = target / 'profile.json'
        sentinel.write_text('original')
        with self.assertRaises(FileExistsError):
            profile.output_namespace(target)
        self.assertEqual('original', sentinel.read_text())
        profile.output_namespace(self.root / 'retry')

    def test_output_symlink_parent_refused(self):
        link = self.root / 'link'
        link.symlink_to(self.root, target_is_directory=True)
        with self.assertRaises(profile.Refusal):
            profile.output_namespace(link / 'output')


class CommandTests(unittest.TestCase):
    def test_non_utf8_refuses_with_exact_bytes_and_observed_exit(self):
        argv = [sys.executable, '-I', '-c', "import os; os.write(1, b'raw\\xff')"]
        with self.assertRaisesRegex(profile.Refusal, 'command output is not UTF-8') as caught:
            profile.command(argv, profile.Budget())
        evidence = caught.exception.command_evidence
        self.assertEqual(argv, evidence['argv'])
        self.assertEqual(0, evidence['exit'])
        self.assertEqual(b'raw\xff', base64.b64decode(evidence['output_bytes_base64'], validate=True))
        self.assertIsNotNone(evidence['decoding_failure'])
        self.assertFalse(evidence['output_truncated'])
        self.assertIsNone(evidence['cleanup_uncertainty'])

    def test_nonzero_precedes_decoding_failure_and_preserves_both(self):
        with self.assertRaisesRegex(profile.Refusal, 'command exit 37') as caught:
            profile.command([sys.executable, '-I', '-c',
                             "import os; os.write(1, b'raw\\xff'); raise SystemExit(37)"], profile.Budget())
        evidence = caught.exception.command_evidence
        self.assertEqual(37, evidence['exit'])
        self.assertEqual('command exit 37', evidence['first_failure'])
        self.assertIsNotNone(evidence['decoding_failure'])
        self.assertEqual(b'raw\xff', base64.b64decode(evidence['output_bytes_base64'], validate=True))

    def test_real_local_python_adapter(self):
        result = profile.command([sys.executable, '-I', '-c', 'print("inert")'], profile.Budget())
        self.assertEqual('inert\n', result['output'])
        self.assertEqual(0, result['exit'])

    def test_nonzero_keeps_raw_output(self):
        with self.assertRaises(profile.Refusal) as caught:
            profile.command([sys.executable, '-I', '-c', 'print("first"); raise SystemExit(37)'], profile.Budget())
        self.assertEqual('command exit 37', str(caught.exception))
        self.assertEqual('first\n', caught.exception.command_evidence['output'])

    def test_output_overflow(self):
        with self.assertRaises(profile.Refusal) as caught:
            profile.command([sys.executable, '-I', '-c', 'print("x"*10000)'], profile.Budget(), 128)
        self.assertEqual('command output limit', str(caught.exception))
        self.assertEqual(128, len(caught.exception.command_evidence['output']))
        self.assertTrue(caught.exception.command_evidence['output_truncated'])
        self.assertEqual(b'x' * 128, base64.b64decode(caught.exception.command_evidence['output_bytes_base64'], validate=True))

    def test_timeout_kills_owned_process(self):
        with self.assertRaises(profile.Refusal) as caught:
            profile.command([sys.executable, '-I', '-c', 'import time; time.sleep(5)'], profile.Budget(seconds=0.1))
        self.assertEqual('command deadline', str(caught.exception))
        self.assertIsNone(caught.exception.command_evidence['cleanup_uncertainty'])

    def test_environment_not_forwarded(self):
        with mock.patch.dict(os.environ, {'H1_TEST_SECRET': 'never-forward'}):
            result = profile.command([sys.executable, '-I', '-c', 'import os; print(os.getenv("H1_TEST_SECRET"))'], profile.Budget())
        self.assertEqual('None\n', result['output'])

    def test_missing_command(self):
        with self.assertRaises(FileNotFoundError):
            profile.command(['/not/a/tool'], profile.Budget())

    def test_cancellation_never_succeeds(self):
        with mock.patch.object(profile.selectors.DefaultSelector, 'select', side_effect=KeyboardInterrupt):
            with self.assertRaises(profile.Refusal) as caught:
                profile.command([sys.executable, '-I', '-c', 'import time; time.sleep(5)'], profile.Budget())
        self.assertIsNone(caught.exception.command_evidence['cleanup_uncertainty'])

    def test_cleanup_failure_preserves_first_failure(self):
        original = os.killpg
        def kill_then_error(pid, number):
            try:
                original(pid, number)
            except ProcessLookupError:
                pass
            raise PermissionError('injected cleanup uncertainty after actual kill')
        with mock.patch.object(profile.os, 'killpg', side_effect=kill_then_error):
            with self.assertRaises(profile.Refusal) as caught:
                profile.command([sys.executable, '-I', '-c', 'raise SystemExit(37)'], profile.Budget())
        self.assertEqual('command exit 37', str(caught.exception))
        self.assertIn('injected cleanup', caught.exception.command_evidence['cleanup_uncertainty'])


class RefusalTests(unittest.TestCase):
    def test_oversize_inventory_is_retained_before_refusal(self):
        # Inject only this failure segment; no successful installed profile is simulated.
        inventory = {'argv': ['fixed-dpkg', '-W'], 'exit': 0,
                     'output': 'package\t1\tall\n' * 4097, 'cleanup_uncertainty': None}
        report = {'files': [], 'commands': []}
        class EmptyFragments:
            def iterdir(self):
                return iter(())
        def trusted(path):
            return EmptyFragments() if str(path) == '/etc/ld.so.conf.d' else Path(path)
        def command(argv, budget):
            return inventory if '-W' in argv else {'argv': argv, 'exit': 0, 'output': 'version'}
        with mock.patch.object(profile, 'trusted', side_effect=trusted), \
                mock.patch.object(profile, 'identity', return_value={'fixture': True}), \
                mock.patch.object(profile, 'bounded_read', return_value=b'ID=fixture\nVERSION_ID=1\n'), \
                mock.patch.object(profile, 'command', side_effect=command):
            with self.assertRaisesRegex(profile.Refusal, 'package count'):
                profile.collect(report, 'aarch64', '/usr/share/dotnet')
        self.assertEqual(5, len(report['commands']))
        self.assertIs(inventory, report['commands'][-1])

    def test_actual_nonlinux_no_collection(self):
        if sys.platform.startswith('linux'):
            self.skipTest('real non-Linux refusal only')
        with tempfile.TemporaryDirectory() as scratch:
            target = Path(scratch).resolve() / 'profile'
            with mock.patch.object(profile, 'collect', side_effect=AssertionError('must not collect')):
                with contextlib.redirect_stdout(io.StringIO()):
                    code = profile.main(['--output', str(target), '--architecture', 'x86_64', '--sdk-root', '/usr/share/dotnet'])
            self.assertEqual(77, code)
            report = json.loads((target / 'profile.json').read_text())
            self.assertEqual('NOTRUN', report['status'])
            self.assertEqual([], report['files'])
            self.assertIsNone(report['runtime_pin'])

    def test_publication_failure_does_not_hide_platform_refusal(self):
        if sys.platform.startswith('linux'):
            self.skipTest('real non-Linux refusal only')
        with tempfile.TemporaryDirectory() as scratch:
            with contextlib.redirect_stdout(io.StringIO()) as output:
                code = profile.main(['--output', scratch, '--architecture', 'aarch64', '--sdk-root', '/usr/share/dotnet'])
            result = json.loads(output.getvalue())
            self.assertEqual(77, code)
            self.assertEqual('NOTRUN: Linux only', result['first_failure'])
            self.assertIsNotNone(result['publication_uncertainty'])

    def test_architecture_and_root_gate_are_pure_denial_contracts(self):
        # Mocked predicates test refusal ordering only, never positive native collection.
        with mock.patch.object(profile.platform, 'system', return_value='Linux'):
            with mock.patch.object(profile.os, 'geteuid', return_value=0):
                self.assertEqual((1, 'REFUSED: unprivileged collector required'), profile.preflight('aarch64'))
            with mock.patch.object(profile.os, 'geteuid', return_value=1000):
                with mock.patch.object(profile.platform, 'machine', return_value='x86_64'):
                    self.assertEqual((1, 'REFUSED: native architecture mismatch'), profile.preflight('aarch64'))


if __name__ == '__main__':
    unittest.main()
