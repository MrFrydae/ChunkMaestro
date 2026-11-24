"""Reject missing, duplicate, failed and skipped direct policy execution."""
import copy
import unittest
from policy_runner import validate


class PolicyRunnerTests(unittest.TestCase):
    def setUp(self):
        self.discovered = [{'Class': 'C', 'Method': 'M', 'ID': str(i), 'DisplayName': f'case-{i}'}
                           for i in range(2)]
        self.report = {'results': {'tests': [
            {'name': f'case-{i}', 'status': 'passed', 'extra': {'type': 'C', 'method': 'M'}}
            for i in range(2)], 'summary': {'tests': 2, 'passed': 2, 'failed': 0,
            'pending': 0, 'skipped': 0, 'other': 0}}}

    def test_complete_actual_cases_pass(self):
        self.assertEqual(2, validate(self.discovered, self.report)['cases'])

    def test_empty_or_duplicate_discovery_refuses(self):
        for rows in ([], [self.discovered[0], self.discovered[0]]):
            with self.assertRaises(ValueError):
                validate(rows, self.report)

    def test_missing_or_substituted_method_refuses(self):
        for missing in (True, False):
            report = copy.deepcopy(self.report)
            if missing:
                report['results']['tests'].pop()
            else:
                report['results']['tests'][0]['extra']['method'] = 'Other'
            with self.assertRaises(ValueError):
                validate(self.discovered, report)

    def test_duplicate_execution_refuses(self):
        self.report['results']['tests'][1] = self.report['results']['tests'][0]
        with self.assertRaises(ValueError):
            validate(self.discovered, self.report)

    def test_replaced_theory_row_with_same_method_count_refuses(self):
        self.report['results']['tests'][1]['name'] = 'different-case'
        with self.assertRaises(ValueError):
            validate(self.discovered, self.report)

    def test_failure_skip_pending_or_unknown_refuses(self):
        for status in ('failed', 'skipped', 'pending', 'unknown'):
            with self.subTest(status=status):
                report = copy.deepcopy(self.report)
                report['results']['tests'][0]['status'] = status
                with self.assertRaises(ValueError):
                    validate(self.discovered, report)

    def test_inconsistent_summary_refuses(self):
        for field in self.report['results']['summary']:
            with self.subTest(field=field):
                report = copy.deepcopy(self.report)
                report['results']['summary'][field] += 1
                with self.assertRaises(ValueError):
                    validate(self.discovered, report)
