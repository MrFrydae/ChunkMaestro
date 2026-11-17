"""Offline public-boundary denials, not Windows execution evidence."""
from copy import deepcopy
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

import replay
import shards
import shard_ci
import test_cell_ci as cell
import test_groups


class Cells(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.name = 'src/ChunkMaestro.Persistence/LocalFleetStore.cs'
        source = self.root / self.name
        source.parent.mkdir(parents=True)
        source.write_bytes(b'private static async Task<LocalFleetStore> OpenWorkerAsync(\nabcdef\ninternal static bool IsControllerContention(\n')
        (self.root / 'Tests.cs').write_text('test source')
        self.cases = [{'method': 'Tests.Class.A', 'display': 'A(1)'},
                      {'method': 'Tests.Class.A', 'display': 'A(2)'},
                      {'method': 'Tests.Class.B', 'display': 'B'}]
        full = self.root / 'full'
        self.write_trx(full)
        self.current = {'head': 'a' * 40, 'platform': 'Windows', 'architecture': 'AMD64',
                        'run_id': '123', 'run_attempt': '1', 'trust': {'event': 'workflow_dispatch'}}
        self.bundle = dict(self.current, sdk_version='10.0.404', sdk_sha256='b' * 64,
                           private_tool_sha256='c' * 64, bundle_sha256='d' * 64,
                           inputs=test_groups.inventory(self.root, [self.name, 'Tests.cs']), sdk_path='/sdk/dotnet',
                           private_binary_relative='stryker-net-' + replay.COMMIT + '/src/Stryker.CLI/Stryker.CLI/bin/Release/Stryker.CLI.dll')
        self.raw = {'files': {self.name: {'source': source.read_bytes().decode(), 'mutants': []}},
                    'testFiles': {'Tests.cs': {'source': 'test source', 'tests': []}}}
        for i, status in enumerate(('Ignored', 'Ignored', 'CompileError')):
            self.raw['files'][self.name]['mutants'].append({'id': str(i), 'status': status, 'mutatorName': 'M',
                'replacement': 'X', 'location': {'start': {'line': 2, 'column': i + 1},
                                                'end': {'line': 2, 'column': i + 2}}})
        self.original = self.root / 'official.json'
        replay.write(self.original, self.raw)
        self.config = {'stryker-config': {'mutate': shards.BASE_PATTERNS[:], 'thresholds': {'break': 80}}}
        self.plan = cell.make_plan(self.bundle, shards.read_catalogue(self.original, self.root), self.config,
                                   cell.trx(full), self.root)
        self.folders = [self.make_cell(i) for i in (0, 1)]

    def write_trx(self, folder, cases=None):
        cases = self.cases if cases is None else cases
        folder.mkdir(parents=True, exist_ok=True)
        tree = ET.Element('TestRun')
        ET.SubElement(tree, 'Counters', total=str(len(cases)), executed=str(len(cases)), passed=str(len(cases)))
        defs, results = ET.SubElement(tree, 'TestDefinitions'), ET.SubElement(tree, 'Results')
        for i, case in enumerate(cases):
            definition = ET.SubElement(defs, 'UnitTest', id=str(i))
            cls, method = case['method'].rsplit('.', 1)
            ET.SubElement(definition, 'TestMethod', className=cls, name=method)
            ET.SubElement(results, 'UnitTestResult', testId=str(i), testName=case['display'],
                          outcome='Passed', duration='00:00:01')
        (folder / 'full.trx').write_bytes(ET.tostring(tree))

    def make_cell(self, group):
        folder = self.root / ('cell' + str(group))
        folder.mkdir()
        self.write_trx(folder / 'baseline')
        self.write_trx(folder / 'restored')
        replay.write(folder / 'cell-config.json', cell.configuration(self.plan, group))
        runtime = {'native.dll': 'e' * 64, 'managed.dll': 'f' * 64}
        for name in ('baseline-binaries.json', 'restored-final-binaries.json'):
            replay.write(folder / name, runtime)
        raw = deepcopy(self.raw)
        selected = self.plan['tests']['groups'][group]['cases']
        raw['testFiles']['Tests.cs']['tests'] = [{'id': str(i), 'name': c['method']} for i, c in enumerate(selected)]
        owners = self.plan['region']['owners']
        catalogue = shards.read_catalogue(self.original, self.root)
        for row, mutant in zip(catalogue['rows'], raw['files'][self.name]['mutants']):
            if mutant['status'] != 'CompileError':
                if row['identity'] in owners:
                    mutant.update(status='Killed', killedBy=['0'])
                else:
                    mutant.update(status='Ignored', statusReason=shards.FILTER_REASON)
        (folder / 'mutation').mkdir()
        replay.write(folder / 'mutation/mutation-report.json', raw)
        sdk = '/sdk/dotnet'
        binary = str(self.root / '.artifacts/cell-input/source/private' / self.bundle['private_binary_relative'])
        commands = [[sdk, 'restore', str(self.root / shard_ci.TEST_PROJECT), '--locked-mode']]
        labels = ['restore']
        for label in ('baseline', 'restored'):
            for verb, extra in (('clean', []), ('build', ['--no-restore', '--no-incremental']),
                ('test', ['--no-restore', '--no-build', '--logger', 'trx', '--results-directory', str(folder / label)])):
                commands.append([sdk, verb, str(self.root / shard_ci.TEST_PROJECT), '-c', 'Release', *extra])
                labels.append(label + '-' + verb)
        commands.insert(4, [sdk, binary, '--config-file', str(folder / 'cell-config.json'), '--output', str(folder / 'mutation'), '--log-to-file'])
        labels.insert(4, 'private')
        records = []
        for index, (args, label) in enumerate(zip(commands, labels)):
            log = folder / (label + '.log')
            log.write_text(f"Number of tests found: {len(selected)} for project Lib.csproj. Initial test run started.\n"
                           "Capture mutant coverage using 'CoverageBasedTest' mode.\nThe final mutation score is 100 %\n")
            records.append({'args': args, 'cwd': str(self.root / 'tooling/quality/storage' if index == 4 else self.root),
                'exit_code': 0, 'timeout': False, 'seconds': 1, 'log': str(log), 'log_sha256': replay.digest(log.read_bytes())})
        (folder / 'commands.jsonl').write_text(''.join(json.dumps(r) + '\n' for r in records))
        value = dict(self.current, schema_version=1, scope=cell.SCOPE, status='CELL_COMPLETE', qualification='NOT_EVALUATED',
            inputs_preserved=True, baseline_restored=True, elapsed_seconds=8, inputs=self.bundle['inputs'],
            source_bundle_sha256=self.bundle['bundle_sha256'], plan_sha256=self.plan['plan_sha256'], group=group,
            output_root=str(folder), workspace_root=str(self.root), sdk_path=sdk, binary_path=binary, commands=records,
            retained_tool_backups=[])
        value['derived_group_view'] = {'status': 'DERIVED', 'cases': selected,
            'baseline_trx_sha256': cell.trx(folder / 'baseline')['trx_sha256'],
            'restored_trx_sha256': cell.trx(folder / 'restored')['trx_sha256']}
        value['inventory'] = cell.cell_inventory(folder)
        replay.write(folder / 'cell.json', value)
        return folder

    def validate(self):
        return cell.validate_cell(self.folders[0], self.plan, self.bundle, self.current, self.root)

    def reseal(self, change):
        folder = self.folders[0]
        value = cell.load(folder / 'cell.json')
        change(folder, value)
        actual = cell.cell_inventory(folder)
        value['inventory'] = actual
        replay.write(folder / 'cell.json', value)

    def test_complete_contract_and_derived_union(self):
        self.assertEqual([0, 1], [cell.validate_cell(f, self.plan, self.bundle, self.current, self.root)[0] for f in self.folders])
        self.assertEqual([2, 1], [len(g['cases']) for g in self.plan['tests']['groups']])
        self.assertEqual([shards.CATALOGUE_KEEPALIVE, *shards.EXCLUSIONS],
                         cell.configuration(self.plan, 0)['stryker-config']['mutate'][:3])
        self.assertEqual(2, self.plan['sources']['original_denominator'])
        self.assertIs(True, cell.configuration(self.plan, 0)['stryker-config']['break-on-initial-test-failure'])

    def test_raw_tamper_denied(self):
        (self.folders[0] / 'private.log').write_text('tampered')
        with self.assertRaisesRegex(ValueError, 'evidence'):
            self.validate()

    def test_root_bundle_artifact_not_silently_omitted(self):
        (self.folders[0] / 'bundle.json').write_text('unexpected raw evidence', encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'evidence'):
            self.validate()

    def test_initial_failure_continuation_denied_by_complete_validator(self):
        def change(folder, value):
            log = folder / 'private.log'
            text = log.read_text(encoding='utf-8').replace(
                "Capture mutant coverage", "1 tests are failing. Stryker will continue but outcome will be impacted.\nCapture mutant coverage")
            log.write_bytes(text.encode('utf-8'))
            value['commands'][4]['log_sha256'] = replay.digest(log.read_bytes())
            (folder / 'commands.jsonl').write_bytes(
                ''.join(json.dumps(r) + '\n' for r in value['commands']).encode('utf-8'))
        self.reseal(change)
        with self.assertRaisesRegex(ValueError, 'successful initial-run'):
            self.validate()

    def test_initial_failure_break_cannot_be_omitted_or_disabled(self):
        for omitted in (False, True):
            def change(folder, value):
                config = cell.load(folder / 'cell-config.json')
                if omitted:
                    config['stryker-config'].pop('break-on-initial-test-failure')
                else:
                    config['stryker-config']['break-on-initial-test-failure'] = False
                replay.write(folder / 'cell-config.json', config)
            self.reseal(change)
            with self.assertRaisesRegex(ValueError, 'config'):
                self.validate()

    def test_coordinated_foreign_private_entry_point_denied(self):
        def change(folder, value):
            binary = str(self.root / 'foreign/source/private' / self.bundle['private_binary_relative'])
            value['binary_path'] = binary
            value['commands'][4]['args'][1] = binary
            (folder / 'commands.jsonl').write_text(
                ''.join(json.dumps(r) + '\n' for r in value['commands']), encoding='utf-8')
        self.reseal(change)
        with self.assertRaisesRegex(ValueError, 'entry point'):
            self.validate()

    def test_manifest_denials(self):
        original = (self.folders[0] / 'cell.json').read_bytes()
        for changes in ({'status': 'UNRESOLVED'}, {'baseline_restored': False}, {'elapsed_seconds': 481},
                        {'inputs_preserved': False}, {'group': True}, {'head': 'f' * 40}, {'run_attempt': '2'}):
            with self.subTest(changes=changes):
                self.reseal(lambda f, v: v.update(changes))
                with self.assertRaises(ValueError): self.validate()
                (self.folders[0] / 'cell.json').write_bytes(original)

    def test_case_omission_and_extra_config_denied(self):
        for action in (lambda f, v: self.write_trx(f / 'restored', self.cases[:1]),
                       lambda f, v: replay.write(f / 'cell-config.json', {'stryker-config': {'since': True}}),
                       lambda f, v: replay.write(f / 'restored-final-binaries.json', {'managed.dll': 'f' * 64})):
            self.reseal(action)
            with self.assertRaises(ValueError): self.validate()

    def test_report_denials_even_if_inventory_resealed(self):
        folder = self.folders[0]
        report = folder / 'mutation/mutation-report.json'
        original = report.read_bytes()
        actions = [lambda r: r['files'][self.name]['mutants'].pop(),
            lambda r: r['files'][self.name].update(source='changed'),
            lambda r: r['testFiles']['Tests.cs'].update(source='changed'),
            lambda r: r['testFiles']['Tests.cs']['tests'].pop(),
            lambda r: r['files'][self.name]['mutants'][0].update(killedBy=['foreign']),
            lambda r: r['files'][self.name]['mutants'][2].update(status='NoCoverage'),
            lambda r: r['files'][self.name]['mutants'][1].update(status='Survived'),
            lambda r: r['files'].update({'/foreign/Code.cs': r['files'].pop(self.name)})]
        for action in actions:
            raw = json.loads(original)
            action(raw)
            self.reseal(lambda f, v: replay.write(report, raw))
            with self.assertRaises(ValueError): self.validate()
        report.write_bytes(original)

    def test_command_options_timeout_and_score_denied(self):
        original = (self.folders[0] / 'cell.json').read_bytes()
        ledger = (self.folders[0] / 'commands.jsonl').read_bytes()
        for action in (lambda r: r[4]['args'].append('--since'), lambda r: r[4].update(timeout=True),
                       lambda r: r[4].update(seconds=181), lambda r: r[4].update(exit_code=2),
                       lambda r: r[7].update(exit_code=1), lambda r: r[4].update(log='/foreign/log')):
            def change(folder, value):
                action(value['commands'])
                (folder / 'commands.jsonl').write_text(''.join(json.dumps(r) + '\n' for r in value['commands']))
            self.reseal(change)
            with self.assertRaises(ValueError): self.validate()
            (self.folders[0] / 'cell.json').write_bytes(original)
            (self.folders[0] / 'commands.jsonl').write_bytes(ledger)

    def test_all_cells_required_and_no_overlap(self):
        with patch.object(cell, 'windows_context', return_value=self.current), \
             patch.object(cell.source, 'sdk', return_value=(Path('/sdk/dotnet'), '10.0.404', 'b' * 64)), \
             patch.object(cell, 'preflight', return_value=(self.bundle, self.plan, Path('/tool'))):
            result = cell.account(self.root / '.artifacts/cell-input', self.folders, self.root)
            self.assertEqual('PARTIAL_DIAGNOSTIC', result['status'])
            self.assertEqual('NOT_EVALUATED', result['qualification'])
            for folders in ([], self.folders[:1], [self.folders[0], self.folders[0]]):
                with self.assertRaises(ValueError): cell.account(self.root / '.artifacts/cell-input', folders, self.root)

    def test_no_candidate_fallback_and_unsupported_log(self):
        (self.root / self.name).write_text('unsupported')
        with self.assertRaises(ValueError):
            cell.make_plan(self.bundle, self.plan['sources']['catalogue'], self.config,
                           self.plan['tests']['baseline'], self.root)
        for text in ('', 'Initial test run started.', 'The final mutation score is 100 %'):
            with self.assertRaises(ValueError): cell.selection_log(text, 2)

    def test_native_backup_verification(self):
        self.reseal(lambda f, v: v.update(retained_tool_backups=[{'original_runtime_file': 'native.dll',
            'sha256': 'e' * 64, 'evidence_file': 'missing-backup'}]))
        with self.assertRaises((ValueError, OSError)): self.validate()

    def test_rehashed_omission_overlap_and_extra_filter_plans_denied(self):
        folder = self.root / 'prepared'
        (folder / 'source').mkdir(parents=True)
        replay.write(folder / 'source/bundle.json', {})
        replay.write(folder / 'source/official-index.json', self.raw)
        self.write_trx(folder / 'source/coverage')
        bundle = dict(self.bundle, workspace_root=str(self.root),
                      bundle_sha256=replay.digest((folder / 'source/bundle.json').read_bytes()))
        config_path = self.root / cell.replay_ci.replay_configuration('Windows')
        config_path.parent.mkdir(parents=True, exist_ok=True)
        replay.write(config_path, self.config)
        plan = cell.make_plan(bundle, shards.read_catalogue(self.original, self.root), self.config,
                              cell.trx(folder / 'source/coverage'), self.root)
        for group in (0, 1):
            replay.write(folder / f'cell-{group}-config.json', cell.configuration(plan, group))
        for change in (lambda p: p['tests']['groups'][0]['cases'].pop(),
                       lambda p: p['tests']['groups'][1]['cases'].extend(p['tests']['groups'][0]['cases']),
                       lambda p: p['tests']['baseline_config']['stryker-config'].update(since=True)):
            tampered = deepcopy(plan)
            change(tampered)
            tampered.pop('plan_sha256')
            tampered['plan_sha256'] = shards.sha(tampered)
            replay.write(folder / 'cell-plan.json', tampered)
            with patch.object(cell.source, 'validate_bundle', return_value=(bundle, None, None, None, Path('/tool'))):
                with self.assertRaisesRegex(ValueError, 'raw full inputs'):
                    cell.preflight(folder, self.root, self.current, Path('/sdk'), '10.0.404', 'b' * 64)

    def test_probe_restores_after_failed_private_or_baseline(self):
        for phase in ('private', 'baseline-build', 'restoration'):
            labels = []
            def execute(args, root, output, label, **kwargs):
                labels.append(label)
                if label == phase:
                    raise ValueError('synthetic baseline failure')
            def command(*args, **kwargs):
                self.assertEqual(180, kwargs['timeout'])
                self.assertEqual(120, kwargs['reserve'])
                return {'exit_code': 1, 'timeout': True, 'log': 'synthetic-private.log'}
            output = self.root / ('failure-' + phase)
            budget = unittest.mock.Mock()
            budget.execute.side_effect = execute
            budget.command.side_effect = command
            budget.records = []
            budget.started = cell.time.monotonic()
            with patch.object(cell, 'windows_context', return_value=self.current), \
                 patch.object(cell.source, 'sdk', return_value=(Path('/sdk/dotnet'), '10.0.404', 'b' * 64)), \
                 patch.object(cell.source, 'Budget', return_value=budget), \
                 patch.object(cell, 'preflight', return_value=(self.bundle, self.plan, Path('/tool'))), \
                 patch.object(cell.replay, 'workspace_hashes', return_value=self.bundle['inputs']), \
                 patch.object(cell, 'trx', return_value=self.plan['tests']['baseline']), \
                 patch.object(cell.source, 'binaries', return_value={'native.dll': 'e' * 64}), \
                 patch.object(cell.source, 'retain_tool_backups', side_effect=ValueError('backup mismatch') if phase == 'restoration' else None, return_value=[]):
                self.assertEqual(1, cell.probe(Path('/unused'), output, 0, self.root))
            value = cell.load(output / 'cell.json')
            self.assertEqual('UNRESOLVED', value['status'])
            self.assertTrue(all('restored-' + verb in labels for verb in ('clean', 'build', 'test')))
            if phase == 'restoration':
                self.assertIn('backup mismatch', value['restoration_error'])

    def test_pipeline_contract(self):
        text = (cell.ROOT / '.github/workflows/storage-test-cell-pilot.yml').read_text()
        self.assertNotIn('synchronize', text)
        self.assertNotIn('pull_request_target', text)
        self.assertIn('types: [labeled]', text)
        self.assertIn('max-parallel: 2', text)
        self.assertIn('group: [0, 1]', text)
        self.assertEqual(3, text.count('timeout-minutes: 10'))
        self.assertEqual(3, text.count('runs-on: windows-2025'))
        self.assertIn('needs: [prepare, probe]', text)
        self.assertIn('always() &&', text)
        self.assertIn('contents: read', text)
        self.assertNotIn('run-id:', text)
        existing = (cell.ROOT / '.github/workflows/storage-shard-pilot.yml').read_text()
        for action in re_find_actions(text): self.assertIn(action, existing)


def re_find_actions(text):
    import re
    return re.findall(r'uses: (\S+)', text)


if __name__ == '__main__':
    unittest.main()
