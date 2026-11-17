import json
from pathlib import Path
import tempfile
import unittest

from summary import describe


class SummaryTests(unittest.TestCase):
    def test_incomplete_mutation_explains_timeout_and_retained_log_without_credit(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            (folder / 'replay').mkdir()
            (folder / 'replay/result.json').write_text(json.dumps({'status': 'UNRESOLVED', 'error': 'collection incomplete'}), encoding='utf-8')
            (folder / 'replay/commands.jsonl').write_text(json.dumps({'timeout': True, 'timeout_reason': 'log-inactivity',
                'exit_code': -9, 'seconds': 600.5, 'last_log_update_age_seconds': 600, 'log': r'C:\temp\official.log'})+'\n', encoding='utf-8')
            text = describe(folder)
            for expected in ('UNRESOLVED', 'log-inactivity', '600.5', 'official.log', 'No timeout kill credit'):
                self.assertIn(expected, text)
            self.assertNotIn('C:\\temp', text)

    def test_missing_results_never_imply_qualification_pass(self):
        with tempfile.TemporaryDirectory() as temporary:
            text = describe(Path(temporary))
            self.assertNotIn('**PASS**', text)
            self.assertIn('not full safety qualification', text)
            self.assertIn('storage-qualify', text)
