"""Trust, head binding and risk escalation checks for CI QA publication."""
import json
import unittest
from unittest.mock import patch

import qa_ci


class QaCiTests(unittest.TestCase):
    def test_storage_requires_local_receipt_without_actions_api(self):
        with patch.object(qa_ci, 'api') as api:
            with self.assertRaisesRegex(ValueError, 'local storage'):
                qa_ci.storage_qualification('repos/o/r', 'a' * 40,
                    ['src/ChunkMaestro.Persistence/Store.cs'], [])
            self.assertIsNone(qa_ci.storage_qualification('repos/o/r', 'a' * 40, ['docs/readme.md'], []))
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
