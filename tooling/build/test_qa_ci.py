"""Trust, head binding and risk escalation checks for CI QA publication."""
import json
import unittest
from unittest.mock import patch

import qa_ci


class QaCiTests(unittest.TestCase):
    def test_storage_requires_latest_requested_exact_head_full_run_and_all_jobs(self):
        head = 'a' * 40
        run = {'id': 2, 'head_sha': head, 'display_title': 'Storage qualification',
               'event': 'pull_request', 'status': 'completed', 'conclusion': 'success',
               'jobs_url': 'https://api.github.com/jobs', 'html_url': 'https://github.com/run/2'}
        jobs = [{'name': name, 'conclusion': 'success'} for name in
                ('measure (windows-2025)', 'measure (macos-15)', 'gate')]
        for defect in ('none', 'missing', 'stale', 'fast', 'running', 'failure', 'skipped', 'newer-failed'):
            with self.subTest(defect=defect):
                candidate = dict(run)
                incoming_jobs = [dict(job) for job in jobs]
                if defect == 'stale': candidate['head_sha'] = 'b' * 40
                if defect == 'fast': candidate['display_title'] = 'Storage qualification not requested'
                if defect == 'running': candidate['status'] = 'in_progress'
                if defect == 'failure': candidate['conclusion'] = 'failure'
                if defect == 'skipped': incoming_jobs[2]['conclusion'] = 'skipped'
                runs = [] if defect == 'missing' else [candidate]
                if defect == 'newer-failed': runs.append(dict(run, id=3, conclusion='failure'))
                with patch.object(qa_ci, 'api', side_effect=[{'workflow_runs': runs}, {'jobs': incoming_jobs}]):
                    if defect == 'none':
                        self.assertEqual(qa_ci.storage_qualification('repos/o/r', head,
                            ['src/ChunkMaestro.Persistence/Store.cs'])['run_id'], 2)
                    else:
                        with self.assertRaises(ValueError):
                            qa_ci.storage_qualification('repos/o/r', head, ['src/ChunkMaestro.Persistence/Store.cs'])
        with patch.object(qa_ci, 'api') as api:
            self.assertIsNone(qa_ci.storage_qualification('repos/o/r', head, ['docs/readme.md']))
            api.assert_not_called()

    def setUp(self):
        self.head = 'a' * 40
        self.report = {'head': self.head, 'implementers': ['implementation-agent', 'lead'],
                       'reviews': [self.review('functional', 'reviewer-one'),
                                   self.review('security-reliability', 'reviewer-two')]}

    @staticmethod
    def review(role, identity):
        return {'role': role, 'reviewer': identity, 'verdict': 'PASS', 'commands': ['actual command'],
                'environment': 'fixture', 'evidence': ['fixture evidence'],
                'limitations': ['unit fixture only'], 'findings': ['none']}

    def comment(self, report=None, author=qa_ci.LEAD_ID, identity=1):
        return {'id': identity, 'user': {'id': author}, 'html_url': 'https://example.test/review',
                'body': qa_ci.MARKER + '\n```json\n' + json.dumps(report or self.report) + '\n```'}

    def test_accepts_exact_head_attested_independent_roles(self):
        result = qa_ci.assess([self.comment()], self.head, ['README.md'], [])
        self.assertFalse(result['high_risk'])
        self.assertEqual(result['report'], self.report)

    def test_untrusted_author_cannot_supply_approval(self):
        with self.assertRaisesRegex(ValueError, 'No lead-attested'):
            qa_ci.assess([self.comment(author=123)], self.head, [], [])

    def test_stale_head_and_latest_failed_verdict_block(self):
        with self.assertRaisesRegex(ValueError, 'exact 40-character'):
            qa_ci.assess([self.comment()], 'b' * 40, [], [])
        old = self.comment()
        self.report['reviews'][0]['verdict'] = 'FAIL'
        with self.assertRaisesRegex(ValueError, 'non-PASS'):
            qa_ci.assess([old, self.comment(identity=2)], self.head, [], [])

    def test_malformed_attestation_fails_closed(self):
        comment = self.comment()
        comment['body'] = qa_ci.MARKER + '\n{}'
        with self.assertRaises(ValueError):
            qa_ci.assess([comment], self.head, [], [])

    def test_implementer_cannot_review_own_code(self):
        self.report['reviews'][0]['reviewer'] = 'lead'
        with self.assertRaisesRegex(ValueError, 'independent'):
            qa_ci.assess([self.comment()], self.head, [], [])

    def test_high_risk_requires_third_independent_role(self):
        for files, labels in [(['src/ChunkMaestro.Persistence/Store.cs'], []),
                              (['src/Core/Enrollment.cs'], []), ([], ['high-risk'])]:
            with self.subTest(files=files, labels=labels), self.assertRaisesRegex(ValueError, 'Missing required'):
                qa_ci.assess([self.comment()], self.head, files, labels)
        self.report['reviews'].append(self.review('ux-accessibility-performance', 'reviewer-three'))
        self.assertTrue(qa_ci.assess([self.comment()], self.head,
                                    ['src/ChunkMaestro.Agent/Host.cs'], [])['high_risk'])


if __name__ == '__main__':
    unittest.main()
