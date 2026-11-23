"""Publish exact-head, lead-attested independent QA as a blocking CI artifact.

The lead validates delegate provenance and posts the aggregate report in a PR
comment. This gate authenticates that GitHub account, not distinct human reviewers.
It never executes comment content and grants no approval or write permissions.
"""
import json
import os
from pathlib import Path
import re
import subprocess
import sys

from qa_evidence import validate

MARKER = '<!-- chunkmaestro-qa-v1 -->'
# Verified repository owner id. Changes to the attestation authority require review.
LEAD_ID = 38875853
RISK_PREFIXES = ('src/ChunkMaestro.Agent/', 'src/ChunkMaestro.Transport/',
                 'src/ChunkMaestro.PrivilegedHelper/', 'src/ChunkMaestro.Persistence/',
                 'src/ChunkMaestro.Backups/', 'src/ChunkMaestro.Deployments/', 'packaging/',
                 'tooling/build/storage_attestation.py', 'tooling/ci/', '.github/workflows/')
RISK_WORDS = ('enrollment', 'credential', 'secret', 'certificate', 'schedule',
              'restore', 'authorization', 'updater')



def high_risk(files, labels):
    return ('high-risk' in labels or any(
        file.startswith(RISK_PREFIXES) or any(word in file.lower() for word in RISK_WORDS)
        for file in files))


class AssessmentError(ValueError):
    def __init__(self, message, evidence):
        super().__init__(message)
        self.evidence = evidence


def assess(comments, head, files, labels):
    candidates = [comment for comment in comments
                  if comment.get('user', {}).get('id') == LEAD_ID
                  and comment.get('body', '').startswith(MARKER)]
    if not candidates:
        raise ValueError('No lead-attested independent QA report for this PR. Merge blocked.')
    latest = max(candidates, key=lambda comment: comment['id'])
    payload = latest['body'][len(MARKER):].strip()
    if not payload.startswith('```json\n') or not payload.endswith('\n```'):
        raise ValueError('Latest lead attestation must contain one JSON code block.')
    report = json.loads(payload[len('```json\n'):-len('\n```')])
    evidence = {'report': report, 'attestation_url': latest['html_url'],
                'attesting_account_id': LEAD_ID, 'high_risk': high_risk(files, labels)}
    try:
        validate(report, head, evidence['high_risk'])
    except ValueError as error:
        raise AssessmentError(str(error), evidence) from error
    return evidence


def api(path, pages=False):
    args = ['gh', 'api', path]
    if pages:
        args.extend(['--paginate', '--slurp'])
    result = subprocess.run(args, check=True, capture_output=True, text=True)
    data = json.loads(result.stdout)
    return [item for page in data for item in page] if pages else data


def storage_execution(result):
    """Direct same-workflow safety results replace the owner mutation receipt."""
    if result != 'success':
        raise ValueError('Direct storage safety checks did not pass; inspect storage-safety artifacts and job summary.')
    return {'verdict': 'PASS', 'source': 'same-workflow direct storage safety job'}


def main():
    output = Path('.artifacts/qa')
    output.mkdir(parents=True, exist_ok=True)
    result = {'verdict': 'FAIL', 'head': os.environ.get('REVIEW_HEAD'),
              'limitation': 'Agent provenance is verified by the lead; not distinct GitHub human approvals.'}
    try:
        repository = os.environ['GITHUB_REPOSITORY']
        if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository):
            raise ValueError('Invalid repository.')
        number = int(os.environ['REVIEW_PR'])
        head = os.environ['REVIEW_HEAD']
        prefix = f'repos/{repository}'
        pr = api(f'{prefix}/pulls/{number}')
        if pr['head']['sha'] != head:
            raise ValueError('Workflow head is stale; run checks for the current PR head.')
        comments = api(f'{prefix}/issues/{number}/comments', pages=True)
        files = [item['filename'] for item in api(f'{prefix}/pulls/{number}/files', pages=True)]
        labels = [item['name'] for item in pr['labels']]
        result['storage_execution'] = storage_execution(os.environ.get('STORAGE_SAFETY_RESULT'))
        result.update(assess(comments, head, files, labels))
        result['verdict'] = 'PASS'
    except (KeyError, ValueError, TypeError, subprocess.CalledProcessError) as error:
        if isinstance(error, AssessmentError):
            result.update(error.evidence)
        # Do not echo API responses, comment bodies, subprocess stdout or credentials.
        result['error'] = (str(error) if isinstance(error, ValueError)
                           else 'QA metadata unavailable or malformed; merge blocked.')
    (output / 'verdict.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    summary = f"Independent QA: {result['verdict']} for {result['head']}\n"
    summary += result.get('error', result.get('attestation_url', '')) + '\n'
    summary += result['limitation'] + '\n'
    if os.environ.get('GITHUB_STEP_SUMMARY'):
        with open(os.environ['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as stream:
            stream.write(summary)
    print(summary)
    return 0 if result['verdict'] == 'PASS' else 1


if __name__ == '__main__':
    sys.exit(main())
