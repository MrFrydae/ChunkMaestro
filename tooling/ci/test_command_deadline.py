"""Owned-process timeout controls, independent of GitHub runner cancellation."""
import contextlib
import io
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

import command


class CommandDeadlineTests(unittest.TestCase):
    @unittest.skipUnless(os.name == 'posix', 'Real POSIX process-group probe; Windows uses the taskkill contract')
    def test_actual_silent_parent_and_descendant_stop_at_local_deadline(self):
        with tempfile.TemporaryDirectory() as temporary:
            pid_file = Path(temporary) / 'descendant.pid'
            script = ('import pathlib,subprocess,sys,time; '
                      'child=subprocess.Popen([sys.executable,"-c","import time;time.sleep(60)"]); '
                      'pathlib.Path(sys.argv[1]).write_text(str(child.pid)); time.sleep(60)')
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                result = command.execute([sys.executable, '-c', script, str(pid_file)],
                    command.child_environment(os.environ), timeout=2, heartbeat=0.5)
            self.assertEqual(124, result)
            self.assertIn('[ci-command] running', output.getvalue())
            self.assertIn('terminating owned process tree', output.getvalue())
            descendant = pid_file.read_text()
            state = subprocess.run(['ps', '-o', 'stat=', '-p', descendant],
                                   capture_output=True, text=True, timeout=5)
            self.assertTrue(not state.stdout.strip() or state.stdout.strip().startswith('Z'), state.stdout)

    def test_windows_deadline_stops_owned_tree_and_never_returns_success(self):
        process = Mock(pid=12345)
        process.wait.side_effect = [subprocess.TimeoutExpired(['synthetic'], 15), 1]
        with patch.object(command.os, 'name', 'nt'), \
             patch.object(command.time, 'monotonic', side_effect=[0, 0, 180, 180]), \
             patch.object(command.signal, 'signal') as signals, \
             patch.object(command.subprocess, 'Popen', return_value=process), \
             patch.object(command.subprocess, 'run') as terminate:
            self.assertEqual(124, command.execute(['synthetic'], {'PATH': 'fixture'}))
        terminate.assert_called_once_with(['taskkill', '/PID', '12345', '/T', '/F'],
                                          env={'PATH': 'fixture'}, timeout=10, check=False)
        self.assertEqual(5, process.wait.call_args.kwargs['timeout'])
        self.assertEqual(signal.SIGTERM, signals.call_args.args[0])

    @unittest.skipUnless(os.name == 'posix', 'Real POSIX inherited-output probe')
    def test_success_does_not_leave_owned_descendant_holding_runner_output(self):
        with tempfile.TemporaryDirectory() as temporary:
            pid_file = Path(temporary) / 'descendant.pid'
            script = ('import pathlib,subprocess,sys; '
                      'child=subprocess.Popen([sys.executable,"-c","import time;time.sleep(60)"]); '
                      'pathlib.Path(sys.argv[1]).write_text(str(child.pid))')
            self.assertEqual(0, command.execute([sys.executable, '-c', script, str(pid_file)],
                                               command.child_environment(os.environ), timeout=5))
            state = subprocess.run(['ps', '-o', 'stat=', '-p', pid_file.read_text()],
                                   capture_output=True, text=True, timeout=5)
            self.assertTrue(not state.stdout.strip() or state.stdout.strip().startswith('Z'), state.stdout)

    def test_interrupt_and_termination_error_preserve_failure_and_restore_handler(self):
        for error, expected in ((KeyboardInterrupt(), 130), (command.CommandInterrupted(signal.SIGTERM), 143)):
            with self.subTest(error=type(error).__name__):
                process = Mock(pid=12345)
                process.wait.side_effect = error
                with patch.object(command.os, 'name', 'nt'), \
                     patch.object(command.signal, 'signal', return_value=signal.SIG_DFL) as signals, \
                     patch.object(command.subprocess, 'Popen', return_value=process), \
                     patch.object(command.subprocess, 'run', side_effect=OSError('synthetic tree failure')):
                    self.assertEqual(expected, command.execute(['synthetic'], {}))
                self.assertEqual((signal.SIGTERM, signal.SIG_DFL), signals.call_args.args)

    def test_real_exit_code_and_deadline_validation(self):
        process = Mock()
        process.wait.return_value = 7
        with patch.object(command.os, 'name', 'nt'), patch.object(command.signal, 'signal'), \
             patch.object(command.subprocess, 'Popen', return_value=process):
            self.assertEqual(7, command.execute(['synthetic'], {}))
        for timeout in (0, -1, 181):
            with self.subTest(timeout=timeout), patch.object(command.subprocess, 'Popen') as start:
                with self.assertRaises(ValueError):
                    command.execute(['synthetic'], {}, timeout=timeout)
                start.assert_not_called()

