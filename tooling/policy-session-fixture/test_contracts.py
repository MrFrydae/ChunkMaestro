"""Offline refusal/command/store contracts. These never invoke guest services."""
import copy
import importlib.util
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import Mock, patch

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location('fixture_common', Path(__file__).with_name('common.py'))
c = importlib.util.module_from_spec(spec)
spec.loader.exec_module(c)
process_spec = importlib.util.spec_from_file_location('fixture_process', Path(__file__).with_name('process.py'))
p = importlib.util.module_from_spec(process_spec)
with patch.dict(sys.modules, {'common': c}):
    process_spec.loader.exec_module(p)


class ChildExit(BaseException):
    def __init__(self, code):
        self.code = code


class RejectedSenderContracts(unittest.TestCase):
    def test_child_is_alive_when_queued_notification_is_processed(self):
        for access in ('main', 'all'):
            events = []
            state = {'alive': True, 'pending': False, 'attributed': False, 'removed': False}
            def send(message):
                self.assertEqual(message, 'FDSTOREREMOVE=1\nFDNAME=worker-' + 'e' * 32)
                state['pending'] = True
                events.append('send')
            def barrier():
                self.assertTrue(state['pending'])
                self.assertTrue(state['alive'])
                state['attributed'] = state['alive']
                state['removed'] = state['attributed'] and access == 'all'
                events.append('child-barrier')
            def exit_child(code):
                state['alive'] = False
                events.append(('exit', code))
                raise ChildExit(code)
            notify = Mock(send=send, barrier=barrier)
            with self.subTest(access=access), patch.object(p.os, 'fork', return_value=0), \
                    patch.object(p.os, '_exit', side_effect=exit_child), patch.object(p.os, 'waitpid') as wait:
                with self.assertRaises(ChildExit) as exited:
                    p.rejected_sender(notify, 'e' * 32)
                self.assertEqual(exited.exception.code, 0)
                wait.assert_not_called()
            self.assertEqual(events, ['send', 'child-barrier', ('exit', 0)])
            self.assertTrue(state['attributed'])
            self.assertEqual(state['removed'], access == 'all')

    def test_send_or_child_barrier_failure_never_returns_success(self):
        def exit_child(code):
            raise ChildExit(code)
        for stage in ('send', 'barrier'):
            notify = Mock()
            getattr(notify, stage).side_effect = c.Failure('notify-' + stage)
            with self.subTest(stage=stage), patch.object(p.os, 'fork', return_value=0), \
                    patch.object(p.os, '_exit', side_effect=exit_child) as exit_mock, \
                    patch.object(p.os, 'waitpid') as wait:
                with self.assertRaises(ChildExit) as exited:
                    p.rejected_sender(notify, 'e' * 32)
                self.assertEqual(exited.exception.code, 2)
                exit_mock.assert_called_once_with(2)
                wait.assert_not_called()
                if stage == 'send': notify.barrier.assert_not_called()
                else: notify.barrier.assert_called_once_with()

    def test_parent_reaps_exact_child_and_refuses_nonzero_or_signal(self):
        for status in (0, 2 << 8, 15):
            notify = Mock()
            with self.subTest(status=status), patch.object(p.os, 'fork', return_value=321), \
                    patch.object(p.os, 'waitpid', return_value=(321, status)) as wait:
                if status == 0:
                    p.rejected_sender(notify, 'e' * 32)
                else:
                    with self.assertRaisesRegex(c.Failure, '^rejection-send$'):
                        p.rejected_sender(notify, 'e' * 32)
                wait.assert_called_once_with(321, 0)
                notify.send.assert_not_called()
                notify.barrier.assert_not_called()

    def test_fork_and_reap_errors_propagate_without_success(self):
        for stage in ('fork', 'waitpid'):
            notify = Mock()
            with self.subTest(stage=stage), patch.object(p.os, 'fork', return_value=321) as fork, \
                    patch.object(p.os, 'waitpid', return_value=(321, 0)) as wait:
                (fork if stage == 'fork' else wait).side_effect = OSError('synthetic')
                with self.assertRaises(OSError):
                    p.rejected_sender(notify, 'e' * 32)
                notify.send.assert_not_called()
                notify.barrier.assert_not_called()
                if stage == 'fork': wait.assert_not_called()


