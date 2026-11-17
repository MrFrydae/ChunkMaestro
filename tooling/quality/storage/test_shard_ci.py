"""Synthetic pilot boundaries; these contracts do not claim hosted execution."""
from copy import deepcopy
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import replay
import replay_ci
import shard_ci
import shards


class PilotContracts(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / 'repo'
        self.folder = Path(self.temp.name) / 'bundle'
        self.root.mkdir()
        self.folder.mkdir()
        self.code = 'src/ChunkMaestro.Persistence/Code.cs'
        self.test = 'tests/ChunkMaestro.PersistenceTests/Tests.cs'
        self.write(self.root / self.code, 'abcdefghijklmno\n')
        self.write(self.root / self.test, 'test source\n')
        self.raw = {'files': {self.code: {'source': 'abcdefghijklmno\n', 'mutants': [
            {'id': str(i), 'mutatorName': 'M' + str(i), 'replacement': 'r',
             'location': {'start': {'line': 1, 'column': 3 + i * 4},
                          'end': {'line': 1, 'column': 5 + i * 4}},
             'status': 'CompileError' if i == 2 else 'Ignored', 'statusReason': shards.FILTER_REASON}
            for i in range(3)]}}, 'testFiles': {self.test: {'source': 'test source\n',
                'tests': [{'id': 't', 'name': 'Tests.Case'}]}}}
        self.cases = [[['Tests', 'Case'], 'Tests.Case', 'Passed']]
        self.report = self.json(self.folder / 'private-index.json', self.raw)
        self.catalogue = shards.read_catalogue(self.report, self.root)
        self.config = {'stryker-config': {'mutate': shards.BASE_PATTERNS[:]}}
        self.current = {'head': 'a' * 40, 'platform': 'Darwin', 'architecture': 'arm64',
                        'run_id': '1', 'run_attempt': '1', 'trust': {'authenticated': True}}
        self.dotnet = Path(self.temp.name) / 'sdk/dotnet'
        self.write(self.dotnet, 'sdk')
        self.dotnet.chmod(0o700)
        self.sdk_hash = replay.digest(self.dotnet.read_bytes())
        self.binding = {'head': self.current['head'], 'sdk_version': '10.0.404', 'sdk_sha256': self.sdk_hash,
                        'tool_sha256': 'c' * 64, 'upstream_commit': replay.COMMIT, 'platform': 'Darwin'}
        self.plan = shards.plan(self.catalogue, self.config, self.binding, target=1)
        self.packed = shards.batch_plan(self.plan, 2)
        self.inputs = {'Code.cs': 'synthetic'}

    def write(self, path, text):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding='utf-8')
        return path

    def json(self, path, value):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(shards.canonical(value))
        return path

    def report_result(self, statuses):
        raw = deepcopy(self.raw)
        for mutant, status in zip(raw['files'][self.code]['mutants'], statuses):
            mutant['status'] = status
            if status == 'Killed':
                mutant['killedBy'] = ['t']
        return self.json(self.folder / 'probe-report.json', raw)

    def test_complete_selected_batch_remains_partial_accounting(self):
        batch = self.packed['batches'][0]
        owners = set(batch['owners'])
        statuses = ['Killed' if r['identity'] in owners and r['original']['status'] != 'CompileError' else r['original']['status'] for r in self.catalogue['rows']]
        value = shard_ci.verify_probe_report(self.report_result(statuses), self.root, self.plan, batch, self.cases)
        self.assertEqual(sorted(i for i in owners if not shard_ci.catalogue_masks(self.catalogue)[i]), value['attributed_killed'])
        self.assertNotIn('qualification', value)
        self.assertNotIn('status', value)

    def test_mutant_timeout_uncredited(self):
        batch = self.packed['batches'][0]
        statuses = ['Timeout' if r['identity'] in batch['owners'] and r['original']['status'] != 'CompileError' else r['original']['status'] for r in self.catalogue['rows']]
        result = shard_ci.verify_probe_report(self.report_result(statuses), self.root, self.plan, batch, self.cases)
        self.assertEqual([], result['attributed_killed'])
        self.assertEqual(sorted(i for i in batch['owners'] if not shard_ci.catalogue_masks(self.catalogue)[i]), result['uncredited_mutant_timeouts'])

    def test_probe_denials(self):
        batch = self.packed['batches'][0]
        for change in ('outside', 'mask', 'identity', 'source', 'unattributed', 'partial', 'unresolved'):
            with self.subTest(change=change):
                raw = deepcopy(self.raw)
                rows = raw['files'][self.code]['mutants']
                for mutant, row in zip(rows, self.catalogue['rows']):
                    if row['identity'] in batch['owners'] and mutant['status'] != 'CompileError':
                        mutant.update(status='Killed', killedBy=['t'])
                if change == 'outside':
                    for mutant, row in zip(rows, self.catalogue['rows']):
                        if row['identity'] not in batch['owners'] and mutant['status'] != 'CompileError':
                            mutant.update(status='Killed', killedBy=['t'])
                elif change == 'mask': rows[-1]['status'] = 'Ignored'
                elif change == 'identity': rows[0]['replacement'] = 'changed'
                elif change == 'source': raw['files'][self.code]['source'] += 'changed'
                elif change == 'unattributed':
                    for mutant in rows:
                        if mutant['status'] == 'Killed': mutant['killedBy'] = ['unknown']
                elif change == 'partial': rows.pop()
                elif change == 'unresolved':
                    for mutant in rows:
                        if mutant['status'] == 'Killed': mutant['status'] = 'Ignored'
                with self.assertRaises(ValueError):
                    shard_ci.verify_probe_report(self.json(self.folder / 'changed.json', raw), self.root, self.plan, batch, self.cases)

    def test_full_test_case_counts_not_only_method_sets(self):
        with self.assertRaises(ValueError):
            shard_ci.bind_tests(self.report, self.root, self.cases * 2)

    def test_index_never_accepts_actual_mutation_or_placeholder(self):
        for value in (self.report_result(['Killed', 'Ignored', 'CompileError']),):
            with self.assertRaises(ValueError):
                shard_ci.index_catalogue(value, self.root, shard_ci.expected_sources(self.root), self.cases)
        raw = deepcopy(self.raw)
        raw['files'][self.code] = {'source': 'File ignored by mutate filter', 'mutants': []}
        with self.assertRaises(ValueError):
            shard_ci.index_catalogue(self.json(self.folder / 'placeholder.json', raw), self.root,
                                     shard_ci.expected_sources(self.root), self.cases)

    def test_source_discovery_detects_omitted_file(self):
        self.write(self.root / 'src/ChunkMaestro.Persistence/Migrations/Initial.cs', 'migration')
        with self.assertRaises(ValueError):
            shard_ci.index_catalogue(self.report, self.root, shard_ci.expected_sources(self.root), self.cases)

    def test_inventory_includes_nested_bundle_json_and_rejects_symlink(self):
        self.write(self.folder / 'bundle.json', 'manifest')
        self.write(self.folder / 'nested/bundle.json', 'must be inventoried')
        value = shard_ci.inventory(self.folder)
        self.assertNotIn('bundle.json', value)
        self.assertIn('nested/bundle.json', value)
        (self.folder / 'link').symlink_to(self.report)
        with self.assertRaises(ValueError): shard_ci.inventory(self.folder)

    def test_sdk_pin_mismatch_and_executable_hash(self):
        self.json(self.root / 'global.json', {'sdk': {'version': '10.0.404'}})
        env = {'PATH': str(self.dotnet.parent), 'CHUNKMAESTRO_DOTNET': str(self.dotnet)}
        with patch('shard_ci.subprocess.check_output', return_value='10.0.404\n'):
            self.assertEqual(self.sdk_hash, shard_ci.sdk(self.root, env)[2])
        with patch('shard_ci.subprocess.check_output', return_value='wrong\n'):
            with self.assertRaises(ValueError): shard_ci.sdk(self.root, env)

    def test_stale_auth_sdk_inventory_denied_before_private_validation(self):
        bundle = dict(self.current, schema_version=1, scope=shard_ci.SCOPE, status='PREPARED_DIAGNOSTIC',
                      qualification='NOT_EVALUATED', sdk_version='10.0.404', sdk_sha256=self.sdk_hash,
                      inputs=self.inputs, environment_keys=[], inventory=shard_ci.inventory(self.folder))
        for change in ('head', 'run_id', 'trust', 'platform', 'sdk_sha256', 'inputs', 'inventory', 'qualification', 'environment_keys'):
            with self.subTest(change=change):
                wrong = deepcopy(bundle)
                wrong[change] = 'changed'
                self.json(self.folder / 'bundle.json', wrong)
                with patch('shard_ci.replay.workspace_hashes', return_value=self.inputs), \
                     patch('shard_ci.private_bundle') as private:
                    with self.assertRaises(ValueError):
                        shard_ci.validate_bundle(self.folder, self.root, self.current, self.dotnet, '10.0.404', self.sdk_hash)
                    private.assert_not_called()

    def test_bundle_full_preflight_recomputes_plan(self):
        self.json(self.root / replay_ci.replay_configuration('Darwin'), self.config)
        self.json(self.folder / 'official-index.json', self.raw)
        self.json(self.folder / 'plan.json', self.plan)
        self.json(self.folder / 'batches.json', self.packed)
        bundle = dict(self.current, schema_version=1, scope=shard_ci.SCOPE, status='PREPARED_DIAGNOSTIC',
                      qualification='NOT_EVALUATED', sdk_version='10.0.404', sdk_sha256=self.sdk_hash,
                      inputs=self.inputs, environment_keys=[], sources=shard_ci.expected_sources(self.root), baseline_cases=self.cases,
                      workspace_root=str(self.root), private_tool_sha256=self.binding['tool_sha256'], max_batches=2,
                      plan_sha256=self.plan['plan_sha256'], batch_plan_sha256=self.packed['batch_plan_sha256'],
                      selected_batch=min(self.packed['batches'], key=lambda b: (-b['weight'], b['id']))['id'],
                      inventory=shard_ci.inventory(self.folder))
        self.json(self.folder / 'bundle.json', bundle)
        with patch('shard_ci.replay.workspace_hashes', return_value=self.inputs), \
             patch('shard_ci.baseline_cases', return_value=self.cases), \
             patch('shard_ci.qualify.runtime_cases'), patch('shard_ci.validate_commands'), \
             patch('shard_ci.private_bundle', return_value=self.dotnet):
            result = shard_ci.validate_bundle(self.folder, self.root, self.current, self.dotnet, '10.0.404', self.sdk_hash)
            self.assertEqual(self.plan, result[1])
            changed = deepcopy(self.plan)
            changed['baseline_config']['stryker-config']['mutate'] = ['**/*.cs']
            changed['regions'][0]['owners'] = []
            self.json(self.folder / 'plan.json', changed)
            bundle['inventory'] = shard_ci.inventory(self.folder)
            self.json(self.folder / 'bundle.json', bundle)
            with self.assertRaises(ValueError):
                shard_ci.validate_bundle(self.folder, self.root, self.current, self.dotnet, '10.0.404', self.sdk_hash)

    def test_raw_command_ledger_filter_logs_and_report_bytes(self):
        bundle = {'original_bundle_root': str(self.folder), 'workspace_root': str(self.root),
                  'sdk_path': str(self.dotnet), 'official_tool_path': '/package/Stryker.CLI.dll',
                  'platform': 'Darwin', 'private_binary_relative': 'private-tool.dll'}
        commands = []
        def record(label, args, cwd, destination=None):
            log = self.write((destination or self.folder) / (label + '-abc.log'), 'raw ' + label)
            value = {'args': [str(a) for a in args], 'cwd': str(cwd), 'timeout': False,
                     'exit_code': 0, 'log': str(log), 'log_sha256': replay.digest(log.read_bytes())}
            commands.append(value)
            return value
        runtime = 'tests/ChunkMaestro.PersistenceTests/ChunkMaestro.PersistenceTests.csproj'
        record('restore-runtime', [self.dotnet, 'restore', runtime, '--locked-mode'], self.root)
        record('restore-tool', [self.dotnet, 'tool', 'restore'], self.root)
        record('restore-mutation-project', [self.dotnet, 'restore', shard_ci.TEST_PROJECT, '--locked-mode'], self.root)
        record('runtime-coverage', [self.dotnet, 'test', runtime, '-c', 'Release', '--no-restore',
            '--collect', 'XPlat Code Coverage', '--settings', 'tooling/quality/storage/coverage.runsettings',
            '--logger', 'trx', '--results-directory', self.folder / 'coverage'], self.root)
        private = self.folder / 'private/private-tool.dll'
        build = record('private-build', [self.dotnet, 'build', 'synthetic-pinned-project'],
                       self.folder / 'private', self.folder / 'private')
        self.json(self.folder / 'private/provenance.json', {'binary': str(private), 'build': build})
        for label, tool in (('official-index', bundle['official_tool_path']), ('private-index', private)):
            args = [self.dotnet, tool, '--config-file', self.root / replay_ci.replay_configuration('Darwin'),
                    '--mutate', '**/*.cs{0..1}']
            for exclusion in shards.EXCLUSIONS: args.extend(['--mutate', exclusion])
            args.extend(['--output', self.folder / label, '--log-to-file'])
            record(label, args, self.root / 'tooling/quality/storage')
            self.json(self.folder / label / 'reports/mutation-report.json', self.raw)
            self.json(self.folder / (label + '.json'), self.raw)
        bundle['commands'] = commands
        def ledgers():
            self.write(self.folder / 'commands.jsonl', ''.join(json.dumps(c) + '\n' for c in commands if c is not build))
            self.write(self.folder / 'private/commands.jsonl', json.dumps(build) + '\n')
        ledgers()
        shard_ci.validate_commands(self.folder, bundle, self.root)
        commands[-1]['args'][5] = '**/*.cs'
        ledgers()
        with self.assertRaises(ValueError): shard_ci.validate_commands(self.folder, bundle, self.root)
        commands[-1]['args'][5] = '**/*.cs{0..1}'
        ledgers()
        self.write(self.folder / 'private-index/reports/mutation-report.json', '{}')
        with self.assertRaises(ValueError): shard_ci.validate_commands(self.folder, bundle, self.root)
        self.json(self.folder / 'private-index/reports/mutation-report.json', self.raw)
        Path(commands[-1]['log']).write_text('tampered', encoding='utf-8')
        with self.assertRaises(ValueError): shard_ci.validate_commands(self.folder, bundle, self.root)

    def test_budget_absolute_and_restoration_reserve(self):
        with patch('shard_ci.replay.command', return_value={'exit_code': 0, 'timeout': False}) as run, \
             patch('shard_ci.time.monotonic', side_effect=[0, 100, 400]):
            budget = shard_ci.Budget(480, {'SAFE': '1'})
            budget.command(['tool'], self.root, self.folder, 'private-patched', timeout=300, reserve=90)
            self.assertEqual(290, run.call_args.args[-1])
            with self.assertRaises(ValueError):
                budget.command(['tool'], self.root, self.folder, 'private-patched', timeout=300, reserve=90)
            self.assertEqual(1, run.call_count)

    def test_private_builder_restores_command_wrapper(self):
        original = replay.command
        budget = shard_ci.Budget(420, {})
        with self.assertRaises(RuntimeError):
            with budget.private_builder():
                self.assertEqual(budget.command, replay.command)
                raise RuntimeError('synthetic builder failure')
        self.assertIs(original, replay.command)

    def test_untrusted_prepare_never_executes_and_preserves_error(self):
        output = Path(self.temp.name) / 'untrusted'
        with patch('shard_ci.context', side_effect=ValueError('untrusted')), \
             patch('shard_ci.replay.workspace_hashes', return_value=self.inputs), \
             patch('shard_ci.replay.build_private') as build:
            self.assertEqual(1, shard_ci.prepare(output, 2, self.root))
            build.assert_not_called()
        value = json.loads((output / 'bundle.json').read_bytes())
        self.assertEqual(('UNRESOLVED', 'NOT_EVALUATED', []),
                         (value['status'], value['qualification'], value['commands']))
        self.assertIn('untrusted', value['error'])

    def test_invalid_probe_preserves_diagnostic_result_without_execution(self):
        output = Path(self.temp.name) / 'untrusted-probe'
        with patch('shard_ci.context', side_effect=ValueError('untrusted')), \
             patch('shard_ci.replay.command') as run:
            self.assertEqual(1, shard_ci.probe(self.folder, output, self.root))
            run.assert_not_called()
        value = json.loads((output / 'probe.json').read_bytes())
        self.assertEqual(('UNRESOLVED', 'NOT_EVALUATED', []),
                         (value['status'], value['qualification'], value['commands']))
        self.assertNotIn('baseline_restored', value)

    def test_untrusted_context_rejects_fork_owner_mismatch(self):
        event = {'sender': {'id': replay_ci.OWNER_ID}, 'pull_request': {
            'user': {'id': replay_ci.OWNER_ID}, 'head': {'sha': self.current['head'], 'repo': {'full_name': 'attacker/fork'}},
            'base': {'repo': {'full_name': replay_ci.REPOSITORY}}}}
        path = self.json(self.folder / 'event.json', event)
        env = {'GITHUB_EVENT_PATH': str(path), 'GITHUB_EVENT_NAME': 'pull_request',
               'GITHUB_REPOSITORY': replay_ci.REPOSITORY, 'GITHUB_ACTOR_ID': str(replay_ci.OWNER_ID)}
        with patch.dict(os.environ, env, clear=True), \
             patch('shard_ci.subprocess.check_output', return_value=self.current['head']):
            with self.assertRaises(ValueError): shard_ci.context(self.root)

    def test_private_paths_sdk_and_build_log_validated(self):
        binary_relative = 'stryker-net-' + replay.COMMIT + '/src/Stryker.CLI/Stryker.CLI/bin/Release/Stryker.CLI.dll'
        binary = self.write(self.folder / 'private' / binary_relative, 'binary')
        compiler = self.write(self.dotnet.parent / 'sdk/Roslyn/compiler.dll', 'compiler')
        log = self.write(self.folder / 'private/private-build-abc.log', 'raw log')
        provenance = {'source_after_build': {shard_ci.GENERATED: replay_ci.REPORTER_SHA},
                      'compiler_files': {str(compiler): replay.digest(compiler.read_bytes())},
                      'build': {'log': str(log), 'log_sha256': replay.digest(log.read_bytes())}}
        bundle = {'provenance_sha256': 'synthetic', 'sdk_sha256': self.sdk_hash,
                  'private_binary_relative': binary_relative, 'private_tool_sha256': replay.digest(binary.read_bytes()),
                  'original_bundle_root': str(self.folder)}
        with patch('shard_ci.replay_gate.private_tool', return_value=provenance):
            self.assertEqual(binary, shard_ci.private_bundle(self.folder, bundle, self.root, self.dotnet))
            for key, value in (('private_binary_relative', '../escape'), ('private_tool_sha256', 'wrong'),
                               ('original_bundle_root', '/untrusted/root')):
                wrong = dict(bundle, **{key: value})
                with self.assertRaises(ValueError): shard_ci.private_bundle(self.folder, wrong, self.root, self.dotnet)
            compiler.write_text('changed', encoding='utf-8')
            with self.assertRaises(ValueError): shard_ci.private_bundle(self.folder, bundle, self.root, self.dotnet)


if __name__ == '__main__':
    unittest.main()
