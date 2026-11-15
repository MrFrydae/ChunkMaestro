import unittest
import io
from pathlib import Path
import plistlib
from types import SimpleNamespace
from unittest.mock import patch

from volume import detach_mac_image, free_drive_letter, mac_image_device


class DriveSelectionTests(unittest.TestCase):
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
