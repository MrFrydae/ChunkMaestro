"""Synthetic denial tests for the direct CI gate; not product qualification."""
import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET
from evidence import read_json, inventory, verify_inventory

import command
import gate
import storage


def trx(folder, counts, outcome='Passed'):
    document = ET.Element('TestRun')
    definitions = ET.SubElement(document, 'TestDefinitions')
    results = ET.SubElement(document, 'Results')
    for name, count in counts.items():
        for index in range(count):
            identity = name + '-' + str(index)
            definition = ET.SubElement(definitions, 'UnitTest', id=identity)
            ET.SubElement(definition, 'TestMethod', className='ChunkMaestro.PersistenceTests.' + name)
            ET.SubElement(results, 'UnitTestResult', testId=identity, testName=identity, outcome=outcome)
    summary = ET.SubElement(document, 'ResultSummary')
    total = str(sum(counts.values()))
    ET.SubElement(summary, 'Counters', total=total, executed=total, passed=total)
    ET.ElementTree(document).write(folder / 'tests.trx')


class DirectStorageTests(unittest.TestCase):
    def test_partitions_cover_all_supported_classes_once(self):
        expected = json.loads(storage.PLAN.read_text())['expected_cases']
        for system, count in (('Darwin', 270), ('Windows', 279)):
            groups = storage.plan(system)
            names = [name for values in groups.values() for name in values]
            self.assertEqual(len(names), len(set(names)))
            self.assertEqual(set(expected[system]), set(names))
            self.assertEqual(count, sum(expected[system].values()))
        with self.assertRaises(ValueError):
            storage.plan('Linux')

    def test_added_or_unassigned_test_class_refuses_plan(self):
        data = json.loads(storage.PLAN.read_text())
        data['groups']['inventory'].pop()
        with patch.object(storage.PLAN.__class__, 'read_text', return_value=json.dumps(data)):
            with self.assertRaises(ValueError):
                storage.plan('Darwin')

    def test_duplicate_partition_refused(self):
        data = json.loads(storage.PLAN.read_text())
        data['groups']['authority'].append(data['groups']['inventory'][0])
        with patch.object(storage.PLAN.__class__, 'read_text', return_value=json.dumps(data)):
            with self.assertRaises(ValueError):
                storage.plan('Darwin')

    def test_trx_pass_is_actual_case_bound(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            trx(folder, {'ExampleTests': 2})
            self.assertEqual(2, storage.read_trx(folder, ['ExampleTests'], {'ExampleTests': 2}))
            with self.assertRaises(ValueError):
                storage.read_trx(folder, ['ExampleTests'], {'ExampleTests': 3})
            with self.assertRaises(ValueError):
                storage.read_trx(folder, ['AnotherTests'])

    def test_skipped_failed_empty_and_duplicate_results_refused(self):
        for outcome in ('Failed', 'NotExecuted', 'Timeout', 'Skipped'):
            with tempfile.TemporaryDirectory() as temporary:
                folder = Path(temporary)
                trx(folder, {'ExampleTests': 1}, outcome)
                with self.assertRaises(ValueError):
                    storage.read_trx(folder, ['ExampleTests'])
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            trx(folder, {})
            with self.assertRaises(ValueError):
                storage.read_trx(folder, [])
            trx(folder, {'ExampleTests': 1})
            root = ET.parse(folder / 'tests.trx').getroot()
            root.find('Results').append(copy.deepcopy(root.find('.//UnitTestResult')))
            root.find('.//Counters').set('total', '2')
            root.find('.//Counters').set('executed', '2')
            root.find('.//Counters').set('passed', '2')
            ET.ElementTree(root).write(folder / 'tests.trx')
            with self.assertRaises(ValueError):
                storage.read_trx(folder, ['ExampleTests'])

    def test_credentials_do_not_reach_product_commands(self):
        env = command.child_environment({'PATH': '/tools', 'HOME': '/profile', 'GH_TOKEN': 'synthetic',
            'GITHUB_TOKEN': 'synthetic', 'ACTIONS_RUNTIME_TOKEN': 'synthetic', 'SSH_AUTH_SOCK': '/agent',
            'AWS_SECRET_ACCESS_KEY': 'synthetic'})
        self.assertEqual('/tools', env['PATH'])
        for key in ('GH_TOKEN', 'GITHUB_TOKEN', 'ACTIONS_RUNTIME_TOKEN', 'SSH_AUTH_SOCK', 'AWS_SECRET_ACCESS_KEY'):
            self.assertNotIn(key, env)

    def test_failed_command_keeps_scratch_instead_of_deleting_an_attached_fixture(self):
        with tempfile.TemporaryDirectory() as temporary, \
             patch.object(command.sys, 'argv', ['command.py', '--', 'test-command']), \
             patch.object(command.tempfile, 'mkdtemp', return_value=temporary), \
             patch.object(command.subprocess, 'run') as execute, \
             patch.object(command.shutil, 'rmtree') as remove:
            execute.return_value.returncode = 1
            self.assertEqual(1, command.main())
            remove.assert_not_called()
            self.assertEqual(temporary, execute.call_args.kwargs['env']['TMPDIR'])

    def test_successful_command_cleans_only_owned_scratch(self):
        with tempfile.TemporaryDirectory() as temporary, \
             patch.object(command.sys, 'argv', ['command.py', '--', 'test-command']), \
             patch.object(command.tempfile, 'mkdtemp', return_value=temporary), \
             patch.object(command.subprocess, 'run') as execute, \
             patch.object(command.shutil, 'rmtree') as remove:
            execute.return_value.returncode = 0
            self.assertEqual(0, command.main())
            remove.assert_called_once_with(temporary)

    def test_windows_success_preserves_locked_compiler_scratch_and_actual_exit(self):
        with patch.object(command.shutil, 'rmtree', side_effect=PermissionError('locked analyzer')) as remove:
            self.assertEqual(0, command.finish('owned-scratch', 0, windows=True))
            remove.assert_not_called()

    def test_cleanup_lock_cannot_convert_a_success_into_a_failed_build(self):
        with patch.object(command.shutil, 'rmtree', side_effect=PermissionError('locked analyzer')):
            self.assertEqual(0, command.finish('owned-scratch', 0, windows=False))
            self.assertEqual(7, command.finish('owned-scratch', 7, windows=False))

    def test_failed_cancelled_skipped_or_missing_jobs_refused(self):
        for jobs in ({}, {'storage': 'failure'}, {'volume': 'cancelled'}, {'native': 'skipped'}):
            with self.assertRaisesRegex(ValueError, 'prerequisite'):
                gate.evaluate(Path('/absent'), 'a' * 40, jobs)

    def test_failed_prerequisite_message_names_jobs_and_artifacts(self):
        with self.assertRaises(ValueError) as error:
            gate.evaluate(Path('/absent'), 'a' * 40,
                          {'storage': 'failure', 'native': 'success', 'volume': 'cancelled'})
        self.assertIn('storage=failure', str(error.exception))
        self.assertIn('volume=cancelled', str(error.exception))
        self.assertNotIn('native=success', str(error.exception))
        self.assertIn('artifacts', str(error.exception))

    def test_coverage_combines_source_identity_without_absolute_runner_prefix(self):
        def xml(path, visits):
            return ('<CoverageSession><Module><Files><File uid="1" fullPath="' + path + '" /></Files>'
                    '<Method><Name>method</Name><SequencePoints><SequencePoint fileid="1" sl="4" vc="' + str(visits) +
                    '" /></SequencePoints><BranchPoints><BranchPoint fileid="1" offset="0" path="0" offsetend="1" vc="' +
                    str(visits) + '" /></BranchPoints></Method></Module></CoverageSession>')
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            (folder / 'coverage.opencover.xml').write_text(xml('D:\\runner\\src\\ChunkMaestro.Persistence\\Store.cs', 0))
            lines, branches = gate.coverage_points(folder)
            self.assertEqual([False], list(lines.values()))
            (folder / 'coverage.opencover.xml').write_text(xml('/runner/src/ChunkMaestro.Persistence/Store.cs', 1))
            positive, positive_branches = gate.coverage_points(folder)
            self.assertEqual(set(lines), set(positive))
            self.assertEqual(set(branches), set(positive_branches))
            self.assertEqual([True], list(positive.values()))

    def test_missing_raw_artifact_cannot_be_replaced_by_a_receipt(self):
        with tempfile.TemporaryDirectory() as temporary, patch.object(gate, 'digest_inputs', return_value='inputs'):
            folder = Path(temporary)
            (folder / 'receipt.json').write_text(json.dumps({'status': 'PASS'}))
            with self.assertRaises(FileNotFoundError):
                gate.evaluate(folder, 'a' * 40, {'storage': 'success', 'native': 'success', 'volume': 'success'})

    def test_workflow_cannot_call_old_collectors_or_mutation_runner(self):
        workflow = (storage.ROOT / '.github/workflows/ci.yml').read_text()
        for removed in ('hosted_windows.py', 'local.py', 'qualify.py', 'replay_ci.py', 'stryker', 'workflow_dispatch:'):
            self.assertNotIn(removed, workflow)
        for retained in ('--blame-hang-timeout 60s', 'storage-safety:', 'STORAGE_SAFETY_RESULT:',
                         'persist-credentials: false', 'if: always()', 'timeout-minutes: 4'):
            self.assertIn(retained, workflow)
        self.assertNotIn('pull_request_target', workflow)

    def test_record_binds_raw_cases_head_platform_and_clean_inputs(self):
        classes = storage.plan('Darwin')['inventory']
        expected = json.loads(storage.PLAN.read_text())['expected_cases']['Darwin']
        for defect in ('none', 'head', 'platform', 'dirty'):
            with tempfile.TemporaryDirectory() as temporary, \
                 patch.object(storage, 'digest_inputs', return_value='inputs'), \
                 patch.object(storage.platform, 'system', return_value='Windows' if defect == 'platform' else 'Darwin'), \
                 patch.object(storage.subprocess, 'check_output', side_effect=[
                     'b' * 40 if defect == 'head' else 'a' * 40, ' M changed' if defect == 'dirty' else '']):
                folder = Path(temporary)
                trx(folder, {name: expected[name] for name in classes})
                (folder / 'case-inventory.json').write_text(json.dumps({
                    'head': 'a' * 40, 'group': 'inventory', 'system': 'Darwin',
                    'cases': [name + '-' + str(i) for name in classes for i in range(expected[name])]}))
                (folder / 'coverage.opencover.xml').write_text('<CoverageSession/>')
                self.assertEqual(defect != 'none', storage.record(folder, 'inventory', 'Darwin', 'a' * 40))
                report = json.loads((folder / 'result.json').read_text())
                self.assertEqual('PASS' if defect == 'none' else 'FAIL', report['status'])
                self.assertIn('tests.trx', report['files'])

    def test_semantic_duplicate_with_a_distinct_id_cannot_hide_a_missing_case(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            trx(folder, {'ExampleTests': 2})
            document = ET.parse(folder / 'tests.trx')
            rows = document.findall('.//UnitTestResult')
            rows[1].set('testName', rows[0].attrib['testName'])
            document.write(folder / 'tests.trx')
            with self.assertRaisesRegex(ValueError, 'Duplicate'):
                storage.read_trx(folder, ['ExampleTests'], {'ExampleTests': 2})

    def test_discovered_method_and_theory_case_set_is_required(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            trx(folder, {'ExampleTests': 2})
            with self.assertRaisesRegex(ValueError, 'identities'):
                storage.read_trx(folder, ['ExampleTests'], {'ExampleTests': 2},
                                 ['ExampleTests-0', 'missing-case'])

    def test_json_size_bound_and_extra_or_escaped_raw_files_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            report = folder / 'report.json'
            report.write_text(' ' * 65)
            with self.assertRaisesRegex(ValueError, 'size bound'):
                read_json(report, limit=64)
            report.write_text('{}')
            expected = inventory(folder)
            (folder / 'unrecorded.json').write_text('{}')
            with self.assertRaisesRegex(ValueError, 'unrecorded'):
                verify_inventory(folder, expected)
            (folder / 'unrecorded.json').unlink()
            outside = folder.parent / (folder.name + '-outside')
            outside.write_text('unread')
            try:
                (folder / 'link.json').symlink_to(outside)
                with self.assertRaisesRegex(ValueError, 'escapes'):
                    inventory(folder)
            finally:
                outside.unlink()


if __name__ == '__main__':
    unittest.main()
