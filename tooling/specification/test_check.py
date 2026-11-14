"""Regression tests for the integrity gate, using disposable copies only."""
import importlib.util
import json
import shutil
import tempfile
import unittest
from pathlib import Path

SPEC = importlib.util.spec_from_file_location('spec_check', Path(__file__).with_name('check.py'))
check = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(check)


class IntegrityGateTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        shutil.copytree(check.ROOT / 'docs', self.root / 'docs')
        shutil.copytree(check.ROOT / 'tooling/specification', self.root / 'tooling/specification',
                        ignore=shutil.ignore_patterns('__pycache__'))

    def mutate_row(self, fn):
        path = self.root / check.DATA
        data = json.loads(path.read_text())
        fn(data['requirements'])
        path.write_text(json.dumps(data))

    def assert_rejected(self, expected):
        errors = check.validate(self.root)
        self.assertTrue(any(expected in error for error in errors), errors)

    def test_baseline_and_render_are_valid(self):
        self.assertEqual(check.validate(self.root), [])
        before = (self.root / check.MATRIX).read_bytes()
        self.assertEqual(check.validate(self.root, write=True), [])
        self.assertEqual((self.root / check.MATRIX).read_bytes(), before)

    def test_missing_question_is_rejected(self):
        self.mutate_row(lambda rows: rows.pop())
        self.assert_rejected('exactly Q001–Q205')

    def test_duplicate_question_is_rejected(self):
        self.mutate_row(lambda rows: rows.__setitem__(1, rows[0].copy()))
        self.assert_rejected('exactly Q001–Q205')

    def test_source_change_is_rejected(self):
        with (self.root / 'docs/product/requirements-source.md').open('a') as stream:
            stream.write('\nUnreviewed source change\n')
        self.assert_rejected('source digest mismatch')

    def test_source_line_change_is_rejected(self):
        self.mutate_row(lambda rows: rows[0].update(source_line=999))
        self.assert_rejected('source line mismatch')

    def test_missing_acceptance_is_rejected(self):
        self.mutate_row(lambda rows: rows[0].update(acceptance=''))
        self.assert_rejected('missing concrete acceptance')

    def test_fabricated_implementation_is_rejected(self):
        self.mutate_row(lambda rows: rows[0].update(status='implemented', evidence=['synthetic']))
        self.assert_rejected('bootstrap cannot claim')

    def test_invalid_scope_is_rejected(self):
        self.mutate_row(lambda rows: rows[0].update(scope='universal'))
        self.assert_rejected('unknown scope')

    def test_stale_render_is_rejected(self):
        with (self.root / check.MATRIX).open('a') as stream:
            stream.write('\nDrift\n')
        self.assert_rejected('rendering stale')

    def test_broken_local_link_is_rejected(self):
        with (self.root / 'docs/architecture/README.md').open('a') as stream:
            stream.write('\n[missing](missing.md)\n')
        self.assert_rejected('missing link')

    def test_broken_fragment_is_rejected(self):
        with (self.root / 'docs/architecture/README.md').open('a') as stream:
            stream.write('\n[missing](README.md#nonexistent)\n')
        self.assert_rejected('missing fragment')

    def test_missing_decision_is_rejected(self):
        path = self.root / 'docs/product/open-decisions.md'
        path.write_text(path.read_text().replace('## OD-12 ', '## OMITTED '))
        self.assert_rejected('twelve open decisions')

    def test_missing_adr_section_is_rejected(self):
        path = self.root / 'docs/architecture/adr/0004-helper.md'
        path.write_text(path.read_text().replace('## Failure and recovery', '## Omitted'))
        self.assert_rejected('missing Failure and recovery')


if __name__ == '__main__':
    unittest.main()
