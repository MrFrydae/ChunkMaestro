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


if __name__ == '__main__':
    unittest.main()
