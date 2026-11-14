import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest

from qualify import canonical, coverage, gate


class GateTests(unittest.TestCase):
    def test_absolute_platform_paths_normalize_without_collapsing_files(self):
        self.assertEqual(canonical(r'C:\agent\src\ChunkMaestro.Persistence\StorageLocation.cs'),
                         'src/ChunkMaestro.Persistence/StorageLocation.cs')
        with self.assertRaises(ValueError):
            canonical('/unrelated/StorageLocation.cs')

    def test_missing_evidence_fails_and_writes_json(self):
        with tempfile.TemporaryDirectory() as root:
            output = Path(root) / 'gate.json'
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertTrue(gate([Path(root) / 'absent'], output))
            self.assertEqual(json.loads(output.read_text())['status'], 'FAIL')

    def test_branch_paths_are_distinct_and_duplicate_lines_are_not_inflated(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root)
            (path / 'coverage.opencover.xml').write_text('''<CoverageSession><Modules><Module>
              <Files><File uid="1" fullPath="/repo/src/ChunkMaestro.Persistence/Test.cs"/></Files>
              <Classes><Class><Methods><Method><Name>M()</Name><SequencePoints>
              <SequencePoint fileid="1" sl="10" vc="0"/><SequencePoint fileid="1" sl="10" vc="2"/>
              </SequencePoints><BranchPoints>
              <BranchPoint fileid="1" offset="1" path="0" offsetend="2" vc="1"/>
              <BranchPoint fileid="1" offset="1" path="1" offsetend="3" vc="0"/>
              </BranchPoints></Method></Methods></Class></Classes></Module></Modules></CoverageSession>''')
            lines, branches = coverage(path)
            self.assertEqual(list(lines.values()), [True])
            self.assertEqual(sorted(branches.values()), [False, True])

    def test_missing_or_ambiguous_collector_report_is_an_error(self):
        with tempfile.TemporaryDirectory() as root:
            with self.assertRaises(ValueError):
                coverage(Path(root))


if __name__ == '__main__':
    unittest.main()
