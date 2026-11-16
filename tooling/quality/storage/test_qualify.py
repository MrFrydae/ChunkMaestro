import contextlib
import copy
import hashlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import qualify
import replay
import replay_gate
from qualify import ROOT, canonical, coverage, gate, mutation_key, native_evidence, runtime_cases


class GateTests(unittest.TestCase):
    def test_initial_collection_runs_full_coverage_without_repeating_persistence_mutations(self):
        # Fake commands prove scheduling only, never runtime acceptance.
        for system in ('Windows', 'Darwin'):
            with self.subTest(system=system), tempfile.TemporaryDirectory() as temporary:
                folder = Path(temporary) / 'evidence'
                calls = []
                def command(args, *unused):
                    calls.append(args)
                    return {'exit_code': 0, 'timeout': False}
                with patch.object(qualify.platform, 'system', return_value=system), \
                        patch.object(replay, 'command', side_effect=command), \
                        patch.object(qualify, 'runtime_cases'), \
                        patch('replay_ci.complete_report', return_value={'status': 'collected'}):
                    self.assertEqual(qualify.run(folder), 0)
                manifests = json.loads((folder / 'manifest.json').read_bytes())
                self.assertEqual(manifests['schema_version'], 2)
                self.assertEqual(manifests['mutation_reports'], qualify.MUTATION_REPORTS)
                mutation_calls = [c for c in calls if c[1] == 'stryker']
                self.assertEqual([Path(c[c.index('--config-file') + 1]).stem for c in mutation_calls],
                                 ['Domain', 'Application'])
                test = next(c for c in calls if c[1] == 'test')
                self.assertIn('XPlat Code Coverage', test)
                self.assertNotIn('--filter', test)
                self.assertNotIn('Persistence.json', [p.name for p in folder.iterdir()])
                with self.assertRaises(FileExistsError):
                    qualify.run(folder)

    def test_aggregate_requires_validated_pair_and_retains_original_persistence_denominator(self):
        # Synthetic aggregation contract: the replay validator has its own raw
        # artifact/provenance/attribution tests. No mutation execution is claimed.
        for defect in ('none', 'timeout', 'pair', 'empty', 'layout', 'legacy', 'module'):
            with self.subTest(defect=defect), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                folders = [root / name for name in ('Windows', 'Darwin')]
                head = 'a' * 40
                lines = {(f'src/ChunkMaestro.{module}/Example.cs', 1): True for module in qualify.MODULES}
                for folder in folders:
                    folder.mkdir()
                    manifest = {'schema_version': 2, 'mutation_reports': qualify.MUTATION_REPORTS,
                                'head': head, 'platform': folder.name, 'status': 'COLLECTED',
                                'runtime_tests_passed': True, 'clean': True, 'sources': {}}
                    if defect == 'layout':
                        manifest['mutation_reports'] = {'Persistence': 'cached.json'}
                    if defect == 'legacy':
                        manifest['schema_version'] = 1
                    (folder / 'manifest.json').write_text(json.dumps(manifest), encoding='utf-8')
                    (folder / 'volume').mkdir()
                    (folder / 'volume/volume.json').write_text(json.dumps(
                        {'status': 'PASS', 'head': head, 'platform': folder.name}), encoding='utf-8')
                    for module in qualify.INITIAL_MUTATION_MODULES:
                        source = root / f'src/ChunkMaestro.{module}/Example.cs'
                        source.parent.mkdir(parents=True, exist_ok=True)
                        source.write_bytes(b'false\n')
                        directory = folder / ('mutation-' + module)
                        directory.mkdir()
                        if defect != 'module' or module != 'Domain':
                            (directory / 'mutation-report.json').write_text(json.dumps({'files': {
                                str(source): {'source': 'false\n', 'mutants': [
                                    {'status': 'Killed', 'location': {}, 'mutatorName': 'Boolean', 'replacement': 'true'}]}}}), encoding='utf-8')
                rows = [{'file': 'src/ChunkMaestro.Persistence/Example.cs',
                         'original': {'status': 'Survived' if i < 4 else 'Timeout' if i == 4 else 'CompileError',
                                      'location': {'id': i}, 'mutatorName': 'Boolean', 'replacement': 'true'},
                         'supplemental_status': 'Killed' if i < (3 if defect == 'timeout' else 4) or i == 5 else 'UNRESOLVED_Timeout'}
                        for i in range(6)]
                with patch.object(qualify, 'ROOT', root), patch.object(qualify, 'source_hashes', return_value={}), \
                        patch.object(qualify, 'runtime_cases'), patch.object(qualify, 'native_evidence'), \
                        patch.object(qualify, 'coverage', return_value=(lines, {})), \
                        patch.object(qualify.subprocess, 'check_output', return_value=head), \
                        patch.object(replay_gate, 'validate', return_value=[] if defect == 'empty' else rows,
                                     side_effect=ValueError('pair missing') if defect == 'pair' else None) as validate, \
                        contextlib.redirect_stdout(io.StringIO()):
                    self.assertEqual(gate(folders, root / 'gate.json'), defect != 'none')
                result = json.loads((root / 'gate.json').read_bytes())
                if defect in ('none', 'timeout'):
                    metric = result['metrics']['Persistence']['mutation']
                    self.assertEqual(metric['applicable'], 5)
                    self.assertEqual(metric['compile_error_only'], 1)
                    self.assertEqual(metric['killed'], 3 if defect == 'timeout' else 4)
                    self.assertEqual(validate.call_count, 2)
                if defect in ('layout', 'legacy'):
                    validate.assert_not_called()

    def test_same_source_mutant_unions_across_platform_checkout_paths(self):
        mutant = {'location': {'start': {'line': 10, 'column': 4}, 'end': {'line': 10, 'column': 5}},
                  'mutatorName': 'Boolean mutation', 'replacement': 'false'}
        windows = mutation_key(r'D:\agent\src\ChunkMaestro.Persistence\StorageLocation.cs', mutant)
        macos = mutation_key('/Users/runner/work/src/ChunkMaestro.Persistence/StorageLocation.cs', mutant)
        self.assertEqual(windows, macos)
        statuses = {}
        statuses.setdefault(windows, set()).add('NoCoverage')
        statuses.setdefault(macos, set()).add('Killed')
        self.assertEqual(list(statuses.values()), [{'NoCoverage', 'Killed'}])
        different = dict(mutant, replacement='true')
        self.assertNotEqual(windows, mutation_key('/repo/src/ChunkMaestro.Persistence/StorageLocation.cs', different))

    def test_runtime_requires_actual_passed_upgrade_and_backup_cases(self):
        names = (
            'BackupPublicationTests.KillAfterProductionStepLeavesOnlyUnpublishedCandidateAndRecoverableOriginal',
            'BackupPublicationTests.PublishedBackupRestoresCommittedWalThroughPublicStore',
            'PopulatedUpgradeTests.PopulatedInitialUpgradePreservesAllMetadataAndCompatibleBackup',
            'PopulatedUpgradeTests.KillAfterNewMigrationDropsPopulatedEndpointsRollsBackAndRecoversSeparately')
        with tempfile.TemporaryDirectory() as root:
            folder = Path(root)
            (folder / 'coverage').mkdir()
            path = folder / 'coverage/tests.trx'
            def write(cases, outcome='Passed'):
                # Synthetic parser fixtures only, never runtime acceptance evidence.
                path.write_text('<TestRun><Counters total="4" passed="4"/>' + ''.join(
                    f'<UnitTestResult testName="Tests.{case}" outcome="{outcome}"/>' for case in cases) + '</TestRun>', encoding='utf-8')
            write(names)
            runtime_cases(folder)
            write(names[:-1])
            with self.assertRaises(ValueError):
                runtime_cases(folder)
            write(names, 'NotExecuted')
            with self.assertRaises(ValueError):
                runtime_cases(folder)

    def test_native_contract_rejects_stale_dirty_injected_or_incomplete_evidence(self):
        # Synthetic contract fixtures; no compiler/runtime/qualification claim.
        source = ROOT / 'src/ChunkMaestro.Persistence/Native/storage.c'
        runner = ROOT / 'tooling/quality/storage/native.py'
        resources = runner.with_suffix('')
        hashes = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in resources.glob('*') if p.is_file()}
        original = source.read_text(encoding='utf-8')
        catalogue = json.loads((resources / 'mutations.json').read_text(encoding='utf-8'))
        with tempfile.TemporaryDirectory() as root:
            folder = Path(root)
            totals = {'lines': {'count': 37, 'covered': 37}, 'branches': {'count': 56, 'covered': 51}}
            (folder / 'coverage.json').write_text(json.dumps({'data': [{'totals': totals}]}), encoding='utf-8')
            mutants = []
            for index, (name, before, after) in enumerate(catalogue):
                data = original.replace(before, after).encode('utf-8')
                path = folder / f'mutant-{index}.c'
                path.write_bytes(data)
                log = folder / f'mutant-{index}.json'
                log.write_text(json.dumps({'cases': [{'passed': False}]}), encoding='utf-8')
                mutants.append({'id': name, 'source': path.name, 'source_sha256': hashlib.sha256(data).hexdigest(),
                                'status': 'Killed' if index < 24 else 'Survived', 'test_log': log.name})
            manifest = {'head': 'a' * 40, 'sources': {'src/ChunkMaestro.Persistence/Native/storage.c': hashlib.sha256(source.read_bytes()).hexdigest()}}
            baseline = {'schema_version': 2, 'status': 'PASS', 'clean': True, 'head': manifest['head'], 'platform': 'Darwin',
                        'source_sha256': manifest['sources']['src/ChunkMaestro.Persistence/Native/storage.c'],
                        'runner_sha256': hashlib.sha256(runner.read_bytes()).hexdigest(), 'resources': hashes,
                        'coverage_category': 'real-filesystem-only', 'metrics': totals, 'runtime': {'failed': 0, 'passed': 42},
                        'preservation': {'source_unchanged': True, 'dylib_unchanged': True}, 'mutation': {'mutants': mutants}}
            path = folder / 'native.json'
            path.write_text(json.dumps(baseline), encoding='utf-8')
            native_evidence(folder, manifest)
            for field, value in (('head', 'b' * 40), ('clean', False), ('runner_sha256', 'bad'),
                                 ('coverage_category', 'injected'), ('resources', {})):
                with self.subTest(field=field):
                    report = copy.deepcopy(baseline)
                    report[field] = value
                    path.write_text(json.dumps(report), encoding='utf-8')
                    with self.assertRaises(ValueError):
                        native_evidence(folder, manifest)
            report = copy.deepcopy(baseline)
            report['mutation']['mutants'].pop()
            path.write_text(json.dumps(report), encoding='utf-8')
            with self.assertRaises(ValueError):
                native_evidence(folder, manifest)
            path.write_text(json.dumps(baseline), encoding='utf-8')
            (folder / mutants[0]['test_log']).write_text(json.dumps({'cases': [{'passed': True}]}), encoding='utf-8')
            with self.assertRaises(ValueError):
                native_evidence(folder, manifest)

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
