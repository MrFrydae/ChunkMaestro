"""Trust, head binding and risk escalation checks for CI QA publication."""
import json
import io
import os
from contextlib import redirect_stdout
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import qa_ci


class QaCiTests(unittest.TestCase):
    def test_native_failure_blocks_even_a_complete_attestation_and_green_storage(self):
        self.report['reviews'].append(self.review('ux-accessibility-performance', 'reviewer-three'))

        def api(path, pages=False):
            if path.endswith('/comments'):
                return [self.comment()]
            if path.endswith('/files'):
                return [{'filename': 'src/ChunkMaestro.Inspector.Native/metadata.c'}]
            return {'head': {'sha': self.head}, 'labels': []}

        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            for native in (None, '', 'failure', 'skipped', 'cancelled', 'in_progress', 'success'):
                env = {'GITHUB_REPOSITORY': 'MrFrydae/ChunkMaestro', 'REVIEW_PR': '22',
                       'REVIEW_HEAD': self.head, 'STORAGE_SAFETY_RESULT': 'success'}
                if native is not None:
                    env['INSPECTOR_METADATA_RESULT'] = native
                with self.subTest(native=native), patch.dict(os.environ, env, clear=True), \
                        patch.object(qa_ci, 'api', side_effect=api), \
                        patch.object(qa_ci, 'Path', return_value=output), redirect_stdout(io.StringIO()):
                    code = qa_ci.main()
                    verdict = json.loads((output / 'verdict.json').read_text())
                    self.assertEqual('PASS', verdict['storage_execution']['verdict'])
                    if native == 'success':
                        self.assertEqual(0, code)
                        self.assertEqual('PASS', verdict['verdict'])
                        self.assertEqual('PASS', verdict['inspector_execution']['verdict'])
                        self.assertTrue(verdict['high_risk'])
                        self.assertEqual(self.report, verdict['report'])
                    else:
                        self.assertEqual(1, code)
                        self.assertEqual('FAIL', verdict['verdict'])
                        self.assertIn('Linux inspector metadata', verdict['error'])

    def test_storage_requires_successful_same_workflow_execution(self):
        with patch.object(qa_ci, 'api') as api:
            for result in (None, '', 'failure', 'skipped', 'cancelled'):
                with self.assertRaisesRegex(ValueError, 'Direct storage'):
                    qa_ci.storage_execution(result)
            self.assertEqual('PASS', qa_ci.storage_execution('success')['verdict'])
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
                              (['src/ChunkMaestro.Contracts/InspectCodec.cs'], []),
                              (['src/ChunkMaestro.Inspector.Native/metadata.c'], []),
                              (['tests/ChunkMaestro.InspectorNativeTests/run.sh'], []),
                              (['src/ChunkMaestro.OwnerVerification/OwnerContext.cs'], []),
                              (['src/Core/Enrollment.cs'], []), ([], ['high-risk'])]:
            with self.subTest(files=files, labels=labels), self.assertRaisesRegex(ValueError, 'Missing required'):
                qa_ci.assess([self.comment()], self.head, files, labels)
        self.report['reviews'].append(self.review('ux-accessibility-performance', 'reviewer-three'))
        self.assertTrue(qa_ci.assess([self.comment()], self.head,
                                    ['src/ChunkMaestro.Agent/Host.cs'], [])['high_risk'])


if __name__ == '__main__':
    unittest.main()
