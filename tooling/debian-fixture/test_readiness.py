"""Private serial readiness contracts; never starts a guest or makes SSH connections."""
import contextlib
import hashlib
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch

import fixture as f

NONCE = 'a' * 32


class ReadinessContracts(unittest.TestCase):
    def test_nonce_validation(self):
        for nonce in ('', 'a' * 31, 'g' * 32, 'a' * 32 + '\n', '$(id)'):
            with self.subTest(nonce=nonce), self.assertRaisesRegex(f.Failure, 'readiness-nonce'):
                f.readiness_marker(nonce)

    def test_exact_current_nonce_line_required_and_console_private(self):
        with tempfile.TemporaryDirectory() as d, tempfile.TemporaryFile() as output:
            h = f.Harness(Path(d))
            vm = Mock()
            vm.poll.return_value = None
            data = (b'PRIVATE_SENTINEL\nSSH-2.0-test\n' +
                    f.readiness_marker('b' * 32).encode() + b'\n' +
                    b'prefix' + f.readiness_marker(NONCE).encode() + b'\n')
            output.write(data)
            output.flush()
            offset = output.tell()
            def complete(seconds):
                self.assertEqual(seconds, 5)
                self.assertEqual(output.tell(), offset)  # pread must not disturb writer
                output.write(f.readiness_marker(NONCE).encode() + b'\r\n')
                output.flush()
            with patch.object(f.time, 'sleep', side_effect=complete) as sleep, \
                 contextlib.redirect_stdout(io.StringIO()) as log:
                f.wait_ready(h, vm, output, NONCE)
            sleep.assert_called_once_with(5)
            self.assertNotIn('PRIVATE_SENTINEL', log.getvalue())
            self.assertNotIn(NONCE, log.getvalue())
            self.assertIn('fixture-bootstrap-ready', log.getvalue())
            self.assertIn(hashlib.sha256(data + f.readiness_marker(NONCE).encode() + b'\r\n').hexdigest(), log.getvalue())

    def test_unterminated_marker_is_not_ready(self):
        with tempfile.TemporaryDirectory() as d, tempfile.TemporaryFile() as output:
            h = f.Harness(Path(d))
            vm = Mock()
            vm.poll.return_value = None
            output.write(f.readiness_marker(NONCE).encode())
            output.flush()
            def newline(seconds):
                output.write(b'\n')
                output.flush()
            with patch.object(f.time, 'sleep', side_effect=newline) as sleep:
                f.wait_ready(h, vm, output, NONCE)
            sleep.assert_called_once_with(5)

    def test_timeout_and_vm_death_even_with_marker(self):
        for dead in (False, True):
            with self.subTest(dead=dead), tempfile.TemporaryDirectory() as d, tempfile.TemporaryFile() as output:
                h = f.Harness(Path(d))
                h.deadline = 0
                vm = Mock()
                vm.poll.return_value = 1 if dead else None
                output.write(f.readiness_marker(NONCE).encode() + b'\n')
                output.flush()
                with self.assertRaisesRegex(f.Failure, 'boot-qemu-exit' if dead else 'boot-timeout'):
                    f.wait_ready(h, vm, output, NONCE)

    def test_serial_cap_checked_before_marker(self):
        with tempfile.TemporaryDirectory() as d, tempfile.TemporaryFile() as output:
            h = f.Harness(Path(d))
            vm = Mock()
            vm.poll.return_value = None
            h.children.append((vm, output))
            output.write(f.readiness_marker(NONCE).encode() + b'\n' + b'x' * 1024**2)
            output.flush()
            with self.assertRaisesRegex(f.Failure, 'boot-output-limit'):
                f.wait_ready(h, vm, output, NONCE)

    def test_exercise_gates_one_strict_probe_and_requires_exact_result(self):
        for failure in ('readiness', 'identity', 'host-key', None):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as d:
                root = Path(d)
                h = f.Harness(root)
                probes = []
                def download(name, cap, seconds):
                    (root / name).write_bytes(b'base')
                def run(args, phase, seconds=30):
                    if phase == 'disk-info':
                        return b'{"format":"qcow2","virtual-size":1024}'
                    if phase == 'keys':
                        (root / args[-1]).write_text('synthetic-private')
                        (root / (args[-1] + '.pub')).write_text('ssh-ed25519 synthetic\n')
                    if phase == 'probe':
                        probes.append(args)
                        if failure == 'host-key':
                            raise f.Failure('probe-exit')
                        return b'wrong' if failure == 'identity' else f.EXPECTED
                    return b''
                with patch.object(h, 'download', side_effect=download), \
                     patch.object(h, 'run', side_effect=run), \
                     patch.object(h, 'start', return_value=(Mock(), Mock())), \
                     patch.object(f, 'validate_provenance'), \
                     patch.object(f.secrets, 'token_hex', return_value=NONCE), \
                     patch.object(f, 'wait_ready', side_effect=f.Failure('boot-timeout') if failure == 'readiness' else None) as ready, \
                     patch.dict(f.PIN, sha512=hashlib.sha512(b'base').hexdigest()):
                    if failure:
                        with self.assertRaisesRegex(f.Failure, {'readiness': 'boot-timeout', 'identity': 'probe-identity', 'host-key': 'probe-exit'}[failure]):
                            f.exercise(h)
                    else:
                        f.exercise(h)
                    self.assertEqual(ready.call_args.args[-1], NONCE)
                self.assertEqual(len(probes), 0 if failure == 'readiness' else 1)
                if probes:
                    self.assertIn('StrictHostKeyChecking=yes', probes[0])
                    self.assertEqual(probes[0][-1], f.SMOKE)
