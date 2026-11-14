"""Validate supplied independent QA evidence structure; does not authenticate identity.

Usage: python3 tooling/build/qa_evidence.py FILE HEAD_SHA [--high-risk]
The lead must verify provenance and identities outside this offline contract check.
"""
import argparse
import json
import re


def validate(report, head, high_risk=False):
    if not re.fullmatch(r'[0-9a-f]{40}', head) or report.get('head') != head:
        raise ValueError('Evidence must match the exact 40-character reviewed head SHA.')
    implementers = report.get('implementers')
    if not isinstance(implementers, list) or not implementers or not all(isinstance(x, str) and x.strip() for x in implementers):
        raise ValueError('Named implementers required.')
    required = {'functional', 'security-reliability'}
    if high_risk:
        required.add('ux-accessibility-performance')
    seen_roles, seen_reviewers = set(), {x.strip().casefold() for x in implementers}
    for review in report.get('reviews', []):
        role, reviewer = review.get('role'), review.get('reviewer', '').strip().casefold()
        if not reviewer or reviewer in seen_reviewers or role in seen_roles:
            raise ValueError('Review roles and reviewers must be independent and unique.')
        seen_reviewers.add(reviewer)
        seen_roles.add(role)
        if review.get('verdict') != 'PASS':
            raise ValueError('Unresolved non-PASS verdict.')
        for field in ('commands', 'environment', 'evidence', 'limitations', 'findings'):
            if not review.get(field):
                raise ValueError(f'Review must include {field}; explicitly state none if applicable.')
    if not required.issubset(seen_roles):
        raise ValueError('Missing required QA roles.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('file')
    parser.add_argument('head')
    parser.add_argument('--high-risk', action='store_true')
    args = parser.parse_args()
    with open(args.file, encoding='utf-8') as stream:
        validate(json.load(stream), args.head, args.high_risk)
    print('Evidence contract valid; provenance and GitHub approvals remain lead-owned.')
