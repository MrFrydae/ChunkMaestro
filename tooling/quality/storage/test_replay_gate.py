"""Synthetic evidence parser contracts; these never establish runtime qualification."""
import copy
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import replay
import replay_ci
import replay_gate as gate


class ReplayGateTests(unittest.TestCase):
    def test_windows_host_inventories_are_portable_without_relaxing_artifact_paths(self):
        original = {r'src\Tool\bin\Tool.dll': 'hash'}
        normalized = gate.host_inventory(original)
        self.assertEqual(normalized, {'src/Tool/bin/Tool.dll': 'hash'})
        self.assertEqual(original, {r'src\Tool\bin\Tool.dll': 'hash'})
        for bad in ({r'..\escape': 'hash'}, {r'C:\host\file': 'hash'},
                    {r'\\server\share': 'hash'}, {'a/../b': 'hash'},
                    {r'a\b': 'hash', 'a/b': 'hash'}, {}):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                gate.host_inventory(bad)
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / 'nested').mkdir()
            (root / 'nested/tool').write_bytes(b'payload')
            gate.verify_files(root, gate.host_inventory({r'nested\tool': replay.digest(b'payload')}))
            with self.assertRaises(ValueError):
                gate.verify_files(root, {r'nested\tool': replay.digest(b'payload')})

    def test_credit_preserves_original_denominator_and_never_promotes_timeouts(self):
        rows = [{'original': {'status': original}, 'supplemental_status': supplemental}
                for original, supplemental in (('Ignored', 'Killed'), ('Killed', 'Survived'),
                    ('CompileError', 'Killed'), ('Timeout', 'UNRESOLVED_Timeout'),
                    ('Survived', 'UNRESOLVED_CompileError'), ('NoCoverage', 'UNRESOLVED_NO_FAILED_TEST'))]
        self.assertEqual(gate.credited(rows), rows[:2])
        self.assertEqual(len(rows), 6)  # No row/status is removed or rewritten.
        self.assertEqual(rows[2]['original']['status'], 'CompileError')

    def fixture(self, base):
        root, folder = base / 'repo', base / 'artifact'
        root.mkdir()
        folder.mkdir()
        def put(path, data):
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
        source = 'src/ChunkMaestro.Persistence/Example.cs'
        test = 'tests/ChunkMaestro.PersistenceTests/ExampleTests.cs'
        for name, data in ((source, b'false\n'), (test, b'test source\n'),
                           ('tooling/quality/storage/replay.py', b'runner'),
                           (gate.WINDOWS_CONFIG, b'configuration'), ('global.json', b'{"sdk":{"version":"pin"}}')):
            put(root / name, data)
        directory = folder / 'replay'
        directory.mkdir()
        trx = ('<TestRun><Counters total="1" executed="1" passed="1"/>'
               '<TestDefinitions><UnitTest id="id"><TestMethod className="Tests" name="Works"/></UnitTest></TestDefinitions>'
               '<UnitTestResult testId="id" testName="Tests.Works" outcome="Passed"/></TestRun>').encode()
        for sub in ('baseline', 'restored'):
            put(directory / sub / 'tests.trx', trx)
        put(folder / 'coverage/tests.trx', trx)
        mutant = {'id': '1', 'status': 'Survived', 'mutatorName': 'Boolean', 'replacement': 'true',
                  'location': {'start': {'line': 1, 'column': 1}, 'end': {'line': 1, 'column': 6}}}
        report = {'files': {'C:\\agent\\' + source.replace('/', '\\'): {'source': 'false\n', 'mutants': [mutant]}},
                  'testFiles': {'C:/agent/' + test: {'source': 'test source\n', 'tests': [{'id': 'id', 'name': 'Tests.Works'}]}}}
        replay.write(directory / 'official.json', report)
        mutated = copy.deepcopy(report)
        patched = next(iter(mutated['files'].values()))['mutants'][0]
        patched.update(status='Killed', killedBy=['id'])
        replay.write(directory / 'private-patched.json', mutated)
        for label in ('official', 'private-patched'):
            put(directory / label / 'reports/mutation-report.json', (directory / (label + '.json')).read_bytes())
        for label in ('baseline', 'restored'):
            replay.write(directory / (label + '-binaries.json'), {'bin/ChunkMaestro.' + name + '.dll': 'hash'
                         for name in ('Domain', 'Application', 'Persistence', 'PersistenceTests')})
        original = gate.catalogue(directory / 'official.json', root)
        supplement = gate.catalogue(directory / 'private-patched.json', root)
        replay.write(directory / 'catalogue.json', original)
        replay.write(directory / 'patched-catalogue.json', supplement)
        (directory / 'ledger.jsonl').write_text('\n'.join(json.dumps(row) for row in original + list(replay.supplement(original, supplement))), encoding='utf-8')
        private = folder / 'replay-private'
        put(private / 'build.log', b'build log')
        put(private / 'tool.dll', b'binary')
        prefix = 'C:/temp/collection/'
        provenance = {'binary': prefix + 'replay-private/tool.dll',
                      'dependencies_and_outputs': {'src/Stryker.CLI/Stryker.CLI/bin/Release/Stryker.CLI.dll': replay.digest(b'binary')},
                      'build': {'log': prefix + 'replay-private/build.log', 'log_sha256': replay.digest(b'build log')}}
        commands = []
        for index, verb in enumerate(('restore', 'clean', 'build', 'test', 'official', 'private', 'clean', 'build', 'test')):
            put(directory / f'{index}.log', b'log')
            args = ['dotnet', verb, 'C:/agent/tooling/quality/storage/MutationTests.csproj']
            if index in (4, 5):
                args = ['dotnet', provenance['binary'] if index == 5 else 'C:/tool/Stryker.CLI.dll',
                        '--config-file', 'C:/agent/' + gate.WINDOWS_CONFIG]
            commands.append({'args': args, 'exit_code': 2 if index == 4 else 0, 'timeout': False,
                             'log': prefix + f'replay/{index}.log', 'log_sha256': replay.digest(b'log')})
        (directory / 'commands.jsonl').write_text('\n'.join(map(json.dumps, commands)), encoding='utf-8')
        result = {'schema_version': 1, 'category': 'production-experiment', 'status': 'COLLECTED', 'head': 'a' * 40,
                  'baseline_restored': True, 'inputs_preserved': True, 'architecture': 'synthetic',
                  'thresholds': {'line': 90, 'branch': 85, 'mutation': 80}, 'mutation_directory': 'tooling/quality/storage',
                  'inputs': replay.hashes(root), 'runner_sha256': replay.digest(b'runner'),
                  'config_sha256': replay.digest(b'configuration'), 'sdk_version': 'pin',
                  'baseline_cases': json.loads(json.dumps(replay.test_cases(directory / 'baseline'))),
                  'official_report_sha256': replay.digest((directory / 'official.json').read_bytes()),
                  'original_counts': {'Survived': 1}, 'fixed_applicable_denominator': 1,
                  'official_tool_files': {'Stryker.CLI.dll': 'hash'}, 'official_binary_sha256': 'hash',
                  'tool_threshold_failures': {'official': True, 'private-patched': False}}
        replay.write(directory / 'result.json', result)
        replay.write(directory / 'binding.json', {'status': 'UNRESOLVED', 'head': result['head'], 'inputs': result['inputs']})
        collection = {'schema_version': 1, 'status': 'COLLECTED', 'platform': 'Windows', 'private_identity': gate.IDENTITY,
                      'trust': {'head': 'a' * 40, 'repository': replay_ci.REPOSITORY, 'actor_id': replay_ci.OWNER_ID, 'event': 'pull_request'},
                      'environment_keys': ['PATH'], 'architecture': 'synthetic', 'sdk_version': 'pin', 'original_temporary_root': prefix.rstrip('/')}
        def seal():
            data = json.loads((directory / 'result.json').read_bytes())
            data['artifacts'] = {k: v for k, v in replay.hashes(directory).items() if k != 'result.json'}
            replay.write(directory / 'result.json', data)
            collection['relocated'] = {name: replay_ci.inventory(folder / name) for name in ('replay', 'replay-private')}
            replay.write(folder / 'replay-collection.json', collection)
        seal()
        return root, folder, provenance, collection, seal

    def test_complete_relocated_contract_and_fail_closed_bindings(self):
        for change in ('none', 'probe', 'head', 'fixture', 'restoration', 'timeout', 'ledger', 'test', 'inputs', 'inventory', 'credential'):
            with self.subTest(change=change), tempfile.TemporaryDirectory() as temporary:
                root, folder, provenance, collection, seal = self.fixture(Path(temporary))
                result_path = folder / 'replay/result.json'
                result = json.loads(result_path.read_bytes())
                if change == 'head':
                    collection['trust']['head'] = 'b' * 40
                elif change == 'fixture':
                    result['category'] = 'fixture-only'
                elif change == 'restoration':
                    result['baseline_restored'] = False
                elif change == 'timeout':
                    path = folder / 'replay/commands.jsonl'
                    commands = list(map(json.loads, path.read_text().splitlines()))
                    commands[5]['timeout'] = True
                    path.write_text('\n'.join(map(json.dumps, commands)))
                elif change == 'ledger':
                    (folder / 'replay/ledger.jsonl').write_text('')
                elif change == 'test':
                    path = folder / 'replay/restored/tests.trx'
                    path.write_bytes(path.read_bytes().replace(b'Passed', b'NotExecuted'))
                elif change == 'inputs':
                    result['inputs'] = {}
                elif change == 'credential':
                    collection['environment_keys'].append('GITHUB_TOKEN')
                replay.write(result_path, result)
                seal()  # Rehashing cannot make semantically invalid evidence valid.
                if change == 'inventory':
                    (folder / 'replay/0.log').write_bytes(b'tampered')
                with patch.object(gate.runtime_probe, 'validate', return_value={},
                                  side_effect=ValueError('invalid probe') if change == 'probe' else None), \
                        patch.object(gate, 'private_tool', return_value=provenance), \
                        patch.object(replay, 'workspace_hashes', side_effect=lambda r, fixture: replay.hashes(r)):
                    if change == 'none':
                        rows = gate.validate(folder, {'head': 'a' * 40, 'platform': 'Windows'}, root)
                        self.assertEqual(rows[0]['supplemental_status'], 'Killed')
                    else:
                        with self.assertRaises(ValueError):
                            gate.validate(folder, {'head': 'a' * 40, 'platform': 'Windows'}, root)

    def test_portable_catalogue_rejects_duplicate_source_and_unbound_kills(self):
        with tempfile.TemporaryDirectory() as temporary:
            root, folder, _, _, _ = self.fixture(Path(temporary))
            path = folder / 'replay/private-patched.json'
            report = json.loads(path.read_bytes())
            next(iter(report['files'].values()))['mutants'][0]['killedBy'] = ['unknown']
            replay.write(path, report)
            with self.assertRaises(ValueError):
                gate.test_bindings(path, root, replay.test_cases(folder / 'replay/baseline'))
            report['files']['/other/src/ChunkMaestro.Persistence/Example.cs'] = copy.deepcopy(next(iter(report['files'].values())))
            replay.write(path, report)
            with self.assertRaises(ValueError):
                gate.catalogue(path, root)
            for name in ('../escape', '/absolute', 'C:/host', 'a/../../host', 'a\\host'):
                with self.assertRaises(ValueError):
                    gate.child(folder, name)


if __name__ == '__main__':
    unittest.main()