class GuestProgressContracts(unittest.TestCase):
    def test_guest_guard_and_failure_finalization_boundaries(self):
        import contextlib
        import io
        import base64
        guest_spec = importlib.util.spec_from_file_location('diagnostic_guest', Path(__file__).with_name('guest.py'))
        guest = importlib.util.module_from_spec(guest_spec)
        with patch.dict(sys.modules, {'common': c}):
            guest_spec.loader.exec_module(guest)
        identity = {'nonce': 'a' * 32, 'source_sha256': 'b' * 64}
        for guarded in (False, True):
            with contextlib.ExitStack() as stack:
                stack.enter_context(patch.object(sys, 'argv', ['guest.py']))
                stack.enter_context(patch.object(guest, 'guard', side_effect=None if guarded else c.Failure('guard'),
                                               return_value=identity))
                owner = stack.enter_context(patch.object(guest, 'OwnedState'))
                stack.enter_context(patch.object(guest, 'Cancellation'))
                stack.enter_context(patch.object(guest, 'Notify', side_effect=RuntimeError('PRIVATE_SENTINEL')))
                final = stack.enter_context(patch.object(guest, 'finalize'))
                output = stack.enter_context(contextlib.redirect_stdout(io.StringIO()))
                if guarded:
                    self.assertEqual(guest.main(), 1)
                    final.assert_called_once()
                    self.assertEqual(final.call_args.args[0]['primary_failure'], 'runtime-unavailable')
                else:
                    with self.assertRaises(c.Failure): guest.main()
                    owner.assert_not_called(); final.assert_not_called()
            records = [json.loads(base64.b64decode(line.split(':', 1)[1]))
                       for line in output.getvalue().splitlines()]
            self.assertEqual([record['phase'] for record in records],
                             ['guest-entered', 'campaign-finalizing'] if guarded else [])
            self.assertNotIn('PRIVATE_SENTINEL', output.getvalue())
            for record in records:
                self.assertEqual(set(record), {'schema', 'nonce', 'source_sha256', 'phase'})
                self.assertEqual(record['nonce'], identity['nonce'])
                self.assertEqual(record['source_sha256'], identity['source_sha256'])
        with self.assertRaisesRegex(c.Failure, 'progress-phase'):
            guest.progress(identity, 'PRIVATE_SENTINEL')


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

    def test_show_includes_empty_values_without_defaulting_absent_properties(self):
        for unit in c.UNITS:
            expected = ('/usr/bin/systemctl', 'show', '--all', unit)
            self.assertEqual(c.command('show', unit), expected)
            with patch.object(c, '_run_process', return_value='MainPID=0\nInvocationID=\nControlGroup=\nJob=0\n') as run:
                props = c.properties(unit)
                run.assert_called_once_with(expected, (0, 1, 4))
                self.assertEqual(props['InvocationID'], '')
                self.assertEqual(props['ControlGroup'], '')
            with patch.object(c, '_run_process', return_value='MainPID=0\n'):
                with self.assertRaises(KeyError):
                    c.properties(unit)['InvocationID']

    def test_show_all_keeps_fixed_targets_flags_and_exit_policy(self):
        for argv in (('/usr/bin/systemctl', 'show', c.UNITS[0]),
                     ('/usr/bin/systemctl', 'show', '--all', 'minecraft.service'),
                     ('/usr/bin/systemctl', 'show', '--all', '--value', c.UNITS[0]),
                     ('/usr/bin/systemctl', 'start', '--all', c.UNITS[0])):
            with self.subTest(argv=argv), patch.object(c, '_run_process') as run:
                with self.assertRaises(c.Failure): c.run_raw(argv, (0, 1, 4))
                run.assert_not_called()
        with patch.object(c, '_run_process') as run:
            with self.assertRaises(c.Failure): c.run_raw(c.command('show', c.UNITS[0]), (0,))
            run.assert_not_called()

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
        import base64
        import io
        serial = io.BytesIO()
        from contextlib import nullcontext
        self.assertEqual(result['bootcmd'][0][:3], ['/usr/bin/python3', '-I', '-c'])
        with patch('builtins.open', return_value=nullcontext(serial)) as opened:
            exec(compile(result['bootcmd'][0][3], '<offline-boot-marker>', 'exec'), {})
        opened.assert_called_once_with('/dev/ttyS0', 'wb', buffering=0)
        record = json.loads(base64.b64decode(serial.getvalue().strip().removeprefix(b'CM14-BOOT:')))
        self.assertEqual(record, {'schema': 1, 'nonce': 'a' * 32,
            'source_sha256': digest, 'phase': 'cloud-init-bootcmd'})
        self.assertEqual(len(args), 8)
        self.assertEqual(args[::2], ['-fw_cfg'] * 4)
        self.assertTrue(all(item['path'].startswith(('/opt/cm14-fixture/', '/run/cm14-authorization-template', '/run/cm14-source-manifest'))
                            for item in result['write_files']))
        self.assertFalse(result['package_update'])
        with self.assertRaises(Exception):
            module.prepare('#cloud-config\n' + json.dumps(config), 'bad,command')

    def test_provision_progress_is_fixed_and_import_does_not_launch(self):
        import base64
        import io
        spec = importlib.util.spec_from_file_location('offline_provision', Path(__file__).with_name('provision.py'))
        module = importlib.util.module_from_spec(spec)
        with patch('subprocess.run') as run:
            spec.loader.exec_module(module)
            run.assert_not_called()
            binding = {'nonce': 'a' * 32, 'source_sha256': 'b' * 64}
            serial = io.BytesIO()
            for phase in ('provision-entered', 'template-bound', 'authorization-written',
                          'guard-passed', 'guest-returned', 'provision-failed', 'guest-timeout'):
                module.progress(serial, binding, phase)
            records = [json.loads(base64.b64decode(line.removeprefix(b'CM14-BOOT:')))
                       for line in serial.getvalue().splitlines() if line]
            self.assertEqual(len(records), 7)
            self.assertTrue(all(set(record) == {'schema', 'nonce', 'source_sha256', 'phase'}
                and record['nonce'] == binding['nonce'] and record['source_sha256'] == binding['source_sha256']
                for record in records))
            with self.assertRaises(module.Failure):
                module.progress(serial, binding, 'PRIVATE_SENTINEL')
            from unittest.mock import Mock
            for written in (0, 1, None):
                with self.subTest(short_write=written), self.assertRaises(module.Failure):
                    module.progress(Mock(write=Mock(return_value=written)), binding, 'provision-entered')
            with patch.object(module.platform, 'system', return_value='Darwin'), \
                    patch('builtins.open') as opened, patch.object(module, 'run') as child:
                with self.assertRaises(module.Failure): module.main()
                opened.assert_not_called(); child.assert_not_called()

    def test_provision_failure_after_entry_retains_stage_without_spawning(self):
        import base64
        import io
        spec = importlib.util.spec_from_file_location('offline_provision_failure', Path(__file__).with_name('provision.py'))
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        serial = io.BytesIO()
        with patch.object(module, 'trusted', side_effect=module.Failure('template-refused')), \
                patch.object(module.subprocess, 'run') as run:
            with self.assertRaises(module.Failure):
                module.run(serial, {'nonce': 'a' * 32, 'source_sha256': 'b' * 64})
            run.assert_not_called()
        record = json.loads(base64.b64decode(serial.getvalue().strip().removeprefix(b'CM14-BOOT:')))
        self.assertEqual(record['phase'], 'provision-entered')

    def test_provision_main_reports_only_fixed_failure_or_timeout_after_binding(self):
        import base64
        import io
        import tempfile
        import subprocess
        from contextlib import nullcontext
        spec = importlib.util.spec_from_file_location('offline_provision_main', Path(__file__).with_name('provision.py'))
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            files = {'/sys/class/dmi/id/sys_vendor': 'QEMU',
                     '/sys/firmware/qemu_fw_cfg/by_name/opt/cm14/accelerator/raw': 'tcg',
                     '/sys/firmware/qemu_fw_cfg/by_name/opt/cm14/nonce/raw': 'a' * 32,
                     '/sys/firmware/qemu_fw_cfg/by_name/opt/cm14/source-sha256/raw': 'b' * 64}
            for name, value in files.items():
                path = root / name.lstrip('/'); path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(value)
            for error, phase in ((module.Failure('PRIVATE_SENTINEL'), 'provision-failed'),
                    (subprocess.TimeoutExpired('PRIVATE_SENTINEL', 160), 'guest-timeout')):
                serial = io.BytesIO()
                with patch.object(module.sys, 'argv', ['provision.py']), \
                        patch.object(module.platform, 'system', return_value='Linux'), \
                        patch.object(module.os, 'geteuid', return_value=0), \
                        patch.object(module, 'Path', side_effect=lambda value: root / str(value).lstrip('/')), \
                        patch('builtins.open', return_value=nullcontext(serial)) as opened, \
                        patch.object(module, 'run', side_effect=error) as run:
                    self.assertEqual(module.main(), 3)
                    opened.assert_called_once_with('/dev/ttyS0', 'wb', buffering=0)
                    run.assert_called_once_with(serial, {'nonce': 'a' * 32, 'source_sha256': 'b' * 64})
                self.assertNotIn(b'PRIVATE_SENTINEL', serial.getvalue())
                record = json.loads(base64.b64decode(serial.getvalue().strip().removeprefix(b'CM14-BOOT:')))
                self.assertEqual(record['phase'], phase)

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
