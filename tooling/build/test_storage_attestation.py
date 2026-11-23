"""Synthetic receipt trust and denial contracts; not storage qualification."""
from copy import deepcopy
import json
import unittest
import storage_attestation as storage


class LocalAttestationTests(unittest.TestCase):
    def setUp(self):
        self.head = 'a' * 40
        self.report = {'schema_version': 1, 'head': self.head, 'status': 'PASS', 'failures': [],
            'collection': 'local', 'artifacts': [{'platform': p, 'location': '/retained/' + p,
                'inventory_sha256': 'b' * 64} for p in ('Windows', 'Darwin')], 'metrics': {}}
        for module in ('Domain', 'Application', 'Persistence'):
            self.report['metrics'][module] = {'line': {'covered': 90, 'total': 100, 'percent': 90},
                'branch': {'covered': 85, 'total': 100, 'percent': 85},
                'mutation': {'killed': 80, 'applicable': 100, 'percent': 80}}

    def comment(self, report, identity=1, owner=storage.LEAD_ID):
        return {'id': identity, 'user': {'id': owner}, 'html_url': 'https://example.test/receipt',
                'body': storage.MARKER + '\n```json\n' + json.dumps(report) + '\n```'}

    def test_exact_owner_receipt_and_application_not_applicable(self):
        self.assertEqual(storage.assess([self.comment(self.report)], self.head)['report'], self.report)
        for label, count in (('branch', 'total'), ('mutation', 'applicable')):
            metric = self.report['metrics']['Application'][label]
            metric.update({count: 0, 'covered' if label == 'branch' else 'killed': 0, 'percent': None})
        storage.validate(self.report, self.head)

    def test_missing_untrusted_stale_failed_or_latest_failed_refuse(self):
        for comments in ([], [self.comment(self.report, owner=123)],
            [self.comment(dict(self.report, head='c' * 40))],
            [self.comment(self.report), self.comment(dict(self.report, status='FAIL'), identity=2)]):
            with self.assertRaises(ValueError): storage.assess(comments, self.head)

    def test_threshold_counts_and_percent_tampering_refuse(self):
        for label, key, value in (('line', 'covered', 89), ('branch', 'covered', 84),
            ('mutation', 'killed', 79), ('mutation', 'killed', True),
            ('line', 'percent', float('nan')), ('line', 'percent', 100)):
            report = deepcopy(self.report)
            report['metrics']['Persistence'][label][key] = value
            with self.assertRaises(ValueError): storage.validate(report, self.head)

    def test_platform_evidence_and_missing_required_metrics_refuse(self):
        for change in (lambda r: r.update(artifacts=[]),
            lambda r: r['artifacts'][0].update(inventory_sha256='wrong'),
            lambda r: r['artifacts'][0].update(platform='Darwin'),
            lambda r: r['metrics']['Persistence']['mutation'].update(killed=0, applicable=0, percent=None)):
            report = deepcopy(self.report); change(report)
            with self.assertRaises(ValueError): storage.validate(report, self.head)

    def test_hosted_windows_exception_preserves_threshold_and_owner_run_binding(self):
        report = deepcopy(self.report)
        report['collection'] = 'local-macos-hosted-windows'
        windows = report['artifacts'][0]
        windows.update(session_sha256='c' * 64, session_location='/retained/session.json', hosted={
            'repository': 'MrFrydae/ChunkMaestro', 'actor_id': storage.LEAD_ID,
            'event': 'workflow_dispatch', 'head': self.head, 'workflow_sha': self.head,
            'execution': 'github-hosted', 'authorization': 'PR6-one-Windows-run',
            'workflow': '.github/workflows/ci.yml', 'run_id': '123', 'run_attempt': 1,
            'run_url': 'https://github.com/MrFrydae/ChunkMaestro/actions/runs/123'})
        storage.validate(report, self.head)
        for field, value in [('actor_id', 1), ('head', 'b' * 40), ('workflow_sha', 'b' * 40),
            ('run_id', ''), ('run_attempt', 2), ('event', 'pull_request'), ('execution', 'local'),
            ('authorization', 'wrong'), ('run_url', 'https://example.test')]:
            changed = deepcopy(report)
            changed['artifacts'][0]['hosted'][field] = value
            with self.assertRaises(ValueError): storage.validate(changed, self.head)
        changed = deepcopy(report)
        changed['metrics']['Persistence']['mutation'].update(killed=79, percent=79)
        with self.assertRaises(ValueError): storage.validate(changed, self.head)
        changed = deepcopy(report); changed['collection'] = 'local'
        with self.assertRaises(ValueError): storage.validate(changed, self.head)
        changed = deepcopy(report); del changed['artifacts'][0]['session_sha256']
        with self.assertRaises(ValueError): storage.validate(changed, self.head)
