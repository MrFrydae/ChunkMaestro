"""Complete synthetic artifacts exercise aggregation and denial, not OS acceptance."""
import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import gate
import storage
import test_storage
import test_volume
import volume

HEAD = 'a' * 40


def artifacts(root):
    expected = json.loads(storage.PLAN.read_text())['expected_cases']
    for system in ('Darwin', 'Windows'):
        for group, classes in storage.plan(system).items():
            folder = root / ('storage-' + system + '-' + group)
            folder.mkdir()
            counts = {name: expected[system][name] for name in classes}
            test_storage.trx(folder, counts)
            modules = []
            for module in ('Domain', 'Application', 'Persistence'):
                branch = ('' if module == 'Application' else '<BranchPoints><BranchPoint fileid="1" offset="0" path="0" offsetend="1" vc="1" /></BranchPoints>')
                modules.append('<Module><Files><File uid="1" fullPath="/fixture/src/ChunkMaestro.' + module +
                    '/Example.cs" /></Files><Method><Name>method</Name><SequencePoints>'
                    '<SequencePoint fileid="1" sl="1" vc="1" /></SequencePoints>' + branch + '</Method></Module>')
            (folder / 'coverage.opencover.xml').write_text('<CoverageSession>' + ''.join(modules) + '</CoverageSession>')
            report = {'head': HEAD, 'system': system, 'group': group, 'inputs': 'inputs', 'status': 'PASS',
                      'passed': sum(counts.values()),
                      'files': {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in folder.iterdir()}}
            (folder / 'result.json').write_text(json.dumps(report))
        folder = root / ('volume-' + system)
        folder.mkdir()
        test_volume.VolumeExecutionEvidenceTests().trx(folder)
        (folder / 'volume.json').write_text(json.dumps({'schema_version': 2, 'head': HEAD,
            'platform': system, 'status': 'PASS', 'inputs': volume.input_hashes(),
            'execution': volume.executed_case(folder)}))
    native = root / 'native-Darwin'
    native.mkdir()
    (native / 'execution.json').write_text(json.dumps({'head': HEAD, 'system': 'Darwin', 'inputs': 'inputs'}))
    for name, category, count in (('filesystem.json', 'real-filesystem', 42),
                                 ('injected.json', 'injected-OS-failure-or-metadata', 10)):
        (native / name).write_text(json.dumps({'category': category, 'cases': [
            {'name': str(i), 'passed': True, 'actual': 1, 'expected': 1} for i in range(count)]}))
    (native / 'coverage.json').write_text(json.dumps({'data': [{'files': [
        {'filename': '/fixture/src/ChunkMaestro.Persistence/Native/storage.c'}],
        'totals': {metric: {'count': 1, 'covered': 1} for metric in ('lines', 'branches')}}]}))


class AggregateTests(unittest.TestCase):
    def run_gate(self, root):
        with patch.object(gate, 'digest_inputs', return_value='inputs'):
            return gate.evaluate(root, HEAD, {'storage': 'success', 'volume': 'success', 'native': 'success'})

    def test_complete_raw_execution_passes_without_receipt_or_mutation(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            artifacts(root)
            result = self.run_gate(root)
            self.assertEqual('PASS', result['status'])
            self.assertEqual(549, result['passed'])
            self.assertIsNone(result['metrics']['Application']['branch']['percent'])

    def test_stale_wrong_platform_failed_and_tampered_partition_refused(self):
        for key, value in (('head', 'b' * 40), ('system', 'Darwin'), ('status', 'FAIL'), ('inputs', 'stale')):
            with tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                artifacts(root)
                path = root / 'storage-Windows-authority/result.json'
                report = json.loads(path.read_text())
                report[key] = value
                path.write_text(json.dumps(report))
                with self.assertRaises(ValueError):
                    self.run_gate(root)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            artifacts(root)
            (root / 'storage-Windows-authority/tests.trx').write_text('<changed/>')
            with self.assertRaisesRegex(ValueError, 'changed'):
                self.run_gate(root)

    def test_missing_partition_and_volume_or_zero_case_native_refused(self):
        for target in ('storage-Darwin-authority/result.json', 'volume-Windows/tests/volume.trx',
                       'native-Darwin/execution.json'):
            with tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                artifacts(root)
                (root / target).unlink()
                with self.assertRaises((OSError, ValueError)):
                    self.run_gate(root)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            artifacts(root)
            (root / 'native-Darwin/filesystem.json').write_text(json.dumps({'category': 'real-filesystem', 'cases': []}))
            with self.assertRaisesRegex(ValueError, 'Native fault'):
                self.run_gate(root)

    def test_low_coverage_is_reported_as_real_failure_not_missing_attestation(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            artifacts(root)
            for folder in root.glob('storage-*'):
                coverage = folder / 'coverage.opencover.xml'
                coverage.write_text(coverage.read_text().replace('vc="1"', 'vc="0"'))
                path = folder / 'result.json'
                report = json.loads(path.read_text())
                report['files'][coverage.name] = hashlib.sha256(coverage.read_bytes()).hexdigest()
                path.write_text(json.dumps(report))
            result = self.run_gate(root)
            self.assertEqual('FAIL', result['status'])
            self.assertIn('Persistence line coverage 0.00% below 90%', result['failures'])


if __name__ == '__main__':
    unittest.main()
