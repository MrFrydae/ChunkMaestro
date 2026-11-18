"""Small lead-attested receipt for locally validated storage evidence, never execution."""
import json
import math
import re

MARKER = '<!-- chunkmaestro-storage-local-v1 -->'
LEAD_ID = 38875853


def validate(report, head):
    if not isinstance(report, dict):
        raise ValueError('Malformed local storage receipt.')
    if (type(report.get('schema_version')) is not int or report.get('schema_version') != 1 or report.get('head') != head or
            re.fullmatch('[0-9a-f]{40}', head) is None or report.get('status') != 'PASS' or
            report.get('collection') != 'local' or report.get('failures') != []):
        raise ValueError('Local storage receipt is failed, stale or malformed; merge blocked.')
    artifacts = report.get('artifacts', [])
    if (not isinstance(artifacts, list) or len(artifacts) != 2 or
            any(not isinstance(a, dict) for a in artifacts) or
            {a['platform'] for a in artifacts} != {'Windows', 'Darwin'} or
            any(not isinstance(a.get('location'), str) or not a['location'].strip() or
                re.fullmatch('[0-9a-f]{64}', a.get('inventory_sha256', '')) is None for a in artifacts)):
        raise ValueError('Local receipt requires retained Windows/macOS evidence inventories.')
    for module in ('Domain', 'Application', 'Persistence'):
        metrics = report['metrics'][module]
        for label, threshold, numerator, denominator in (
                ('line', 90, 'covered', 'total'), ('branch', 85, 'covered', 'total'),
                ('mutation', 80, 'killed', 'applicable')):
            metric = metrics[label]
            hit, total = metric[numerator], metric[denominator]
            if type(hit) is not int or type(total) is not int or not 0 <= hit <= total:
                raise ValueError('Invalid local storage metric counts.')
            if total == 0:
                if module != 'Application' or label == 'line' or metric['percent'] is not None:
                    raise ValueError('Missing required local storage metric.')
            elif (100 * hit / total < threshold or type(metric['percent']) not in (float, int) or
                  not math.isfinite(metric['percent']) or abs(metric['percent'] - 100 * hit / total) > 1e-8):
                raise ValueError('Local storage thresholds not met.')
    return report


def assess(comments, head):
    candidates = [c for c in comments if c.get('user', {}).get('id') == LEAD_ID and
                  c.get('body', '').startswith(MARKER)]
    if not candidates:
        raise ValueError('No lead-attested local storage qualification for this head; merge blocked.')
    latest = max(candidates, key=lambda c: c['id'])
    body = latest['body'][len(MARKER):].strip()
    if not body.startswith('```json\n') or not body.endswith('\n```'):
        raise ValueError('Malformed local storage attestation.')
    report = validate(json.loads(body[8:-4]), head)
    return {'report': report, 'attestation_url': latest['html_url'],
            'limitation': 'Lead verifies retained raw local evidence; CI authenticates the attesting account, not raw execution.'}
