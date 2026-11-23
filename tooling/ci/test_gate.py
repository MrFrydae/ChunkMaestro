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
from evidence import inventory

HEAD = 'a' * 40


def artifacts(root):
    expected = json.loads(storage.PLAN.read_text())['expected_cases']
    for system in ('Darwin', 'Windows'):
        for group, classes in storage.plan(system).items():
            folder = root / ('storage-' + system + '-' + group)
            folder.mkdir()
            counts = {name: expected[system][name] for name in classes}
            test_storage.trx(folder, counts)
            names = [name + '-' + str(i) for name, count in counts.items() for i in range(count)]
            (folder / 'case-inventory.json').write_text(json.dumps({
                'head': HEAD, 'system': system, 'group': group, 'cases': names}))
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
    native_plan = json.loads((storage.PLAN.parent / 'native-plan.json').read_text())
    for name, category, count in (('filesystem.json', 'real-filesystem', 42),
                                 ('injected.json', 'injected-OS-failure-or-metadata', 10)):
        (native / name).write_text(json.dumps({'category': category, 'cases': [
            {'name': label, 'passed': True, 'actual': 1, 'expected': 1} for label in native_plan[name]]}))
    (native / 'coverage.json').write_text(json.dumps({'data': [{'files': [
        {'filename': '/fixture/src/ChunkMaestro.Persistence/Native/storage.c'}],
        'totals': {metric: {'count': 1, 'covered': 1} for metric in ('lines', 'branches')}}]}))
    (native / 'execution.json').write_text(json.dumps({'head': HEAD, 'system': 'Darwin', 'inputs': 'inputs',
        'files': inventory(native, exclude=('execution.json',))}))


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
            self.rebind_native(root)
            with self.assertRaisesRegex(ValueError, 'Native fault'):
                self.run_gate(root)

    def rebind_native(self, root):
        native = root / 'native-Darwin'
        path = native / 'execution.json'
        data = json.loads(path.read_text())
        data['files'] = inventory(native, exclude=('execution.json',))
        path.write_text(json.dumps(data))

    def test_unrecorded_coverage_cannot_override_verified_reports(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            artifacts(root)
            folder = root / 'storage-Windows-integrity'
            extra = folder / 'unrecorded'
            extra.mkdir()
            (extra / 'coverage.opencover.xml').write_bytes((folder / 'coverage.opencover.xml').read_bytes())
            with self.assertRaisesRegex(ValueError, 'unrecorded'):
                self.run_gate(root)

    def test_native_raw_changes_and_rebound_renamed_cases_refused(self):
        for name in ('filesystem.json', 'injected.json', 'coverage.json'):
            with tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                artifacts(root)
                path = root / 'native-Darwin' / name
                path.write_text(path.read_text() + ' ')
                with self.assertRaisesRegex(ValueError, 'changed'):
                    self.run_gate(root)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            artifacts(root)
            path = root / 'native-Darwin/filesystem.json'
            data = json.loads(path.read_text())
            data['cases'][0]['name'] = 'unreviewed-case'
            path.write_text(json.dumps(data))
            self.rebind_native(root)
            with self.assertRaisesRegex(ValueError, 'Native fault'):
                self.run_gate(root)

    def test_malformed_native_coverage_produces_machine_fail_report(self):
        for malformed in ({'data': []}, {'data': [None]}, {'data': [{'files': None}]},
                          {'data': [{'files': [{'filename': '/fixture/src/ChunkMaestro.Persistence/Native/storage.c'}],
                                     'totals': {'lines': {'count': 0, 'covered': 0}}}]}):
            with tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                artifacts(root)
                (root / 'native-Darwin/coverage.json').write_text(json.dumps(malformed))
                self.rebind_native(root)
                result = root / 'verdict.json'
                args = ['gate.py', str(root), '--head', HEAD, '--jobs', json.dumps({
                    name: {'result': 'success'} for name in ('storage', 'volume', 'native')}), '--output', str(result)]
                with patch.object(gate.sys, 'argv', args), patch.object(gate, 'digest_inputs', return_value='inputs'):
                    self.assertEqual(1, gate.main())
                self.assertEqual('FAIL', json.loads(result.read_text())['status'])

    def test_low_managed_and_native_coverage_is_reported_without_percentage_gate(self):
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
            native = root / 'native-Darwin/coverage.json'
            data = json.loads(native.read_text())
            for metric in data['data'][0]['totals'].values():
                metric['covered'] = 0
            native.write_text(json.dumps(data))
            self.rebind_native(root)
            result = self.run_gate(root)
            self.assertEqual('PASS', result['status'])
            self.assertEqual([], result['failures'])
            self.assertEqual({'covered': 0, 'total': 1, 'percent': 0.0},
                             result['metrics']['Persistence']['line'])
            self.assertEqual(0.0, result['metrics']['Persistence']['branch']['percent'])
            self.assertEqual(0.0, result['native_metrics']['lines']['percent'])
            self.assertEqual(0.0, result['native_metrics']['branches']['percent'])

    def test_missing_coverage_remains_a_failure_with_diagnostic_metrics(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            artifacts(root)
            for folder in root.glob('storage-*'):
                coverage = folder / 'coverage.opencover.xml'
                coverage.write_text('<CoverageSession/>')
                path = folder / 'result.json'
                report = json.loads(path.read_text())
                report['files'][coverage.name] = hashlib.sha256(coverage.read_bytes()).hexdigest()
                path.write_text(json.dumps(report))
            result = self.run_gate(root)
            self.assertEqual('FAIL', result['status'])
            self.assertIn('Persistence missing line coverage', result['failures'])

    def test_unsuccessful_test_jobs_are_never_replaced_by_diagnostic_scores(self):
        for status in ('failure', 'cancelled', 'skipped'):
            with self.subTest(status=status), self.assertRaisesRegex(ValueError, 'Failed prerequisite jobs'):
                gate.evaluate(Path('unused'), HEAD, {'storage': status})


if __name__ == '__main__':
    unittest.main()
