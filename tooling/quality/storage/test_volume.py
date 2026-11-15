import unittest

from volume import free_drive_letter


class DriveSelectionTests(unittest.TestCase):
    def test_occupied_drive_is_never_selected_even_with_lowercase_inventory(self):
        self.assertEqual(free_drive_letter(['c:\\', 'z:\\']), 'Y')
        self.assertEqual(free_drive_letter('C:\\'), 'Z')

    def test_no_letter_or_invalid_inventory_fails_before_disk_commands(self):
        for inventory in ([], None, 12, ['not-a-drive'], ['C:\\', 42],
                          [letter + ':\\' for letter in 'DEFGHIJKLMNOPQRSTUVWXYZ']):
            with self.subTest(inventory=inventory):
                with self.assertRaises(ValueError):
                    free_drive_letter(inventory)
