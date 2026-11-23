"""Mechanical refusal contracts; these synthetic reports are not runtime evidence."""
from copy import deepcopy
import json
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

import replay
import shards
import test_groups as groups


class TestGroups(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / 'Code.cs').write_bytes(b'abcdef\n')
        (self.root / 'Tests.cs').write_bytes(b'test bytes')
        self.cases = [{'method': 'Tests.Class.A', 'display': 'A(1)'},
                      {'method': 'Tests.Class.A', 'display': 'A(2)'},
                      {'method': 'Tests.Class.B', 'display': 'B'}]
        self.baseline = self.trx('full', self.cases)
        self.raw = {'files': {'Code.cs': {'source': 'abcdef\n', 'mutants': []}},
                    'testFiles': {'Tests.cs': {'source': 'test bytes', 'tests': []}}}
        for i, status in enumerate(('Ignored', 'Survived', 'CompileError')):
            self.raw['files']['Code.cs']['mutants'].append({'id': str(i), 'mutatorName': 'M', 'replacement': 'x',
                'location': {'start': {'line': 1, 'column': i + 1}, 'end': {'line': 1, 'column': i + 2}}, 'status': status})
        self.original = self.write('official', self.raw)
        self.catalogue = shards.read_catalogue(self.original, self.root)
        self.config = {'stryker-config': {'mutate': shards.BASE_PATTERNS[:]}}
        self.binding = {'head': 'a' * 40, 'sdk_version': '10.0.404', 'sdk_sha256': 'b' * 64,
                        'tool_sha256': 'c' * 64, 'upstream_commit': replay.COMMIT, 'platform': 'fixture'}
        self.value = groups.plan(self.catalogue, self.config, self.binding, groups.read_trx(self.baseline),
                                 groups.inventory(self.root, ['Code.cs', 'Tests.cs']), 2)
        self.records = []
        for group in self.value['groups']:
            raw = deepcopy(self.raw)
            raw['testFiles']['Tests.cs']['tests'] = [{'id': str(i), 'name': c['method']} for i, c in enumerate(group['cases'])]
            raw['files']['Code.cs']['mutants'][0].update(status='Killed', killedBy=['0'])
            raw['files']['Code.cs']['mutants'][1]['status'] = 'Timeout'
            report = self.write('group' + str(group['id']), raw)
            selected = self.trx('selected' + str(group['id']), group['cases'])
            self.records.append({'group_id': group['id'], 'plan_sha256': self.value['plan_sha256'],
                'config': groups.configuration(self.value, group['id']), 'report': report,
                'report_sha256': replay.digest(report.read_bytes()), 'exit_code': 2, 'timeout': False,
                'cancelled': False, 'baseline': selected, 'restored': selected, 'test_definitions': 'selected'})

    def trx(self, name, cases):
        tree = ET.Element('TestRun')
        ET.SubElement(tree, 'Counters', total=str(len(cases)), executed=str(len(cases)), passed=str(len(cases)))
        definitions = ET.SubElement(tree, 'TestDefinitions')
        results = ET.SubElement(tree, 'Results')
        for i, case in enumerate(cases):
            definition = ET.SubElement(definitions, 'UnitTest', id=str(i))
            cls, method = case['method'].rsplit('.', 1)
            ET.SubElement(definition, 'TestMethod', className=cls, name=method)
            ET.SubElement(results, 'UnitTestResult', testId=str(i), testName=case['display'], outcome='Passed', duration='00:00:01.0000001')
        path = self.root / (name + '.trx')
        path.write_bytes(ET.tostring(tree))
        return path

    def write(self, name, value):
        path = self.root / (name + '.json')
        path.write_bytes(shards.canonical(value))
        return path

    def validate(self):
        return groups.validate(self.value, self.records, self.root, self.binding, self.original, self.baseline, self.baseline)

    def change_report(self, change):
        record = self.records[0]
        raw = json.loads(record['report'].read_bytes())
        change(raw)
        record['report'].write_bytes(shards.canonical(raw))
        record['report_sha256'] = replay.digest(record['report'].read_bytes())

    def test_atomic_theory_exact_filter_fixed_denominator(self):
        result = self.validate()
        self.assertEqual((2, 1, 1, 'NOT_EVALUATED'), (result['original_denominator'], len(result['attributed_killed']),
                         len(result['uncredited_mutant_timeouts']), result['qualification']))
        self.assertEqual([2, 1], [len(g['cases']) for g in self.value['groups']])
        self.assertEqual('FullyQualifiedName=Tests.Class.A', self.value['groups'][0]['filter'])
        self.assertEqual(shards.BASE_PATTERNS, self.records[0]['config']['stryker-config']['mutate'])

    def test_deterministic_weights_and_no_input_mutation(self):
        old = deepcopy(self.value)
        self.assertEqual(old, groups.plan(self.catalogue, self.config, self.binding, groups.read_trx(self.baseline), old['inputs'], 2))
        self.assertEqual(old, self.value)
        weighted = groups.plan(self.catalogue, self.config, self.binding, groups.read_trx(self.baseline), old['inputs'], 2, True)
        self.assertEqual([2000000, 1000000], [g['weight'] for g in weighted['groups']])

    def test_unsupported_names(self):
        for name in ('A.B|C.D', 'A.B(1)', 'A+B.C', 'A.B C', 'A.B`1', 'A.B=Z', 'A.B~Z', 'A.é', 'A.B\n'):
            with self.subTest(name=name), self.assertRaises(ValueError):
                groups.method_name(name)

    def test_missing_duplicate_and_failed_groups(self):
        for mutation in (lambda r: r.pop(), lambda r: r.append(r[0]), lambda r: r[0].update(exit_code=1),
                         lambda r: r[0].update(timeout=True), lambda r: r[0].update(cancelled=True),
                         lambda r: r[0].update(exit_code=True), lambda r: r[0].update(group_id=True)):
            with self.subTest(mutation=mutation):
                original = deepcopy(self.records)
                mutation(self.records)
                with self.assertRaises(ValueError): self.validate()
                self.records = original

    def test_changed_mask_catalogue_source_attribution(self):
        changes = [lambda r: r['files']['Code.cs']['mutants'].pop(),
                   lambda r: r['files']['Code.cs']['mutants'][2].update(status='NoCoverage'),
                   lambda r: r['files']['Code.cs']['mutants'][0].update(killedBy=[]),
                   lambda r: r['files']['Code.cs']['mutants'][0].update(killedBy=['outside']),
                   lambda r: r['files']['Code.cs']['mutants'][0].update(status='Ignored'),
                   lambda r: r['testFiles']['Tests.cs'].update(source='changed'),
                   lambda r: r['files']['Code.cs'].update(source='changed'),
                   lambda r: r['testFiles']['Tests.cs']['tests'].pop(),
                   lambda r: r['files']['Code.cs']['mutants'][0].update(replacement='changed')]
        record = self.records[0]
        original = record['report'].read_bytes()
        for change in changes:
            with self.subTest(change=change):
                self.change_report(change)
                with self.assertRaises(ValueError): self.validate()
                record['report'].write_bytes(original)
                record['report_sha256'] = replay.digest(original)

    def test_all_definitions_do_not_authorize_outside_kill(self):
        for record in self.records:
            raw = json.loads(record['report'].read_bytes())
            raw['testFiles']['Tests.cs']['tests'] = [{'id': str(i), 'name': c['method']} for i, c in enumerate(self.cases)]
            own_id = '0' if record['group_id'] == 0 else '2'
            raw['files']['Code.cs']['mutants'][0]['killedBy'] = [own_id]
            record['report'].write_bytes(shards.canonical(raw))
            record['report_sha256'] = replay.digest(record['report'].read_bytes())
            record['test_definitions'] = 'all'
        self.validate()
        self.change_report(lambda r: r['files']['Code.cs']['mutants'][0].update(killedBy=['2']))
        with self.assertRaises(ValueError): self.validate()

    def test_selected_cases_must_include_every_theory_row(self):
        self.records[0]['baseline'] = self.trx('partial', self.cases[:1])
        with self.assertRaises(ValueError): self.validate()

    def test_tampered_plan_binding_config_inventory(self):
        for mutate in (lambda: self.value['groups'][0]['methods'].clear(),
                       lambda: self.binding.update(head='f' * 40),
                       lambda: self.records[0]['config']['stryker-config'].update(mutate=['**/Code.cs']),
                       lambda: (self.root / 'Tests.cs').write_text('edited')):
            with self.subTest(mutate=mutate):
                value, binding, records = deepcopy((self.value, self.binding, self.records))
                mutate()
                with self.assertRaises(ValueError): self.validate()
                self.value, self.binding, self.records = value, binding, records
                (self.root / 'Tests.cs').write_text('test bytes')

    def test_trx_refusals(self):
        original = self.baseline.read_bytes()
        for before, after in ((b'outcome="Passed"', b'outcome="NotExecuted"'),
                              (b'passed="3"', b'passed="2"'), (b'testId="0"', b'testId="absent"'),
                              (b'00:00:01.0000001', b'00:61:01'), (b'testName="A(2)"', b'testName="A(1)"')):
            self.baseline.write_bytes(original.replace(before, after))
            with self.assertRaises(ValueError): groups.read_trx(self.baseline)
        self.baseline.write_bytes(original)

    def test_many_partitions_preserve_every_case_once(self):
        import random
        rng = random.Random(6)
        for trial in range(50):
            cases = sorted([{'method': 'Tests.Class.Method' + str(i), 'display': str(row)}
                            for i in range(rng.randint(2, 40)) for row in range(rng.randint(1, 5))], key=groups.case_key)
            baseline = groups.read_trx(self.trx('random', cases))
            width = rng.randint(1, min(20, len(baseline['weights'])))
            plan = groups.plan(self.catalogue, self.config, self.binding, baseline, self.value['inputs'], width, trial % 2 == 0)
            self.assertEqual(cases, sorted([c for g in plan['groups'] for c in g['cases']], key=groups.case_key))
            methods = [m for g in plan['groups'] for m in g['methods']]
            self.assertEqual(len(set(methods)), len(methods))
            self.assertTrue(all(g['cases'] for g in plan['groups']))

    def test_unused_discovery_definitions_do_not_create_cases(self):
        tree = ET.fromstring(self.baseline.read_bytes())
        definition = ET.SubElement(tree.find('TestDefinitions'), 'UnitTest', id='unused')
        ET.SubElement(definition, 'TestMethod', className='Tests.Class', name='Unused')
        self.baseline.write_bytes(ET.tostring(tree))
        self.assertEqual(self.cases, groups.read_trx(self.baseline)['cases'])

    def test_invalid_group_width_and_prefilter(self):
        for count in (0, True, 3, 21):
            with self.assertRaises(ValueError):
                groups.plan(self.catalogue, self.config, self.binding, groups.read_trx(self.baseline), self.value['inputs'], count)
        self.config['stryker-config']['test-case-filter'] = 'FullyQualifiedName=Tests.Class.A'
        with self.assertRaises(ValueError):
            groups.plan(self.catalogue, self.config, self.binding, groups.read_trx(self.baseline), self.value['inputs'], 2)


if __name__ == '__main__':
    unittest.main()
