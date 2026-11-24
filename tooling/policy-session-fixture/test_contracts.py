"""Offline refusal/command/store contracts. These never invoke guest services."""
import copy
import importlib.util
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location('fixture_common', Path(__file__).with_name('common.py'))
c = importlib.util.module_from_spec(spec)
spec.loader.exec_module(c)


class Contracts(unittest.TestCase):
    def setUp(self):
        self.versions = {'systemd': '257.13-1~deb13u1', 'libsystemd0': '257.13-1~deb13u1',
                         'python3': '3.13.5-1', 'python3.13': '3.13.5-2', 'linux-image-6.12.0-contract': '6.12.0-1'}
        self.packages = {'versions': self.versions, 'release': 'contract-only',
                         'kernel_package': 'linux-image-6.12.0-contract'}
        self.identity = {'schema': 1, 'nonce': 'a' * 32, 'source_sha256': 'b' * 64,
                         'boot_id': 'boot', 'image_sha512': c.IMAGE, 'packages': self.versions,
                         'kernel_release': 'contract-only'}
        self.entry = [0, 0, 5, 100, 0, 0, 2]
        self.row = ['worker-a', *self.entry[:6], 'anon_inode:[pidfd]', self.entry[6]]

    def validate(self, identity):
        c.validate_identity(identity, 'a' * 32, 'b' * 64, 'boot', self.packages)

    def test_exact_guest_binding(self):
        self.validate(self.identity)

    def test_independent_binding_mismatches(self):
        for key in ('nonce', 'source_sha256', 'boot_id', 'image_sha512', 'kernel_release'):
            with self.subTest(key=key), self.assertRaises(c.Failure):
                identity = copy.deepcopy(self.identity)
                identity[key] = 'wrong'
                self.validate(identity)

    def test_no_boolean_bypass(self):
        with self.assertRaises(c.Failure):
            self.validate({'approved': True})

    def test_no_unpinned_packages(self):
        for value in ('*', '', 'latest', '>=257'):
            identity = copy.deepcopy(self.identity)
            identity['packages']['systemd'] = value
            with self.subTest(value=value), self.assertRaises(c.Failure):
                self.validate(identity)

    def test_package_inventory_mismatch(self):
        identity = copy.deepcopy(self.identity)
        identity['packages']['systemd'] = '257.9-1'
        with self.assertRaises(c.Failure):
            self.validate(identity)

    def test_extra_package_rejected(self):
        identity = copy.deepcopy(self.identity)
        identity['packages']['gcc'] = '14.0'
        with self.assertRaises(c.Failure):
            self.validate(identity)

    def test_command_allowlist(self):
        for unit in c.UNITS:
            self.assertEqual(c.command('stop', unit), ('/usr/bin/systemctl', 'stop', unit))

    def test_no_unit_or_shell_injection(self):
        for unit in ('minecraft.service', '../cm14-worker.service', '--all', 'cm14-worker.service;id'):
            with self.subTest(unit=unit), self.assertRaises(c.Failure):
                c.command('start', unit)

    def test_no_arbitrary_action(self):
        for action in ('restart', 'clean', 'set-property', 'daemon-reexec', '/bin/sh'):
            with self.subTest(action=action), self.assertRaises(c.Failure):
                c.command(action, c.UNITS[0])

    def test_store_requires_two_exact_originals(self):
        agent = ['agent-a', *self.entry[:6], '', self.entry[6]]
        c.confirm([self.row, agent], {'worker-a': self.entry, 'agent-a': self.entry})
        with self.assertRaises(c.Failure):
            c.confirm([self.row], {'worker-a': self.entry, 'agent-a': self.entry})

    def test_unknown_and_duplicate_store_members(self):
        for rows in ([self.row, self.row], [['unknown', *self.row[1:]]]):
            with self.subTest(rows=rows), self.assertRaises(c.Failure):
                c.confirm(rows, {'worker-a': self.entry})

    def test_inode_and_flags_are_not_labels(self):
        for index in (1, 2, 3, 4, 5, 6, 8):
            row = self.row.copy()
            row[index] += 1
            with self.subTest(index=index), self.assertRaises(c.Failure):
                c.confirm([row], {'worker-a': self.entry})

    def test_partial_removal_and_empty_are_exact(self):
        c.confirm([self.row], {'worker-a': self.entry})
        c.confirm([], {})
        with self.assertRaises(c.Failure):
            c.confirm([self.row], {})

    def test_guard_refuses_macos_before_process_execution(self):
        with patch.object(c.platform, 'system', return_value='Darwin'), patch.object(c, 'run_raw') as run:
            with self.assertRaises(c.Failure):
                c.guard()
            run.assert_not_called()

    def test_source_symlinks_refused(self):
        import tempfile
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'link').symlink_to('/tmp')
            with self.assertRaises(c.Failure):
                c.digest_tree(root)

    def test_source_mutation_changes_authorized_digest(self):
        import tempfile
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'source').write_text('one')
            before = c.digest_tree(root)
            (root / 'source').write_text('two')
            self.assertNotEqual(before, c.digest_tree(root))

    def test_total_budget_before_spawn(self):
        with patch.object(c, 'DEADLINE', 0), patch.object(c.subprocess, 'Popen') as spawn:
            with self.assertRaises(c.Failure):
                c.run_raw(('/never-run',))
            spawn.assert_not_called()

    def test_real_offline_command_exit_is_sanitized(self):
        with self.assertRaises(c.Failure) as error:
            c._run_process((sys.executable, '-c', 'print("private-output"); raise SystemExit(7)'))
        self.assertEqual(str(error.exception), 'command-exit')

    def test_real_offline_timeout_kills_process_group(self):
        with patch.object(c, 'DEADLINE', c.time.monotonic() + 0.1):
            with self.assertRaises(c.Failure) as error:
                c._run_process((sys.executable, '-c', 'import time; time.sleep(10)'))
        self.assertEqual(str(error.exception), 'command-timeout')

    def test_real_offline_output_bound_refuses_flood(self):
        with self.assertRaises(c.Failure):
            c._run_process((sys.executable, '-c', 'import os; os.write(1, b"x" * 100000)'))

    def test_units_have_no_activation_and_distinct_domains(self):
        root = Path(__file__).with_name('units')
        for role in ('holder', 'worker', 'agent'):
            text = (root / ('cm14-' + role + '.service')).read_text()
            self.assertIn('Restart=no\n', text)
            self.assertIn('KillMode=control-group\n', text)
            self.assertNotIn('[Install]', text)
            self.assertNotIn('Slice=', text)
            self.assertNotIn('Requires=', text)
        anchor = (root / 'cm14-reference.target').read_text()
        self.assertIn('After=cm14-holder.service\n', anchor)
        self.assertNotIn('Wants=', anchor)
        self.assertNotIn('Requires=', anchor)




