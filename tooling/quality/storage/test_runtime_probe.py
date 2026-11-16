"""Synthetic artifact/control-flow fixtures; not Windows runtime acceptance."""
from datetime import datetime, timedelta, timezone
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import MagicMock, patch
import xml.etree.ElementTree as ET

import replay
import runtime_probe as probe


class RuntimeProbeTests(unittest.TestCase):
    def fixture(self, base):
        root = base / 'repo'
        root.mkdir()
        (root / 'global.json').write_text(json.dumps({'sdk': {'version': 'synthetic-pin'}}))
        output = base / 'artifact'
        output.mkdir()
        start = datetime(2026, 11, 16, tzinfo=timezone.utc)
        def stamp(seconds):
            return (start + timedelta(seconds=seconds)).isoformat()
        data = {'schema_version': 1, 'status': 'RECOMMENDED', 'platform': 'Windows', 'head': 'a' * 40,
                'sdk_version': 'synthetic-pin', 'clean': True, 'inputs': {'source': 'hash'},
                'inputs_preserved': True, 'seconds': 0, 'start': stamp(0), 'end': stamp(0),
                'root': 'C:/repo', 'output': 'C:/output', 'dotnet': 'C:/dotnet/dotnet.exe',
                'environment_keys': ['PATH'], 'commands': [], 'groups': []}
        initial = [(['--version'], 'sdk-version'), (['--info'], 'sdk-info'),
                   (['restore', 'C:/repo/' + probe.PROJECT, '--locked-mode'], 'restore'),
                   (['build', 'C:/repo/' + probe.PROJECT, '-c', 'Release', '--no-restore'], 'build')]
        for args, name in initial:
            folder = output / name / 'run-1'
            folder.mkdir(parents=True)
            (folder / 'process.log').write_text('synthetic-pin\n' if name == 'sdk-version' else 'synthetic log')
            data['commands'].append({'args': [data['dotnet']] + args, 'directory': 'run-1', 'exit_code': 0})
        elapsed = 0
        for wave in (1, 2):
            for count, seconds in ((1, 2), (2, 2), (4, 5)):
                name = f'wave-{wave}-c{count}'
                group = {'wave': wave, 'concurrency': count, 'start': stamp(elapsed),
                         'end': stamp(elapsed + seconds), 'seconds': seconds, 'runs': [], 'completed_tests': count * 2}
                for index in range(count):
                    relative = name + '/run-' + str(index + 1)
                    directory = output / relative / 'results'
                    directory.mkdir(parents=True)
                    (directory.parent / 'process.log').write_text('synthetic process log')
                    tree = ET.Element('TestRun')
                    summary = ET.SubElement(tree, 'ResultSummary')
                    ET.SubElement(summary, 'Counters', total='2', executed='2', passed='2', failed='0', notExecuted='0')
                    definitions = ET.SubElement(tree, 'TestDefinitions')
                    results = ET.SubElement(tree, 'Results')
                    for identity in ('one', 'two'):
                        case = ET.SubElement(definitions, 'UnitTest', id=identity)
                        ET.SubElement(case, 'TestMethod', className='SyntheticSuite', name=identity)
                        ET.SubElement(results, 'UnitTestResult', testId=identity, testName=identity, outcome='Passed',
                                      startTime=stamp(elapsed), endTime=stamp(elapsed + 1), duration='00:00:01')
                    ET.ElementTree(tree).write(directory / 'full.trx', encoding='utf-8')
                    record = {'args': [data['dotnet'], 'test', 'C:/repo/' + probe.PROJECT, '-c', 'Release',
                                      '--no-build', '--no-restore', '--logger', 'trx', '--results-directory',
                                      'C:/output/' + relative + '/results'],
                              'directory': 'run-' + str(index + 1), 'start': stamp(elapsed),
                              'end': stamp(elapsed + seconds), 'exit_code': 0}
                    record['cases'] = probe.cases(directory, record['start'], record['end'])
                    group['runs'].append(record)
                elapsed += seconds
                data['groups'].append(group)
        data.update(end=stamp(elapsed), seconds=elapsed, recommendation=probe.recommendation(data['groups']))
        def seal():
            replay.write(output / 'result.json', data)
            replay.write(output / 'manifest.json', probe.inventory(output))
        seal()
        return root, output, data, seal

    def validate(self, root, output):
        with patch.object(probe.replay, 'workspace_hashes', return_value={'source': 'hash'}):
            return probe.validate(output, root, 'a' * 40)

    def test_complete_raw_contract_reopens_fourteen_identical_suites(self):
        with tempfile.TemporaryDirectory() as folder:
            root, output, data, _ = self.fixture(Path(folder))
            result = self.validate(root, output)
            self.assertEqual(result['recommendation']['selected_concurrency'], 2)
            self.assertEqual(sum(len(g['runs']) for g in result['groups']), 14)
            self.assertFalse(any('PASS' in result[k] for k in ('status',)))

    def test_invalid_bindings_scope_topology_timing_and_outcomes_are_refused(self):
        for defect in ('head', 'platform', 'sdk', 'inputs', 'dirty', 'preservation', 'whole_deadline',
                       'group_time', 'group_escape', 'duplicate_group', 'missing_run', 'filter', 'failed_process',
                       'credential', 'missing_log', 'missing_trx', 'ambiguous_trx', 'identity', 'skipped',
                       'stale', 'counter', 'missing_build', 'sdk_log', 'first_wave_only'):
            with self.subTest(defect=defect), tempfile.TemporaryDirectory() as folder:
                root, output, data, seal = self.fixture(Path(folder))
                record = data['groups'][-1]['runs'][-1]
                results = output / 'wave-2-c4/run-4/results'
                if defect in ('head', 'platform', 'sdk', 'inputs'):
                    data[{'sdk': 'sdk_version'}.get(defect, defect)] = 'wrong'
                elif defect == 'dirty': data['clean'] = False
                elif defect == 'preservation': data['inputs_preserved'] = False
                elif defect == 'whole_deadline': data['seconds'] = 901
                elif defect == 'group_time': data['groups'][0]['seconds'] = 0.01
                elif defect == 'group_escape': data['groups'][0]['wave'] = '../../outside'
                elif defect == 'duplicate_group': data['groups'][-1]['concurrency'] = 2
                elif defect == 'missing_run': data['groups'][-1]['runs'].pop()
                elif defect == 'filter': record['args'] += ['--filter', 'Subset']
                elif defect == 'failed_process': record['exit_code'] = 1
                elif defect == 'credential': data['environment_keys'] += ['GITHUB_TOKEN']
                elif defect == 'missing_log': (results.parent / 'process.log').unlink()
                elif defect == 'missing_trx': (results / 'full.trx').unlink()
                elif defect == 'ambiguous_trx': (results / 'copy.trx').write_bytes((results / 'full.trx').read_bytes())
                elif defect == 'missing_build': data['commands'].pop()
                elif defect == 'sdk_log': (output / 'sdk-version/run-1/process.log').write_text('other')
                elif defect == 'first_wave_only':
                    data['groups'][-1]['seconds'] = 1
                    data['recommendation'] = probe.recommendation(data['groups'])
                else:
                    path = results / 'full.trx'
                    tree = ET.parse(path)
                    node = tree.find('.//UnitTestResult')
                    if defect == 'identity': node.set('testName', 'different')
                    elif defect == 'skipped': node.set('outcome', 'NotExecuted')
                    elif defect == 'stale': node.set('startTime', '2020-01-01T00:00:00+00:00')
                    elif defect == 'counter': tree.find('.//Counters').set('failed', '1')
                    tree.write(path, encoding='utf-8')
                seal()
                with self.assertRaises(ValueError): self.validate(root, output)

    def test_hash_tampering_and_symlinks_are_refused(self):
        with tempfile.TemporaryDirectory() as folder:
            root, output, _, _ = self.fixture(Path(folder))
            (output / 'sdk-info/run-1/process.log').write_text('changed')
            with self.assertRaises(ValueError): self.validate(root, output)
        with tempfile.TemporaryDirectory() as folder:
            root, output, _, _ = self.fixture(Path(folder))
            target = output / 'sdk-info/run-1/process.log'
            target.unlink()
            try:
                target.symlink_to(root / 'global.json')
            except OSError:
                with patch.object(Path, 'is_symlink', autospec=True, side_effect=lambda path: path == target):
                    with self.assertRaises(ValueError): self.validate(root, output)
                return
            with self.assertRaises(ValueError): self.validate(root, output)

    def test_non_windows_refuses_before_any_dotnet_execution(self):
        with tempfile.TemporaryDirectory() as folder, patch.object(probe.platform, 'system', return_value='Darwin'), \
                patch.object(probe.subprocess, 'Popen') as launch, patch('sys.stdout', new_callable=io.StringIO):
            output = Path(folder) / 'out'
            self.assertEqual(probe.run(output, Path(folder)), 1)
            launch.assert_not_called()
            self.assertEqual(json.loads((output / 'result.json').read_bytes())['status'], 'INCONCLUSIVE')

    def test_work_deadline_prevents_launch_and_terminates_active_process(self):
        with tempfile.TemporaryDirectory() as folder, patch.object(probe.subprocess, 'Popen') as launch, \
                patch.object(probe.time, 'monotonic', side_effect=[0, 100]):
            with self.assertRaises(TimeoutError):
                probe.batch(lambda _: ['never'], 1, Path(folder) / 'batch', Path(folder), {}, 90, 120, [])
            launch.assert_not_called()
        with tempfile.TemporaryDirectory() as folder:
            proc = MagicMock()
            state = [None]
            proc.poll.side_effect = lambda: state[0]
            proc.kill.side_effect = lambda: state.__setitem__(0, -9)
            records = []
            with patch.object(probe.subprocess, 'Popen', return_value=proc), \
                    patch.object(probe.time, 'monotonic', side_effect=[0, 0, 100, 100]), \
                    patch.object(probe, 'Metrics') as metrics, \
                    patch.object(probe.subprocess, 'run', return_value=MagicMock(returncode=0)):
                metrics.return_value.finish.return_value = {'scope': 'synthetic'}
                with self.assertRaises(TimeoutError):
                    probe.batch(lambda _: ['fixture'], 1, Path(folder) / 'batch', Path(folder), {}, 90, 120, records)
            proc.kill.assert_called_once()
            self.assertEqual(records[0]['exit_code'], -9)


if __name__ == '__main__':
    unittest.main()
