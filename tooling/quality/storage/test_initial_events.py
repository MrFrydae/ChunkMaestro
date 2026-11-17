"""Synthetic protocol denials; actual runtime proof lives outside the checkout."""
from copy import deepcopy
from datetime import datetime, timezone
import json
from pathlib import Path
import tempfile
import unittest
import uuid
from unittest.mock import patch

import initial_events as events


class InitialEventsTests(unittest.TestCase):
    def setUp(self):
        self.start = datetime(2026, 11, 17, 12, 0, tzinfo=timezone.utc)
        self.end = datetime(2026, 11, 17, 12, 1, tzinfo=timezone.utc)
        self.cases = [{'fqn': 'Tests.Theory', 'display': 'Tests.Theory(row: 1)'},
                      {'fqn': 'Tests.Theory', 'display': 'Tests.Theory(row: 2)'}]
        self.assembly = '/fixture/Tests.dll'
        self.results = [self.result(case) for case in self.cases]
        self.begin = {'Version': 7, 'MessageType': 'TestExecution.StartWithSources', 'Payload': {
            'AdapterSourceMap': {'_none_': [self.assembly]},
            'RunSettings': '<RunSettings><RunConfiguration><MaxCpuCount>1</MaxCpuCount><DisableParallelization>true</DisableParallelization></RunConfiguration></RunSettings>',
            'TestExecutionContext': {'TestCaseFilter': None, 'KeepAlive': False}}}
        self.done = {'Version': 7, 'MessageType': 'TestExecution.Completed', 'Payload': {
            'TestRunCompleteArgs': {'IsCanceled': False, 'IsAborted': False, 'Error': None,
                                    'TestRunStatistics': {'ExecutedTests': 2, 'Stats': {'Passed': 2}}},
            'ExecutorUris': [events.EXECUTOR], 'LastRunTests': {'NewTestResults': self.results, 'ActiveTests': [], 'TestRunStatistics': {'ExecutedTests': 2, 'Stats': {'Passed': 2}}}}}

    def result(self, case):
        return {'TestCase': {'Id': str(uuid.uuid4()), 'FullyQualifiedName': case['fqn'],
                            'DisplayName': case['display'], 'Source': self.assembly, 'ExecutorUri': events.EXECUTOR},
                'Outcome': 1, 'DisplayName': case['display'], 'ErrorMessage': None, 'ErrorStackTrace': None,
                'StartTime': '2026-11-17T12:00:02+00:00', 'EndTime': '2026-11-17T12:00:03+00:00'}

    def raw(self, messages):
        return ''.join(f'TpTrace Verbose: 0 : 123, 4, 2026/11/17, 12:00:{index*5+1:02d}.000, {index+1}, vstest.console.dll, ' +
                       (events.SEND if message['MessageType'].startswith('TestExecution.Start') else events.RECEIVE) + json.dumps(message) + '\n'
                       for index, message in enumerate(messages)).encode()

    def inspect(self, messages):
        return events.inspect_wire(self.raw(messages), self.start, self.end, self.cases, self.assembly)

    def test_affirmative_final_chunk(self):
        self.assertEqual(len(self.inspect([self.begin, self.done])['cases']), 2)

    def test_disjoint_batch_and_final_chunk(self):
        done = deepcopy(self.done)
        done['Payload']['LastRunTests']['NewTestResults'] = [self.results[1]]
        batch = {'Version': 7, 'MessageType': 'TestExecution.StatsChange', 'Payload': {'NewTestResults': [self.results[0]], 'ActiveTests': [], 'TestRunStatistics': {'ExecutedTests': 1, 'Stats': {'Passed': 1}}}}
        self.assertEqual(len(self.inspect([self.begin, batch, done])['cases']), 2)

    def test_identical_duplicate_batch_denied(self):
        batch = {'Version': 7, 'MessageType': 'TestExecution.StatsChange', 'Payload': {'NewTestResults': [self.results[0]], 'ActiveTests': [], 'TestRunStatistics': {'ExecutedTests': 1, 'Stats': {'Passed': 1}}}}
        with self.assertRaisesRegex(ValueError, 'duplicate'):
            self.inspect([self.begin, batch, self.done])

    def test_outcomes_fail_closed(self):
        for outcome in (0, 2, 3, 4, None, 'Passed', True):
            with self.subTest(outcome=outcome):
                done = deepcopy(self.done)
                done['Payload']['LastRunTests']['NewTestResults'][0]['Outcome'] = outcome
                with self.assertRaises(ValueError):
                    self.inspect([self.begin, done])

    def test_cancel_abort_error_statistics(self):
        for field, value in [('IsCanceled', True), ('IsAborted', True), ('Error', 'failure'), ('TestRunStatistics', {'ExecutedTests': 2, 'Stats': {'Skipped': 2}})]:
            with self.subTest(field=field):
                done = deepcopy(self.done)
                done['Payload']['TestRunCompleteArgs'][field] = value
                with self.assertRaises(ValueError):
                    self.inspect([self.begin, done])

    def test_missing_completion_results_and_count_only(self):
        with self.assertRaises(ValueError):
            self.inspect([self.begin])
        for results in ([], [self.results[0]], self.results + [self.results[0]]):
            done = deepcopy(self.done)
            done['Payload']['LastRunTests']['NewTestResults'] = results
            with self.assertRaises(ValueError):
                self.inspect([self.begin, done])

    def test_identity_source_adapter_time_and_duplicate_id(self):
        mutations = [lambda r: r['TestCase'].update(Source='/other'),
                     lambda r: r['TestCase'].update(ExecutorUri='unsupported'),
                     lambda r: r['TestCase'].update(DisplayName='row: 3'),
                     lambda r: r.update(DisplayName='conflicting'),
                     lambda r: r['TestCase'].update(Id=self.results[1]['TestCase']['Id']),
                     lambda r: r.update(StartTime='2026-11-16T12:00:00Z'),
                     lambda r: r.update(ErrorMessage='error')]
        for mutate in mutations:
            done = deepcopy(self.done)
            mutate(done['Payload']['LastRunTests']['NewTestResults'][0])
            with self.assertRaises(ValueError):
                self.inspect([self.begin, done])

    def test_sessions_unbound_retried_concurrent_and_repeated(self):
        for messages in ([self.done], [self.begin, self.begin, self.done], [self.begin, self.done, self.done]):
            with self.assertRaises(ValueError):
                self.inspect(messages)
        with self.assertRaises(ValueError):
            events.inspect_wire(self.raw([self.begin, self.done]).replace(b'123, 4, 2026/11/17, 12:00:06', b'456, 4, 2026/11/17, 12:00:06'), self.start, self.end, self.cases, self.assembly)

    def test_unsupported_version_coverage_timeout_filter(self):
        for settings in ('<RunSettings><TestSessionTimeout>1</TestSessionTimeout></RunSettings>', '<RunSettings><InProcDataCollectors/></RunSettings>'):
            begin = deepcopy(self.begin)
            begin['Payload']['RunSettings'] = settings
            with self.assertRaises(ValueError):
                self.inspect([begin, self.done])
        begin = deepcopy(self.begin)
        begin['Version'] = 8
        with self.assertRaises(ValueError):
            self.inspect([begin, self.done])

    def test_filtered_selection_matches_exact_expected_filter(self):
        expected = 'FullyQualifiedName=Tests.Theory'
        begin = deepcopy(self.begin)
        begin['Payload']['TestExecutionContext']['TestCaseFilter'] = expected
        self.assertEqual(2, len(events.inspect_wire(self.raw([begin, self.done]), self.start, self.end,
            self.cases, self.assembly, expected)['cases']))
        for wrong in (None, 'FullyQualifiedName=Tests.Other'):
            with self.assertRaisesRegex(ValueError, 'selection'):
                events.inspect_wire(self.raw([begin, self.done]), self.start, self.end,
                    self.cases, self.assembly, wrong)

    def test_statistics_do_not_coerce_boolean_or_fractional_counts(self):
        for count in (True, 2.0, '2'):
            done = deepcopy(self.done)
            done['Payload']['TestRunCompleteArgs']['TestRunStatistics']['ExecutedTests'] = count
            with self.assertRaisesRegex(ValueError, 'statistics'):
                self.inspect([self.begin, done])

    def test_missing_trace_and_duplicate_plan(self):
        for raw in (b'', b'count=2 passed', b'[RAW] unsupported'):
            with self.assertRaises(ValueError):
                events.inspect_wire(raw, self.start, self.end, self.cases, self.assembly)
        with self.assertRaises(ValueError):
            events.inspect_wire(self.raw([self.begin, self.done]), self.start, self.end, self.cases*2, self.assembly)

    def test_strict_json_and_hash_tamper(self):
        with self.assertRaises(ValueError):
            events.load_json('{"Outcome":1,"Outcome":3}')
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'raw'
            path.write_bytes(b'original')
            expected = events.digest(path.read_bytes())
            path.write_bytes(b'tampered')
            with self.assertRaises(ValueError):
                events.read_bound(path, expected)

    def test_public_api_missing_bindings_no_credit(self):
        for binding in ({}, {'source_commit': 'wrong'}):
            result = events.validate_initial_events(binding)
            self.assertEqual(result['status'], 'UNRESOLVED')
            self.assertFalse(result['credit'])
            self.assertFalse(result['initial_cases_passed'])

    def test_diagnostic_size_bound_refuses_before_parsing(self):
        raw = self.raw([self.begin, self.done])
        with patch.object(events, 'MAX_LOG_BYTES', len(raw) - 1):
            with self.assertRaisesRegex(ValueError, 'bound'):
                events.inspect_wire(raw, self.start, self.end, self.cases, self.assembly)
            with self.assertRaisesRegex(ValueError, 'bound'):
                events.phase_window(raw)


if __name__ == '__main__':
    unittest.main()