class PayloadContracts(unittest.TestCase):
    def test_prepare_has_only_fixed_guest_execution_and_firmware(self):
        import importlib.util
        spec = importlib.util.spec_from_file_location('fixture_prepare', Path(__file__).with_name('prepare.py'))
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        config = {'package_update': False, 'package_upgrade': False, 'write_files': [],
                  'runcmd': [['/bin/sh', '-ec', 'fixed-original-ready']]}
        import tempfile
        from test_failures import manifest
        root = Path(__file__).parent
        binding, digest = manifest(root)
        # Synthetic trusted commit backend exercises construction, not actual reviewed authority.
        def committed(argv, **kwargs):
            import subprocess
            name = argv[-1].split(':tooling/policy-session-fixture/')[1]
            return subprocess.CompletedProcess(argv, 0, (root / name).read_bytes())
        with tempfile.NamedTemporaryFile() as file:
            file.write(binding)
            file.flush()
            with patch.object(module.subprocess, 'run', side_effect=committed):
                raw, args = module.prepare('#cloud-config\n' + json.dumps(config), 'a' * 32,
                                           file.name, digest)
        result = json.loads(raw.removeprefix('#cloud-config\n'))
        self.assertEqual(result['runcmd'][0], ['/usr/bin/python3', '-I', '/opt/cm14-fixture/provision.py'])
        self.assertEqual(result['runcmd'][1], config['runcmd'][0])
        self.assertEqual(len(args), 8)
        self.assertEqual(args[::2], ['-fw_cfg'] * 4)
        self.assertTrue(all(item['path'].startswith(('/opt/cm14-fixture/', '/run/cm14-authorization-template', '/run/cm14-source-manifest'))
                            for item in result['write_files']))
        self.assertFalse(result['package_update'])
        with self.assertRaises(Exception):
            module.prepare('#cloud-config\n' + json.dumps(config), 'bad,command')

    def test_static_parse_all_sources(self):
        import ast
        for path in Path(__file__).parent.glob('*.py'):
            with self.subTest(path=path.name):
                ast.parse(path.read_text(), filename=str(path))

    def test_proposed_shared_patch_is_unapplied_and_keeps_ssh_boundary(self):
        diff = Path(__file__).with_name('parent-integration.diff').read_text()
        self.assertNotIn('SMOKE', '\n'.join(line for line in diff.splitlines()
                                           if line.startswith(('+', '-')) and not line.startswith('+++')))
        existing = Path(__file__).resolve().parents[1] / 'debian-fixture/fixture.py'
        self.assertNotIn('policy_fw', existing.read_text())


if __name__ == '__main__':
    unittest.main()
