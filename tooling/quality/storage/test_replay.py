"""Synthetic refusal/parser contracts only: these are NOT runtime acceptance."""
import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).parent))
import replay


class ReplayContracts(unittest.TestCase):
    def test_utf16_end_exclusive_crlf_and_astral(self):
        source = 'a😀\r\n{\r\n}\n'
        self.assertEqual(5, replay.offset(source, {'line': 2, 'column': 1}))
        self.assertEqual(9, replay.offset(source, {'line': 3, 'column': 2}))
        with self.assertRaises(UnicodeDecodeError):
            replay.offset(source, {'line': 1, 'column': 3})
        with self.assertRaises(ValueError):
            replay.offset(source, {'line': 4, 'column': 2})

    def test_patch_is_exact_one_registration_only(self):
        source = b'prefix\n' + replay.REMOVE + b'suffix\n'
        with patch.object(replay, 'FACTORY_SHA', replay.digest(source)):
            self.assertEqual(b'prefix\nsuffix\n', replay.patch(source))
            with self.assertRaises(ValueError):
                replay.patch(source + b'changed')
        with self.assertRaises(ValueError):
            replay.patch(source)

    def test_catalogue_preserves_duplicates_and_raw_statuses(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder).resolve()
            (root / 'File.cs').write_bytes(b'{}\r\n')
            mutant = {'id': '0', 'status': 'Ignored', 'location': {
                'start': {'line': 1, 'column': 1}, 'end': {'line': 1, 'column': 3}},
                'mutatorName': 'Block removal mutation', 'replacement': '{}'}
            data = {'files': {'File.cs': {'source': '{}\r\n', 'mutants': [mutant, mutant]}}}
            report = root / 'report.json'
            replay.write(report, data)
            rows = replay.catalogue(report, root)
            self.assertEqual(2, len(rows))
            self.assertEqual([0, 2], rows[0]['span_utf16'])
            self.assertEqual('Ignored', rows[1]['original']['status'])
            self.assertEqual(['UNRESOLVED_IDENTITY'] * 2,
                             [r['supplemental_status'] for r in replay.supplement(rows, rows)])
            (root / 'File.cs').write_bytes(b'{}\n')
            with self.assertRaises(ValueError):
                replay.catalogue(report, root)

    def test_supplement_never_credits_timeout_compileerror_or_unattributed_kill(self):
        original = [{'identity': 'a', 'original': {'status': 'Ignored'}, 'report_sha256': 'original'}]
        for status in ['Timeout', 'CompileError', 'Ignored', 'Killed']:
            patched = copy.deepcopy(original)
            patched[0]['original']['status'] = status
            rows = list(replay.supplement(original, patched))
            self.assertTrue(rows[0]['supplemental_status'].startswith('UNRESOLVED_'))
            self.assertEqual('Ignored', rows[0]['original']['status'])
        patched[0]['original']['killedBy'] = ['test-id']
        self.assertEqual('Killed', list(replay.supplement(original, patched))[0]['supplemental_status'])

    def test_missing_or_skipped_baseline_refused(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            with self.assertRaises(ValueError):
                replay.test_cases(root)
            (root / 'result.trx').write_text('<TestRun><Counters total="0" executed="0" passed="0"/></TestRun>')
            with self.assertRaises(ValueError):
                replay.test_cases(root)

    def test_timeout_is_recorded_and_not_success(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            result = replay.command([sys.executable, '-c', 'import time; time.sleep(30)'],
                                    root, root, 'synthetic-timeout', dict(replay.os.environ), 0.05)
            self.assertTrue(result['timeout'])
            with self.assertRaises(ValueError):
                replay.require(result)
            self.assertEqual(1, len((root / 'commands.jsonl').read_text().splitlines()))

    def test_report_path_traversal_refused(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder).resolve()
            report = root / 'report.json'
            replay.write(report, {'files': {'../outside.cs': {'source': '{}', 'mutants': []}}})
            with self.assertRaises(ValueError):
                replay.catalogue(report, root)


if __name__ == '__main__':
    unittest.main()
