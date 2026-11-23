"""Offline only; no APT, sudo, image retrieval or VM execution."""
import contextlib
import io
import itertools
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

import fixture as f
import runner_setup as r

SOURCE = b'''Types: deb
URIs: http://archive.ubuntu.com/ubuntu/
Suites: noble noble-updates
Components: main universe restricted multiverse
Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg
'''


class RunnerContracts(unittest.TestCase):
    def test_all_missing_command_combinations_named_without_paths(self):
        for bits in itertools.product((False, True), repeat=len(f.TOOLS)):
            present = dict(zip(f.TOOLS, bits))
            with self.subTest(bits=bits), contextlib.redirect_stdout(io.StringIO()) as log, \
                 patch.object(f.shutil, 'which', side_effect=lambda t, path: '/private/SECRET' if present[t] else None), \
                 patch.object(f.os, 'stat', side_effect=PermissionError('/private/SECRET')):
                result = f.inventory()
            events = [json.loads(line) for line in log.getvalue().splitlines()]
            for tool in f.TOOLS:
                self.assertEqual(result[tool], 'ready' if present[tool] else 'missing')
            self.assertEqual(len(events), 10)
            self.assertNotIn('SECRET', log.getvalue())
            self.assertEqual(result['kvm'], 'not-openable')

    def test_source_policy_denies_insecure_or_unreviewed_inputs(self):
        r.validate_sources(SOURCE)
        for data in (b'', SOURCE.replace(b'archive.ubuntu.com', b'evil.example'),
                     SOURCE.replace(b'noble-updates', b'other'),
                     SOURCE.replace(b'universe', b'evil'),
                     SOURCE.replace(b'ubuntu-archive-keyring.gpg', b'other.gpg'),
                     SOURCE + b'Trusted: yes\n', SOURCE + b'Allow-Insecure: yes\n',
                     SOURCE + b'\nTypes: deb\nURIs: https://evil.example/\n'):
            with self.subTest(data=data), self.assertRaises(f.Failure):
                r.validate_sources(data)

    def test_fixed_package_allowlist_bounded_noninteractive_no_shell(self):
        self.assertEqual(r.PACKAGES, ('curl', 'qemu-system-x86', 'qemu-utils', 'cloud-image-utils', 'openssh-client'))
        with patch.dict(os.environ, {'GITHUB_TOKEN': 'SECRET', 'SSH_AUTH_SOCK': '/private/KEY', 'PACKAGES': 'evil'}):
            commands = r.apt_commands(Path('/synthetic'))
            env = r.SetupHarness(Path('/synthetic')).environment()
        self.assertEqual(set(env), {'PATH', 'HOME', 'LANG', 'LC_ALL', 'DEBIAN_FRONTEND', 'NEEDRESTART_MODE'})
        self.assertNotIn('SECRET', str(env))
        self.assertEqual(env['DEBIAN_FRONTEND'], 'noninteractive')
        self.assertEqual(len(commands), 2)
        for args, phase, seconds in commands:
            self.assertEqual(args[:2], ['/usr/bin/sudo', '-n'])
            self.assertIn('/usr/bin/timeout', args)
            self.assertIn('--kill-after=5', args)
            self.assertLessEqual(seconds, 300)
            self.assertIn('Acquire::Retries=0', args)
            self.assertIn('Dir::Etc::sourceparts=-', args)
            self.assertNotIn('evil', args)
        self.assertEqual(commands[1][0][-len(r.PACKAGES):], list(r.PACKAGES))
        self.assertIn('--no-remove', commands[1][0])
        self.assertIn('--no-install-recommends', commands[1][0])

    def test_local_runner_setup_refused_before_processes(self):
        with patch.dict(os.environ, {}, clear=True), patch.object(r.f.Harness, 'start') as start:
            with self.assertRaisesRegex(f.Failure, 'setup-runner-refused'):
                r.setup()
            start.assert_not_called()

    def test_failure_retains_inventory_without_vm(self):
        for failure in ('apt-update-exit', 'apt-install-timeout-or-output-limit', 'setup-source-policy'):
            with patch.object(sys, 'argv', ['runner_setup.py', 'setup']), \
                 patch.object(r, 'setup', side_effect=f.Failure(failure)), \
                 patch.object(f, 'inventory') as inventory, patch.object(f, 'exercise') as vm:
                with self.assertRaisesRegex(f.Failure, failure):
                    r.main()
                inventory.assert_called_once()
                vm.assert_not_called()

    def test_kvm_permission_refusal_even_with_all_tools(self):
        with patch.object(f.platform, 'system', return_value='Linux'), \
             patch.object(f.platform, 'machine', return_value='x86_64'), \
             patch.object(f.os, 'geteuid', return_value=1000), \
             patch.object(f.shutil, 'which', return_value='/usr/bin/tool'), \
             patch.object(f.os, 'stat') as info, \
             patch.object(f.os, 'open', side_effect=PermissionError('SECRET')):
            info.return_value.st_mode = 0o020000
            with self.assertRaisesRegex(f.Failure, 'preflight-kvm'):
                f.prerequisites()

    def test_user_package_input_denied(self):
        with patch.object(sys, 'argv', ['runner_setup.py', 'setup', 'evil; id']), \
             contextlib.redirect_stderr(io.StringIO()), patch.object(r, 'setup') as setup:
            with self.assertRaisesRegex(f.Failure, 'setup-arguments'):
                r.main()
            setup.assert_not_called()

    def test_setup_failure_stops_install_and_cleans_before_return(self):
        for phase in ('apt-update', 'apt-install'):
            calls = []
            def run(h, args, name, seconds=30):
                calls.append(name)
                if name == phase:
                    raise f.Failure(name + '-timeout-or-output-limit')
            with tempfile.TemporaryDirectory() as d, \
                 patch.object(r, 'runner_guard'), patch.object(r, 'SOURCES', Path(d) / 'sources'), \
                 patch.object(r, 'KEYRING', str(Path(d) / 'keyring')), \
                 patch.object(r, 'validate_sources'), patch.object(f, 'inventory', return_value={'kvm': 'ready'}), \
                 patch.object(r.SetupHarness, 'run', run), \
                 patch.object(r, 'versions') as versions, patch.object(r.SetupHarness, 'cleanup') as cleanup:
                r.SOURCES.write_bytes(SOURCE)
                Path(r.KEYRING).write_bytes(b'synthetic')
                with self.assertRaisesRegex(f.Failure, phase + '-timeout'):
                    r.setup()
                self.assertEqual(calls, ['apt-update'] if phase == 'apt-update' else ['apt-update', 'apt-install'])
                versions.assert_called_once()
                cleanup.assert_called_once()

    def test_setup_kvm_denial_before_apt_or_source_access(self):
        for status in ('missing', 'not-openable', 'not-character-device'):
            with patch.object(r, 'runner_guard'), patch.object(f, 'inventory', return_value={'kvm': status}), \
                 patch.object(r.SetupHarness, 'start') as start:
                with self.assertRaisesRegex(f.Failure, 'preflight-kvm'):
                    r.setup()
                start.assert_not_called()
