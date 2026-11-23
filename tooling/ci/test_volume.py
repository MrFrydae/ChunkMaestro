import unittest
import io
import json
import os
from pathlib import Path
import plistlib
import subprocess
import tempfile
from types import SimpleNamespace
from unittest.mock import Mock, patch
import xml.etree.ElementTree as ET

import volume
from volume import detach_mac_image, free_drive_letter, mac_image_device


class VolumeExecutionEvidenceTests(unittest.TestCase):
    """Synthetic execution and environment contracts; never real volume qualification."""

    def trx(self, folder):
        tests = folder / 'tests'
        tests.mkdir(parents=True, exist_ok=True)
        document = ET.Element('TestRun')
        summary = ET.SubElement(document, 'ResultSummary')
        ET.SubElement(summary, 'Counters', total='1', executed='1', passed='1', failed='0',
                      error='0', timeout='0', aborted='0', inconclusive='0', notExecuted='0')
        name = volume.TEST_CLASS + '.' + volume.TEST_METHOD
        definitions = ET.SubElement(document, 'TestDefinitions')
        definition = ET.SubElement(definitions, 'UnitTest', id='fixture-id', name=name)
        ET.SubElement(definition, 'TestMethod', className=volume.TEST_CLASS, name=volume.TEST_METHOD)
        results = ET.SubElement(document, 'Results')
        ET.SubElement(results, 'UnitTestResult', testId='fixture-id', testName=name, outcome='Passed')
        path = tests / 'volume.trx'
        ET.ElementTree(document).write(path, encoding='utf-8', xml_declaration=True)
        return path

    def test_raw_execution_binding_rejects_missing_skipped_wrong_identity_or_changed_evidence(self):
        for defect in ('none', 'missing', 'duplicate', 'skipped', 'failed', 'wrong-class', 'wrong-method',
                       'wrong-id', 'zero', 'missing-counter', 'oversized', 'inputs', 'hash', 'legacy', 'stale'):
            with self.subTest(defect=defect), tempfile.TemporaryDirectory() as temporary:
                folder = Path(temporary)
                path = self.trx(folder)
                record = {'schema_version': 2, 'status': 'PASS', 'head': 'a' * 40, 'platform': 'Windows',
                          'inputs': volume.input_hashes(), 'execution': volume.executed_case(folder)}
                document = ET.parse(path)
                if defect == 'missing': path.unlink()
                elif defect == 'duplicate': (folder / 'tests/another.trx').write_bytes(path.read_bytes())
                elif defect in ('skipped', 'failed'):
                    document.find('.//UnitTestResult').set('outcome', 'NotExecuted' if defect == 'skipped' else 'Failed')
                    document.write(path)
                elif defect in ('wrong-class', 'wrong-method'):
                    document.find('.//TestMethod').set('className' if defect == 'wrong-class' else 'name', 'Unrelated')
                    document.write(path)
                elif defect == 'wrong-id':
                    document.find('.//UnitTestResult').set('testId', 'different')
                    document.write(path)
                elif defect == 'zero':
                    document.find('.//Counters').set('executed', '0')
                    document.write(path)
                elif defect == 'missing-counter':
                    del document.find('.//Counters').attrib['failed']
                    document.write(path)
                elif defect == 'oversized': path.write_bytes(b' ' * (volume.MAX_TRX_BYTES + 1))
                elif defect == 'inputs': record['inputs'][volume.INPUTS[0]] = 'b' * 64
                elif defect == 'hash': record['execution']['sha256'] = 'b' * 64
                elif defect == 'legacy': record['schema_version'] = 1
                elif defect == 'stale': record['head'] = 'b' * 40
                (folder / 'volume.json').write_text(json.dumps(record))
                if defect == 'none':
                    self.assertEqual(record, volume.validate_evidence(folder, 'a' * 40, 'Windows'))
                else:
                    with self.assertRaises((ValueError, ET.ParseError)):
                        volume.validate_evidence(folder, 'a' * 40, 'Windows')

    def test_aggregate_rechecks_raw_volume_case_instead_of_accepting_status_only(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            (folder / 'volume').mkdir()
            (folder / 'volume/volume.json').write_text(json.dumps(
                {'schema_version': 2, 'status': 'PASS', 'head': 'a' * 40, 'platform': 'Windows',
                 'inputs': volume.input_hashes(), 'execution': {'case': volume.TEST_CLASS + '.' + volume.TEST_METHOD,
                    'trx': 'tests/missing.trx', 'sha256': 'b' * 64, 'passed': 1}}))
            with self.assertRaises(ValueError):
                volume.validate_evidence(folder / 'volume', 'a' * 40, 'Windows')

    def test_collection_filters_credentials_and_zero_exit_without_trx_cannot_pass(self):
        for executed in (False, True):
            with self.subTest(executed=executed), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                scratch = root / 'scratch'
                scratch.mkdir()
                output = root / 'evidence'
                calls = []
                def run(args, **kwargs):
                    env = kwargs.get('env')
                    self.assertIsNotNone(env)
                    for secret in ('GH_TOKEN', 'ACTIONS_RUNTIME_TOKEN', 'AZURE_CLIENT_SECRET'):
                        self.assertNotIn(secret, env)
                    self.assertGreater(kwargs['timeout'], 0)
                    if len(args) > 1 and args[1] in ('restore', 'build', 'test'):
                        calls.append(args[1])
                        if args[1] == 'test':
                            self.assertIn('CHUNKMAESTRO_DISPOSABLE_VOLUME', env)
                            if executed: self.trx(output)
                    if args[0] == 'mountvol':
                        return SimpleNamespace(returncode=0,
                            stdout='\\\\?\\Volume{00000000-0000-0000-0000-000000000000}\\', stderr='')
                    if args[0] == 'pwsh' and '-File' in args:
                        return SimpleNamespace(returncode=0, stdout=json.dumps({
                            'image_path': str(Path(args[args.index('-ImagePath') + 1]).resolve()), 'disk_number': 7,
                            'drive_root': args[args.index('-DriveLetter') + 1] + ':\\',
                            'volume_guid': '\\\\?\\Volume{00000000-0000-0000-0000-000000000000}\\'}), stderr='')
                    return SimpleNamespace(returncode=0, stdout='', stderr='')
                with patch.dict(os.environ, {'GH_TOKEN': 'synthetic-only', 'ACTIONS_RUNTIME_TOKEN': 'synthetic-only',
                                             'AZURE_CLIENT_SECRET': 'synthetic-only'}, clear=True), \
                     patch.object(volume.platform, 'system', return_value='Windows'), \
                     patch.object(volume, 'occupied_drives', return_value=['C:\\']), \
                     patch.object(volume.tempfile, 'mkdtemp', return_value=str(scratch)), \
                     patch.object(volume.subprocess, 'check_output', return_value='a' * 40), \
                     patch.object(volume, 'run_test', side_effect=lambda args, cwd, env, log:
                                  run(args, cwd=cwd, env=env, timeout=120).returncode), \
                     patch.object(volume.subprocess, 'run', side_effect=run):
                    self.assertEqual(not executed, volume.run(output))
                self.assertEqual(['restore', 'build', 'test'], calls)
                record = json.loads((output / 'volume.json').read_bytes())
                self.assertEqual('PASS' if executed else 'FAIL', record['status'])
                if executed: volume.validate_evidence(output, 'a' * 40, 'Windows')

    def test_foreign_drive_or_image_binding_refuses_before_volume_writes(self):
        with tempfile.TemporaryDirectory() as temporary:
            image = Path(temporary) / 'fixture.vhd'
            guid = '\\\\?\\Volume{00000000-0000-0000-0000-000000000000}\\'
            expected = {'image_path': str(image.resolve()), 'disk_number': 7,
                        'drive_root': 'Z:\\', 'volume_guid': guid}
            for field, value in [('image_path', 'foreign.vhd'), ('drive_root', 'C:\\'),
                                 ('volume_guid', guid.replace('00000000', '11111111', 1)), ('disk_number', True)]:
                with self.subTest(field=field), patch.object(volume.subprocess, 'run',
                        return_value=SimpleNamespace(returncode=0, stdout=json.dumps(dict(expected, **{field: value})), stderr='')):
                    with self.assertRaises(RuntimeError):
                        volume.windows_image_binding(image, Path('Z:\\'), guid, io.StringIO(), {})

    def test_failed_or_interrupted_test_retains_attached_image_without_detaching(self):
        for failure in (1, subprocess.TimeoutExpired(['dotnet', 'test'], 120)):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                scratch = root / 'scratch'
                scratch.mkdir()
                output = root / 'evidence'
                with patch.object(volume.platform, 'system', return_value='Darwin'), \
                     patch.object(volume.tempfile, 'mkdtemp', return_value=str(scratch)), \
                     patch.object(volume.subprocess, 'check_output', return_value='a' * 40), \
                     patch.object(volume.subprocess, 'run', return_value=SimpleNamespace(returncode=0)), \
                     patch.object(volume.os.path, 'ismount', return_value=True), \
                     patch.object(volume, 'run_test', side_effect=failure if isinstance(failure, Exception) else None,
                                  return_value=failure) as execute, \
                     patch.object(volume, 'detach_mac_image') as detach:
                    self.assertTrue(volume.run(output))
                execute.assert_called_once()
                detach.assert_not_called()
                self.assertTrue(scratch.exists())
                result = json.loads((output / 'volume.json').read_bytes())
                self.assertEqual('FAIL', result['status'])
                self.assertIn('completion is unconfirmed', result['cleanup_error'])
                self.assertEqual(str(scratch), result['retained_fixture'])

    def test_timeout_terminates_only_owned_test_tree_with_bounded_wait_and_preserves_error(self):
        for system in ('nt', 'posix'):
            with self.subTest(system=system):
                timeout = subprocess.TimeoutExpired(['dotnet', 'test'], 120)
                process = Mock(pid=12345)
                process.wait.side_effect = [timeout, 1]
                log = io.StringIO()
                fixture = Path('/fixture')
                with patch.object(volume.os, 'name', system), \
                     patch.object(volume.signal, 'SIGKILL', 9, create=True), \
                     patch.object(volume.subprocess, 'Popen', return_value=process) as start, \
                     patch.object(volume.subprocess, 'run') as taskkill, \
                     patch.object(volume.os, 'killpg', create=True) as kill:
                    with self.assertRaises(subprocess.TimeoutExpired) as raised:
                        volume.run_test(['dotnet', 'test'], fixture, {'PATH': 'fixture'}, log)
                self.assertIs(timeout, raised.exception)
                self.assertEqual([120, 15], [call.kwargs['timeout'] for call in process.wait.call_args_list])
                self.assertEqual(system != 'nt', start.call_args.kwargs['start_new_session'])
                if system == 'nt':
                    taskkill.assert_called_once_with(['taskkill', '/PID', '12345', '/T', '/F'],
                        env={'PATH': 'fixture'}, stdout=log, stderr=subprocess.STDOUT, timeout=15)
                    kill.assert_not_called()
                else:
                    kill.assert_called_once_with(12345, 9)
                    taskkill.assert_not_called()

    def test_tree_termination_failure_does_not_mask_original_timeout(self):
        timeout = subprocess.TimeoutExpired(['dotnet', 'test'], 120)
        process = Mock(pid=12345)
        process.wait.side_effect = timeout
        log = io.StringIO()
        fixture = Path('/fixture')
        with patch.object(volume.os, 'name', 'nt'), \
             patch.object(volume.subprocess, 'Popen', return_value=process), \
             patch.object(volume.subprocess, 'run', side_effect=OSError('synthetic termination failure')):
            with self.assertRaises(subprocess.TimeoutExpired) as raised:
                volume.run_test(['dotnet', 'test'], fixture, {}, log)
        self.assertIs(timeout, raised.exception)
        self.assertIn('synthetic termination failure', log.getvalue())


class DriveSelectionTests(unittest.TestCase):
    def test_native_drive_mask_includes_every_occupied_letter(self):
        self.assertEqual(['C:\\', 'Z:\\'], volume.occupied_drives(lambda: (1 << 2) | (1 << 25)))
        self.assertEqual('Y', free_drive_letter(volume.occupied_drives(lambda: (1 << 2) | (1 << 25))))
        for mask in (0, -1, 1 << 26, None, True):
            with self.subTest(mask=mask), self.assertRaises(RuntimeError):
                volume.occupied_drives(lambda: mask)

    def info(self, image='/tmp/owned/fixture.dmg', mount='/tmp/owned/mount', device='/dev/disk42'):
        return {'images': [{'image-path': image, 'system-entities': [
            {'dev-entry': device}, {'dev-entry': device + 's1', 'mount-point': mount}]}]}

    def test_busy_mac_cleanup_forces_only_reverified_owned_image(self):
        for busy in (False, True):
            with self.subTest(busy=busy):
                commands = []
                queries = iter([self.info(), self.info(), {'images': []}] if busy else [self.info(), {'images': []}])
                def execute(args, **kwargs):
                    commands.append(args)
                    if args[1] == 'info':
                        return SimpleNamespace(returncode=0, stdout=plistlib.dumps(next(queries)))
                    return SimpleNamespace(returncode=16 if busy and '-force' not in args else 0)
                with patch('volume.os.path.ismount', return_value=False):
                    detach_mac_image(Path('/tmp/owned/fixture.dmg'), Path('/tmp/owned/mount'), io.StringIO(), execute)
                self.assertEqual([c for c in commands if '-force' in c],
                                 [['hdiutil', 'detach', '-force', '/dev/disk42']] if busy else [])

    def test_mac_cleanup_rejects_changed_identity_other_mount_and_unrelated_image(self):
        image, mount = Path('/tmp/owned/fixture.dmg'), Path('/tmp/owned/mount')
        for info in (self.info(mount='/tmp/other'), self.info(device='/dev/physical'),
                     {'images': self.info()['images'] * 2}):
            with self.assertRaises(ValueError):
                mac_image_device(info, image, mount)
        for second in (self.info(device='/dev/disk43'), self.info(mount='/tmp/other')):
            commands = []
            queries = iter([self.info(), second])
            def execute(args, **kwargs):
                commands.append(args)
                return SimpleNamespace(returncode=0 if args[1] == 'info' else 16,
                                       stdout=plistlib.dumps(next(queries)) if args[1] == 'info' else b'')
            with self.assertRaises((ValueError, RuntimeError)):
                detach_mac_image(image, mount, io.StringIO(), execute)
            self.assertFalse(any('-force' in c for c in commands))
        commands = []
        def unrelated(args, **kwargs):
            commands.append(args)
            return SimpleNamespace(returncode=0, stdout=plistlib.dumps(self.info(image='/tmp/unrelated.dmg')))
        with patch('volume.os.path.ismount', return_value=True), self.assertRaises(RuntimeError):
            detach_mac_image(image, mount, io.StringIO(), unrelated)
        self.assertTrue(all(c[1] == 'info' for c in commands))

    def test_busy_mac_eject_can_finish_after_its_own_detach_unmounts_the_fixture(self):
        image, mount = Path('/tmp/owned/fixture.dmg'), Path('/tmp/owned/mount')
        unmounted = self.info()
        del unmounted['images'][0]['system-entities'][1]['mount-point']
        with self.assertRaisesRegex(ValueError, 'mount identity'):
            mac_image_device(unmounted, image, mount)
        commands = []
        queries = iter([self.info(), unmounted, {'images': []}])
        def execute(args, **kwargs):
            commands.append(args)
            if args[1] == 'info':
                return SimpleNamespace(returncode=0, stdout=plistlib.dumps(next(queries)))
            return SimpleNamespace(returncode=0 if '-force' in args else 16)
        with patch('volume.os.path.ismount', return_value=False):
            detach_mac_image(image, mount, io.StringIO(), execute)
        self.assertEqual([command for command in commands if command[1] == 'detach'], [
            ['hdiutil', 'detach', '/dev/disk42'], ['hdiutil', 'detach', '-force', '/dev/disk42']])

    def test_unmounted_busy_cleanup_still_refuses_replaced_device_or_live_mount(self):
        image, mount = Path('/tmp/owned/fixture.dmg'), Path('/tmp/owned/mount')
        for device, mounted in (('/dev/disk42', True), ('/dev/disk43', False)):
            unmounted = self.info(device=device)
            del unmounted['images'][0]['system-entities'][1]['mount-point']
            queries = iter([self.info(), unmounted])
            commands = []
            def execute(args, **kwargs):
                commands.append(args)
                return SimpleNamespace(returncode=0 if args[1] == 'info' else 16,
                    stdout=plistlib.dumps(next(queries)) if args[1] == 'info' else b'')
            with self.subTest(device=device, mounted=mounted), \
                 patch('volume.os.path.ismount', return_value=mounted), \
                 self.assertRaises((ValueError, RuntimeError)):
                detach_mac_image(image, mount, io.StringIO(), execute)
            self.assertFalse(any('-force' in command for command in commands))

    def test_mac_cleanup_never_forces_an_unexplained_error_or_accepts_failed_force(self):
        for code in (1, 16):
            commands = []
            def execute(args, **kwargs):
                commands.append(args)
                return SimpleNamespace(returncode=0 if args[1] == 'info' else code,
                                       stdout=plistlib.dumps(self.info()) if args[1] == 'info' else b'')
            with self.assertRaises(RuntimeError):
                detach_mac_image(Path('/tmp/owned/fixture.dmg'), Path('/tmp/owned/mount'), io.StringIO(), execute)
            self.assertEqual(sum('-force' in c for c in commands), 1 if code == 16 else 0)

    def test_occupied_drive_is_never_selected_even_with_lowercase_inventory(self):
        self.assertEqual(free_drive_letter(['c:\\', 'z:\\']), 'Y')
        self.assertEqual(free_drive_letter('C:\\'), 'Z')

    def test_no_letter_or_invalid_inventory_fails_before_disk_commands(self):
        for inventory in ([], None, 12, ['not-a-drive'], ['C:\\', 42],
                          [letter + ':\\' for letter in 'DEFGHIJKLMNOPQRSTUVWXYZ']):
            with self.subTest(inventory=inventory):
                with self.assertRaises(ValueError):
                    free_drive_letter(inventory)
