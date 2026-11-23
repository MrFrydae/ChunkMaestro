"""Offline contracts: never starts QEMU, downloads an image or changes privileges."""
import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

import fixture as f


class Contracts(unittest.TestCase):
    def test_no_opt_in_denied_before_preflight(self):
        with patch.object(sys, 'argv', ['fixture.py']), patch.object(f, 'prerequisites') as pre:
            with self.assertRaisesRegex(f.Failure, 'owner-opt-in-required'):
                f.main()
            pre.assert_not_called()

    def test_platform_denied(self):
        with patch.object(f.platform, 'system', return_value='Darwin'):
            with self.assertRaisesRegex(f.Failure, 'preflight-platform'):
                f.prerequisites()

    def test_missing_qemu_denied(self):
        with patch.object(f.platform, 'system', return_value='Linux'), \
             patch.object(f.platform, 'machine', return_value='x86_64'), \
             patch.object(f.os, 'geteuid', return_value=1000), \
             patch.object(f.shutil, 'which', return_value=None):
            with self.assertRaisesRegex(f.Failure, 'preflight-tools'):
                f.prerequisites()

    def test_missing_kvm_denied(self):
        with patch.object(f.platform, 'system', return_value='Linux'), \
             patch.object(f.platform, 'machine', return_value='x86_64'), \
             patch.object(f.os, 'geteuid', return_value=1000), \
             patch.object(f.shutil, 'which', return_value='/usr/bin/tool'), \
             patch.object(f.os, 'stat', side_effect=FileNotFoundError):
            with self.assertRaisesRegex(f.Failure, 'preflight-kvm'):
                f.prerequisites()

    def test_credentials_stripped(self):
        with patch.dict(os.environ, {'GITHUB_TOKEN': 'sentinel', 'SSH_AUTH_SOCK': '/secret',
                                     'AWS_SECRET_ACCESS_KEY': 'secret'}):
            env = f.child_env('/private')
        self.assertEqual(set(env), {'PATH', 'HOME', 'LANG', 'LC_ALL'})
        self.assertNotIn('sentinel', str(env))

    def test_provenance_denies_tampering(self):
        sums = (f.HERE / 'SHA512SUMS').read_bytes()
        self.assertEqual(hashlib.sha256(sums).hexdigest(), f.PIN['checksums_sha256'])
        for s, m, error in [(sums + b'x', b'{}', 'checksums'), (sums, b'{}', 'metadata')]:
            with self.assertRaisesRegex(f.Failure, 'provenance-' + error):
                f.validate_provenance(s, m)

    def test_provenance_valid_and_wrong_identity(self):
        info = dict(arch='amd64', release='trixie', release_id='13', type='official',
                    vendor='genericcloud', version='20261103-2651')
        sums = (f.HERE / 'SHA512SUMS').read_bytes()
        for arch in ('amd64', 'arm64'):
            info['arch'] = arch
            meta = json.dumps({'items': [{'data': {'info': info}}]}).encode()
            with patch.dict(f.PIN, metadata_sha512=hashlib.sha512(meta).hexdigest()):
                if arch == 'amd64':
                    f.validate_provenance(sums, meta)
                else:
                    with self.assertRaisesRegex(f.Failure, 'identity'):
                        f.validate_provenance(sums, meta)

    def test_strict_host_key_no_agent_or_forwarding(self):
        args = f.ssh_args(Path('/private'), 12345)
        for expected in ('StrictHostKeyChecking=yes', 'HostKeyAlgorithms=ssh-ed25519',
                         'IdentityAgent=none', 'ForwardAgent=no', 'ClearAllForwardings=yes',
                         'UserKnownHostsFile=/private/known_hosts', 'ConnectionAttempts=1'):
            self.assertIn(expected, args)
        self.assertNotIn('accept-new', str(args))
        self.assertEqual(args[-1], 'fixture@127.0.0.1')

    @unittest.skipUnless(Path('/usr/bin/ssh').exists(), 'OpenSSH configuration parser unavailable')
    def test_openssh_effective_trust_config(self):
        args = f.ssh_args(Path('/private-fixture'), 12345)
        result = subprocess.run([args[0], '-G'] + args[1:], check=True,
                                capture_output=True, env=f.child_env('/tmp'), timeout=5)
        options = result.stdout.decode()
        for expected in ('stricthostkeychecking true', 'identityagent none',
                         'userknownhostsfile /private-fixture/known_hosts',
                         'hostkeyalgorithms ssh-ed25519', 'forwardagent no'):
            self.assertIn(expected, options)

    def test_seed_uses_expected_host_key_and_unprivileged_user(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            for name in ('client.pub', 'host', 'host.pub'):
                (root / name).write_text('synthetic-' + name)
            config = json.loads(f.cloud_config(root).split('\n', 1)[1])
            self.assertEqual(config['ssh_keys']['ed25519_public'], 'synthetic-host.pub')
            self.assertFalse(config['users'][0]['sudo'])
            self.assertFalse(config['ssh_pwauth'])
            self.assertFalse(config['ssh']['emit_keys_to_console'])

    def test_timeout_kills_child_before_workspace_deletion(self):
        with tempfile.TemporaryDirectory() as d:
            h = f.Harness(Path(d))
            try:
                p, out = h.start([sys.executable, '-c', 'import time; time.sleep(60)'])
                with self.assertRaisesRegex(f.Failure, 'probe-timeout'):
                    h.wait(p, out, 'probe', 0.1)
            finally:
                h.cleanup()
            self.assertIsNotNone(p.poll())
            self.assertTrue(Path(d).is_dir())

    def test_failed_command_is_sanitized_and_reaped(self):
        with tempfile.TemporaryDirectory() as d, contextlib.redirect_stdout(io.StringIO()) as log:
            h = f.Harness(Path(d))
            try:
                with self.assertRaisesRegex(f.Failure, 'probe-exit'):
                    h.run([sys.executable, '-c', 'print("PRIVATE_SENTINEL"); exit(7)'], 'probe')
            finally:
                h.cleanup()
            self.assertNotIn('PRIVATE_SENTINEL', log.getvalue())
            self.assertEqual(h.children[0][0].returncode, 7)

    @unittest.skipUnless(sys.platform == 'linux', 'Linux subreaper/reaping proof requires Linux')
    def test_linux_descendant_reaped(self):
        import ctypes
        self.assertEqual(ctypes.CDLL(None).prctl(36, 1, 0, 0, 0), 0)
        with tempfile.TemporaryDirectory() as d:
            h = f.Harness(Path(d))
            try:
                p, out = h.start([sys.executable, '-c',
                    'import subprocess,time; subprocess.Popen(["/bin/sleep","60"]); time.sleep(60)'])
                time.sleep(0.3)
            finally:
                h.cleanup()
            with self.assertRaises(ChildProcessError):
                os.waitpid(-1, os.WNOHANG)

    def test_cleanup_precedes_owned_disk_removal_on_failure(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d) / 'owned'
            root.mkdir()
            seen = []
            def cleanup(h):
                self.assertTrue(root.exists())
                seen.append('reaped')
            with patch.object(sys, 'argv', ['fixture.py', '--execute-owner-reviewed-fixture']), \
                 patch.object(f, 'prerequisites'), patch.object(f.ctypes, 'CDLL') as libc, \
                 patch.object(f.tempfile, 'mkdtemp', return_value=str(root)), \
                 patch.object(f.shutil, 'disk_usage', return_value=type('Usage', (), {'free': 11 * 1024**3})()), \
                 patch.object(f.signal, 'signal'), patch.object(f.signal, 'alarm'), \
                 patch.object(f, 'exercise', side_effect=f.Failure('synthetic-failure')), \
                 patch.object(f.Harness, 'cleanup', cleanup):
                libc.return_value.prctl.return_value = 0
                with self.assertRaisesRegex(f.Failure, 'synthetic-failure'):
                    f.main()
            self.assertEqual(seen, ['reaped'])
            self.assertFalse(root.exists())

    def test_output_limit_denied(self):
        with tempfile.TemporaryDirectory() as d:
            h = f.Harness(Path(d))
            p, out = h.start([sys.executable, '-c', 'print("x" * 1100000)'])
            try:
                with self.assertRaisesRegex(f.Failure, 'output-limit'):
                    h.wait(p, out, 'probe', 5)
            finally:
                h.cleanup()

    def test_boot_configuration_closed_and_cleanup_on_failure(self):
        # Reach the real QEMU argv builder with fake tools and tiny synthetic input.
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            h = f.Harness(root)
            calls = []
            def download(name, cap, seconds):
                (root / name).write_bytes(b'base')
            def run(args, phase, seconds=30):
                calls.append(args)
                if phase == 'disk-info':
                    return b'{"format":"qcow2","virtual-size":1024}'
                if phase == 'keys':
                    (root / args[-1]).write_text('private')
                    (root / (args[-1] + '.pub')).write_text('ssh-ed25519 synthetic\n')
                return b''
            def start(args):
                calls.append(args)
                raise f.Failure('synthetic-stop-before-vm')
            with patch.object(h, 'download', side_effect=download), \
                 patch.object(h, 'run', side_effect=run), \
                 patch.object(h, 'start', side_effect=start), \
                 patch.object(f, 'validate_provenance'), \
                 patch.dict(f.PIN, sha512=hashlib.sha512(b'base').hexdigest()):
                with self.assertRaisesRegex(f.Failure, 'synthetic-stop'):
                    f.exercise(h)
            qemu = calls[-1]
            self.assertIn('q35,accel=kvm', qemu)
            self.assertIn('2', qemu)
            self.assertIn('2048', qemu)
            network = qemu[qemu.index('-netdev') + 1]
            self.assertIn('restrict=on,hostfwd=tcp:127.0.0.1:', network)
            for banned in ('tcg', 'virtfs', 'fsdev', '0.0.0.0', 'daemonize'):
                self.assertNotIn(banned, ' '.join(qemu))


if __name__ == '__main__':
    unittest.main()
