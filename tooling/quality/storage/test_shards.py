"""Fail-closed source-shard contracts; real runner proof lives in shard_pilot."""
from copy import deepcopy
import json
from pathlib import Path
import random
import tempfile
import unittest

import replay
import shards


class ShardContracts(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = '// 😀\r\nabcdefghijklmno\r\n'
        (self.root / 'Code.cs').write_bytes(self.source.encode())
        (self.root / 'Tests.cs').write_text('test source')
        self.raw = {'files': {'Code.cs': {'source': self.source, 'mutants': []}},
                    'testFiles': {'Tests.cs': {'source': 'test source', 'tests': [{'id': 't', 'name': 'Test'}]}}}
        # Outer block, two equal-span mutators, disjoint sibling, compile error.
        for index, (start, end) in enumerate(((1, 16), (3, 7), (3, 7), (9, 12), (13, 15))):
            self.raw['files']['Code.cs']['mutants'].append({
                'id': str(index), 'mutatorName': 'M' + str(index), 'replacement': 'r',
                'location': {'start': {'line': 2, 'column': start}, 'end': {'line': 2, 'column': end}},
                'status': 'CompileError' if index == 4 else 'Killed', 'killedBy': ['t']})
        self.original = self.write('original.json', self.raw)
        self.catalogue = shards.read_catalogue(self.original, self.root)
        self.config = {'stryker-config': {'mutate': shards.BASE_PATTERNS[:]}}
        self.binding = {'head': 'a' * 40, 'sdk_version': '10.0.404', 'sdk_sha256': 'b' * 64,
                        'tool_sha256': 'c' * 64, 'upstream_commit': replay.COMMIT, 'platform': 'test'}
        self.plan = shards.plan(self.catalogue, self.config, self.binding, target=2)
        self.records = []
        for region in self.plan['regions']:
            raw = deepcopy(self.raw)
            owners = set(region['owners'])
            for mutant, row in zip(raw['files']['Code.cs']['mutants'], self.catalogue['rows']):
                if row['identity'] not in owners and mutant['status'] != 'CompileError':
                    mutant.update(status='Ignored', statusReason=shards.FILTER_REASON, killedBy=[])
            path = self.write('region-%d.json' % region['id'], raw)
            self.records.append({'region_id': region['id'], 'plan_sha256': self.plan['plan_sha256'],
                                 'config': shards.configuration(self.plan, region['id']), 'report': path,
                                 'report_sha256': replay.digest(path.read_bytes()), 'exit_code': 0,
                                 'timeout': False, 'cancelled': False})

    def write(self, name, value):
        path = self.root / name
        path.write_bytes(shards.canonical(value))
        return path

    def validate(self):
        return shards.validate(self.plan, self.records, self.root, self.binding, self.original, {'Test'})

    def change(self, function):
        record = self.records[0]
        raw = json.loads(record['report'].read_bytes())
        function(raw)
        record['report'].write_bytes(shards.canonical(raw))
        record['report_sha256'] = replay.digest(record['report'].read_bytes())

    def test_complete_actual_kills_fixed_denominator(self):
        result = self.validate()
        self.assertEqual(('ACCOUNTED', 4, 4, 'NOT_EVALUATED'),
                         (result['status'], result['original_denominator'], len(result['attributed_killed']), result['qualification']))
        self.assertEqual('INDEXED', self.plan['status'])

    def test_equal_spans_stay_together_outer_block_retained(self):
        rows = self.catalogue['rows']
        owner = {i: r['id'] for r in self.plan['regions'] for i in r['owners']}
        self.assertEqual(owner[rows[1]['identity']], owner[rows[2]['identity']])
        self.assertNotEqual(owner[rows[0]['identity']], owner[rows[1]['identity']])
        self.assertEqual([7, 22], rows[0]['span_utf16'])

    def test_plan_recomputable_inputs_immutable(self):
        before = deepcopy(self.catalogue)
        self.assertEqual(self.plan, shards.plan(self.catalogue, self.config, self.binding, 2))
        self.assertEqual(before, self.catalogue)

    def test_missing(self):
        self.records.pop()
        with self.assertRaises(ValueError): self.validate()

    def test_duplicate(self):
        self.records[-1] = self.records[0]
        with self.assertRaises(ValueError): self.validate()

    def test_tampered_plan(self):
        self.plan['regions'][0]['owners'] = []
        with self.assertRaises(ValueError): self.validate()

    def test_refiltered_original(self):
        self.plan['catalogue']['rows'].pop()
        with self.assertRaises(ValueError): self.validate()

    def test_stale_binding(self):
        self.binding['head'] = 'd' * 40
        with self.assertRaises(ValueError): self.validate()

    def test_config_tamper(self):
        self.records[0]['config']['stryker-config']['mutate'] = ['**/*.cs']
        with self.assertRaises(ValueError): self.validate()

    def test_cancelled(self):
        self.records[0]['cancelled'] = True
        with self.assertRaises(ValueError): self.validate()

    def test_command_timeout(self):
        self.records[0]['timeout'] = True
        with self.assertRaises(ValueError): self.validate()

    def test_failed(self):
        self.records[0]['exit_code'] = 1
        with self.assertRaises(ValueError): self.validate()

    def test_changed_compile_mask(self):
        self.change(lambda raw: raw['files']['Code.cs']['mutants'][-1].update(status='Ignored'))
        with self.assertRaises(ValueError): self.validate()

    def test_partial_raw(self):
        self.change(lambda raw: raw['files']['Code.cs']['mutants'].pop())
        with self.assertRaises(ValueError): self.validate()

    def test_mutated_identity(self):
        self.change(lambda raw: raw['files']['Code.cs']['mutants'][0].update(replacement='tampered'))
        with self.assertRaises(ValueError): self.validate()

    def test_out_of_owner_execution(self):
        self.change(lambda raw: [m.update(status='Killed', killedBy=['t']) for m in raw['files']['Code.cs']['mutants'] if m['status'] == 'Ignored'])
        with self.assertRaises(ValueError): self.validate()

    def test_timeout_owner_denies(self):
        self.change(lambda raw: [m.update(status='Timeout') for m in raw['files']['Code.cs']['mutants'] if m['status'] == 'Killed'])
        with self.assertRaises(ValueError): self.validate()

    def test_ignored_owner_denies(self):
        self.change(lambda raw: [m.update(status='Ignored') for m in raw['files']['Code.cs']['mutants'] if m['status'] == 'Killed'])
        with self.assertRaises(ValueError): self.validate()

    def test_unattributed_kill(self):
        self.change(lambda raw: [m.update(killedBy=['unknown']) for m in raw['files']['Code.cs']['mutants']])
        with self.assertRaises(ValueError): self.validate()

    def test_unbound_test_method(self):
        self.change(lambda raw: raw['testFiles']['Tests.cs']['tests'][0].update(name='Invented'))
        with self.assertRaises(ValueError): self.validate()

    def test_mapped_source(self):
        self.raw['files']['Code.cs']['source'] = '#line 1 "other"\n'
        (self.root / 'Code.cs').write_text(self.raw['files']['Code.cs']['source'])
        with self.assertRaises(ValueError): shards.read_catalogue(self.write('mapped.json', self.raw), self.root)

    def test_unbound_source(self):
        (self.root / 'Code.cs').write_text('changed')
        with self.assertRaises(ValueError): self.validate()

    def test_ambiguous_identity(self):
        self.raw['files']['Code.cs']['mutants'].append(self.raw['files']['Code.cs']['mutants'][0])
        with self.assertRaises(ValueError): shards.read_catalogue(self.write('duplicate.json', self.raw), self.root)

    def test_crossing(self):
        self.catalogue['rows'][3]['span_utf16'] = [10, 23]
        with self.assertRaises(ValueError): shards.plan(self.catalogue, self.config, self.binding, 2)

    def test_exclusion_scope_cannot_change(self):
        self.config['stryker-config']['mutate'].pop()
        with self.assertRaises(ValueError): shards.plan(self.catalogue, self.config, self.binding, 2)

    def test_positive_weights_required(self):
        with self.assertRaises(ValueError): shards.plan(self.catalogue, self.config, self.binding, 2, {})

    def test_random_laminar_atomic_ownership(self):
        rng = random.Random(190)
        for _ in range(100):
            target = rng.randint(1, 20)
            weights = {r['identity']: rng.randint(1, 7) for r in self.catalogue['rows']}
            plan = shards.plan(self.catalogue, self.config, self.binding, target, weights)
            owners = [i for r in plan['regions'] for i in r['owners']]
            self.assertEqual(sorted(weights), sorted(owners))
            self.assertEqual(len(owners), len(set(owners)))

    def batch_fixture(self, width=2, producer=None):
        original, value, raw = self.original, deepcopy(self.plan), deepcopy(self.raw)
        if producer:
            raw['files'] = {producer + '/Code.cs': raw['files']['Code.cs']}
            raw['testFiles'] = {producer + '/Tests.cs': raw['testFiles']['Tests.cs']}
            original = self.write('foreign-original.json', raw)
            value = shards.plan(shards.read_catalogue(original, self.root, producer), self.config, self.binding, target=2)
        batches = shards.batch_plan(value, width)
        records = []
        for batch in batches['batches']:
            report = deepcopy(raw)
            owners = set(batch['owners'])
            mutants = next(iter(report['files'].values()))['mutants']
            for mutant, row in zip(mutants, value['catalogue']['rows']):
                if row['identity'] not in owners and mutant['status'] != 'CompileError':
                    mutant.update(status='Ignored', statusReason=shards.FILTER_REASON, killedBy=[])
            path = self.write('batch-%d.json' % batch['id'], report)
            record = {'batch_id': batch['id'], 'plan_sha256': value['plan_sha256'],
                      'batch_plan_sha256': batches['batch_plan_sha256'],
                      'config': shards.batch_configuration(value, batches, batch['id']),
                      'report': path, 'report_sha256': replay.digest(path.read_bytes()),
                      'exit_code': 0, 'timeout': False, 'cancelled': False}
            if producer:
                record['workspace_root'] = producer
            records.append(record)
        return value, batches, records, original

    def validate_batch_fixture(self, fixture, producer=None):
        value, batches, records, original = fixture
        return shards.validate_batches(value, batches, records, self.root, self.binding,
                                       original, {'Test'}, producer)

    def test_batch_accounting_keeps_all_original_eligibility_and_actual_kills(self):
        result = self.validate_batch_fixture(self.batch_fixture())
        self.assertEqual((result['status'], result['original_denominator'], len(result['attributed_killed'])),
                         ('ACCOUNTED', 4, 4))
        self.assertEqual(result['qualification'], 'NOT_EVALUATED')

    def test_batch_packing_separates_ancestor_exclusion_from_descendant_owner(self):
        value, batches, _, _ = self.batch_fixture()
        region_batch = {r: b['id'] for b in batches['batches'] for r in b['region_ids']}
        for ancestor in value['regions']:
            for descendant in value['regions']:
                if ancestor['id'] != descendant['id'] and any(n['file'] == descendant['root']['file'] and
                        shards.contains(n['span'], descendant['root']['span']) for n in ancestor['exclusions']):
                    self.assertNotEqual(region_batch[ancestor['id']], region_batch[descendant['id']])
        with self.assertRaisesRegex(ValueError, 'width'):
            shards.batch_plan(value, 1)

    def test_batches_are_deterministic_complete_and_bounded(self):
        for width in (2, 3, 12):
            value, batches, _, _ = self.batch_fixture(width)
            self.assertEqual(batches, shards.batch_plan(value, width))
            self.assertLessEqual(len(batches['batches']), width)
            self.assertTrue(all(b['patterns'][0] == shards.CATALOGUE_KEEPALIVE for b in batches['batches']))
            self.assertEqual(sorted(i for b in batches['batches'] for i in b['region_ids']), list(range(len(value['regions']))))
            self.assertEqual(len([i for b in batches['batches'] for i in b['owners']]), len(value['weights']))
        for width in (0, -1, True, 2.0):
            with self.assertRaises(ValueError): shards.batch_plan(self.plan, width)

    def test_keepalive_cannot_select_an_extra_original_mutant(self):
        catalogue = deepcopy(self.catalogue)
        catalogue['rows'][0]['span_utf16'] = [0, 1]
        value = shards.plan(catalogue, self.config, self.binding, target=2)
        with self.assertRaisesRegex(ValueError, 'keepalive'):
            shards.batch_plan(value, 2)

    def test_ignored_file_placeholder_is_never_filled_from_checkout(self):
        raw = deepcopy(self.raw)
        raw['files']['Code.cs'] = {'source': 'File ignored by mutate filter', 'language': 'none', 'mutants': []}
        with self.assertRaisesRegex(ValueError, 'source'):
            shards.read_catalogue(self.write('placeholder.json', raw), self.root)

    def test_batch_portable_windows_source_and_test_paths_are_bound_without_raw_rewrite(self):
        producer = 'D:/agent/work/repo'
        fixture = self.batch_fixture(producer=producer)
        before = {p: p.read_bytes() for p in [fixture[3], *(r['report'] for r in fixture[2])]}
        self.assertEqual(len(self.validate_batch_fixture(fixture, producer)['attributed_killed']), 4)
        self.assertTrue(all(p.read_bytes() == b for p, b in before.items()))
        with self.assertRaises(ValueError): self.validate_batch_fixture(fixture)
        with self.assertRaises(ValueError): self.validate_batch_fixture(fixture, 'D:/other/repo')

    def test_portable_path_rejects_traversal_alias_symlink_and_unknown_root(self):
        for name in ('D:/agent/repo/../Code.cs', 'D:/other/Code.cs', 'D:/agent/repo/a/../Code.cs',
                     'D:/agent/repo//Code.cs', '../Code.cs', 'sub/../Code.cs'):
            with self.subTest(name=name), self.assertRaises(ValueError):
                shards.report_path(self.root, name, 'D:/agent/repo')
        alias = self.root / 'Alias.cs'
        try:
            alias.symlink_to(self.root / 'Code.cs')
        except OSError:
            return  # Windows privilege limitation; traversal cases above still execute.
        with self.assertRaises(ValueError): shards.report_path(self.root, str(alias))
        raw = deepcopy(self.raw)
        raw['files']['D:/agent/repo/Code.cs'] = raw['files']['Code.cs']
        with self.assertRaises(ValueError): shards.read_catalogue(self.write('aliased.json', raw), self.root, 'D:/agent/repo')

    def test_batch_denies_missing_duplicate_tampered_and_wrong_execution_records(self):
        for defect in ('missing', 'duplicate', 'plan', 'batch', 'config', 'timeout', 'cancelled', 'exit', 'bool-exit'):
            fixture = self.batch_fixture()
            value, batches, records, _ = fixture
            if defect == 'missing': records.pop()
            if defect == 'duplicate': records[-1] = records[0]
            if defect == 'plan': value['regions'][0]['owners'] = []
            if defect == 'batch': batches['batches'][0]['patterns'].append('**/*.cs')
            if defect == 'config': records[0]['config']['stryker-config']['mutate'] = ['**/*.cs']
            if defect == 'timeout': records[0]['timeout'] = True
            if defect == 'cancelled': records[0]['cancelled'] = True
            if defect == 'exit': records[0]['exit_code'] = 1
            if defect == 'bool-exit': records[0]['exit_code'] = False
            with self.subTest(defect=defect), self.assertRaises(ValueError): self.validate_batch_fixture(fixture)

    def test_batch_denies_source_test_identity_mask_attribution_and_outside_execution_tampering(self):
        for defect in ('source', 'test', 'identity', 'mask', 'attribution', 'outside', 'ignored-owner', 'partial'):
            fixture = self.batch_fixture()
            record = fixture[2][0]
            raw = json.loads(record['report'].read_bytes())
            mutants = raw['files']['Code.cs']['mutants']
            if defect == 'source': raw['files']['Code.cs']['source'] = 'changed'
            if defect == 'test': raw['testFiles']['Tests.cs']['source'] = 'changed'
            if defect == 'identity': mutants[0]['replacement'] = 'changed'
            if defect == 'mask': mutants[-1]['status'] = 'Ignored'
            if defect == 'attribution': next(m for m in mutants if m['status'] == 'Killed')['killedBy'] = ['unknown']
            if defect == 'outside': next(m for m in mutants if m['status'] == 'Ignored').update(status='Killed', killedBy=['t'])
            if defect == 'ignored-owner': next(m for m in mutants if m['status'] == 'Killed')['status'] = 'Ignored'
            if defect == 'partial': mutants.pop()
            record['report'].write_bytes(shards.canonical(raw))
            record['report_sha256'] = replay.digest(record['report'].read_bytes())
            with self.subTest(defect=defect), self.assertRaises(ValueError): self.validate_batch_fixture(fixture)

    def test_complete_mutant_timeout_retains_denominator_without_kill_credit(self):
        fixture = self.batch_fixture()
        record = fixture[2][0]
        raw = json.loads(record['report'].read_bytes())
        next(m for m in raw['files']['Code.cs']['mutants'] if m['status'] == 'Killed')['status'] = 'Timeout'
        record['report'].write_bytes(shards.canonical(raw))
        record['report_sha256'] = replay.digest(record['report'].read_bytes())
        result = self.validate_batch_fixture(fixture)
        self.assertEqual(result['original_denominator'], 4)
        self.assertEqual(len(result['attributed_killed']), 3)
        self.assertEqual(len(result['uncredited_mutant_timeouts']), 1)


if __name__ == '__main__':
    unittest.main()
