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
    def test_primary_apt_failure_survives_compound_inventory_and_cleanup_failures(self):
        for secondary in (f.Failure('package-inventory-output-limit'), PermissionError('PRIVATE_DETAIL')):
            with self.subTest(secondary=type(secondary).__name__), tempfile.TemporaryDirectory() as d:
                root = Path(d)
                key = root / 'keyring'
                key.write_bytes(b'synthetic')
                (root / 'setup').mkdir()
                with patch.object(r, 'runner_guard'), \
                     patch.object(r, 'KEYRING', str(key)), \
                     patch.object(r, 'validate_sources'), \
                     patch.object(f, 'inventory', return_value={'execution-mode': 'tcg-system-qemu64-multi'}), \
                     patch.object(r.tempfile, 'mkdtemp', return_value=str(root / 'setup')), \
                     patch.object(r.SetupHarness, 'run', side_effect=f.Failure('apt-update-output-limit')), \
                     patch.object(r, 'versions', side_effect=secondary), \
                     patch.object(r.SetupHarness, 'cleanup', side_effect=PermissionError('PRIVATE_DETAIL')), \
                     contextlib.redirect_stdout(io.StringIO()) as output:
                    with self.assertRaisesRegex(f.Failure, '^apt-update-output-limit$'):
                        r.setup()
                events = [json.loads(line) for line in output.getvalue().splitlines()]
                self.assertIn({'phase': 'setup-primary-apt-update-output-limit', 'status': 'failed'}, events)
                self.assertIn({'phase': 'setup-workspace', 'status': 'retained-for-ephemeral-runner-destruction'}, events)
                self.assertTrue(any(e['phase'].startswith('setup-inventory-') for e in events))
                self.assertTrue(any(e['phase'].startswith('setup-cleanup-') for e in events))
                self.assertNotIn('PRIVATE_DETAIL', output.getvalue())

    def test_final_inventory_failure_cannot_replace_setup_failure(self):
        with patch.object(sys, 'argv', ['runner_setup.py', 'setup']), \
             patch.object(r, 'setup', side_effect=f.Failure('apt-install-exit')), \
             patch.object(f, 'inventory', side_effect=PermissionError('PRIVATE_DETAIL')), \
             contextlib.redirect_stdout(io.StringIO()) as output:
            with self.assertRaisesRegex(f.Failure, '^apt-install-exit$'):
                r.main()
        self.assertIn('setup-final-inventory-setup-internal', output.getvalue())
        self.assertNotIn('PRIVATE_DETAIL', output.getvalue())

    def test_final_inventory_failure_after_success_still_fails(self):
        with patch.object(sys, 'argv', ['runner_setup.py', 'setup']), \
             patch.object(r, 'setup'), patch.object(f, 'inventory', side_effect=f.Failure('inventory-refused')):
            with self.assertRaisesRegex(f.Failure, '^inventory-refused$'):
                r.main()

    def test_all_missing_command_combinations_named_without_paths(self):
        for bits in itertools.product((False, True), repeat=len(f.TOOLS)):
            present = dict(zip(f.TOOLS, bits))
            with self.subTest(bits=bits), contextlib.redirect_stdout(io.StringIO()) as log, \
                 patch.object(f.shutil, 'which', side_effect=lambda t, path: '/private/SECRET' if present[t] else None), \
                 patch.object(f.os, 'open', side_effect=AssertionError('KVM must not be opened')):
                result = f.inventory()
            events = [json.loads(line) for line in log.getvalue().splitlines()]
            for tool in f.TOOLS:
                self.assertEqual(result[tool], 'ready' if present[tool] else 'missing')
            self.assertEqual(len(events), 10)
            self.assertNotIn('SECRET', log.getvalue())
            self.assertEqual(result['execution-mode'], 'tcg-system-qemu64-multi')

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
                 patch.object(r, 'runner_guard'), \
                 patch.object(r, 'KEYRING', str(Path(d) / 'keyring')), \
                 patch.object(r, 'validate_sources'), patch.object(f, 'inventory', return_value={'execution-mode': 'tcg-system-qemu64-multi'}), \
                 patch.object(r.SetupHarness, 'run', run), \
                 patch.object(r, 'versions') as versions, patch.object(r.SetupHarness, 'cleanup') as cleanup:
                Path(r.KEYRING).write_bytes(b'synthetic')
                with self.assertRaisesRegex(f.Failure, phase + '-timeout'):
                    r.setup()
                self.assertEqual(calls, ['apt-update'] if phase == 'apt-update' else ['apt-update', 'apt-install'])
                versions.assert_called_once()
                cleanup.assert_called_once()

    def test_setup_reaches_fixed_apt_without_kvm(self):
        with tempfile.TemporaryDirectory() as d, \
             patch.object(r, 'runner_guard'), \
             patch.object(r, 'KEYRING', str(Path(d) / 'keyring')), \
             patch.object(r, 'validate_sources'), \
             patch.object(f, 'inventory', return_value={'execution-mode': 'tcg-system-qemu64-multi'}), \
             patch.object(f.os, 'open', side_effect=AssertionError('must not open KVM')), \
             patch.object(r.SetupHarness, 'run') as run, \
             patch.object(r, 'versions'), patch.object(r.SetupHarness, 'cleanup'):
            Path(r.KEYRING).write_bytes(b'synthetic')
            r.setup()
            self.assertEqual([c.args[1] for c in run.call_args_list], ['apt-update', 'apt-install'])

    def test_private_signed_sources_ignore_runner_mirror_and_third_party_lists(self):
        reads = []
        def read(path):
            reads.append(str(path))
            if str(path) != r.KEYRING:
                raise AssertionError('Ambient source/mirror lists must not be consumed')
            return b'synthetic archive keyring'
        with tempfile.TemporaryDirectory() as d:
            root = Path(d) / 'setup'
            root.mkdir()
            with patch.object(r, 'runner_guard'), patch.object(f, 'inventory'), \
                 patch.object(Path, 'read_bytes', read), \
                 patch.object(r.tempfile, 'mkdtemp', return_value=str(root)), \
                 patch.object(r.SetupHarness, 'run') as run, \
                 patch.object(r, 'versions'), patch.object(r.SetupHarness, 'cleanup'), \
                 contextlib.redirect_stdout(io.StringIO()) as output:
                r.setup()
            self.assertEqual(reads, [r.KEYRING])
            staged = (root / 'ubuntu.sources').read_text()
            blocks = [dict(line.split(': ', 1) for line in block.splitlines())
                      for block in staged.strip().split('\n\n')]
            self.assertEqual([b['URIs'] for b in blocks], [
                'https://archive.ubuntu.com/ubuntu/', 'https://security.ubuntu.com/ubuntu/'])
            self.assertEqual([b['Suites'] for b in blocks], ['noble noble-updates', 'noble-security'])
            for block in blocks:
                self.assertEqual(set(block), {'Types', 'URIs', 'Suites', 'Components', 'Signed-By'})
                self.assertEqual(block['Types'], 'deb')
                self.assertEqual(block['Components'], 'main universe')
                self.assertEqual(block['Signed-By'], '/usr/share/keyrings/ubuntu-archive-keyring.gpg')
            self.assertEqual([call.args[1] for call in run.call_args_list], ['apt-update', 'apt-install'])
            for call in run.call_args_list:
                self.assertIn('Dir::Etc::sourcelist=' + str(root / 'ubuntu.sources'), call.args[0])
                self.assertIn('Dir::Etc::sourceparts=-', call.args[0])
                self.assertIn('APT::Get::AllowUnauthenticated=false', call.args[0])
            self.assertIn('fixed-private-official-ubuntu-noble', output.getvalue())

    def test_source_exact_optional_slash_only(self):
        for host in ('archive', 'security', 'azure.archive'):
            for scheme in ('http', 'https'):
                for slash in ('', '/'):
                    uri = f'{scheme}://{host}.ubuntu.com/ubuntu{slash}'.encode()
                    r.validate_sources(SOURCE.replace(b'http://archive.ubuntu.com/ubuntu/', uri))
        for uri in (b'http://archive.ubuntu.com/ubuntu//', b'http://archive.ubuntu.com/ubuntu/../',
                    b'http://archive.ubuntu.com/ubuntu?evil', b'http://archive.ubuntu.com.evil/ubuntu',
                    b'http://user@archive.ubuntu.com/ubuntu', b'http://archive.ubuntu.com:80/ubuntu'):
            with self.subTest(uri=uri), self.assertRaises(f.Failure):
                r.validate_sources(SOURCE.replace(b'http://archive.ubuntu.com/ubuntu/', uri))
