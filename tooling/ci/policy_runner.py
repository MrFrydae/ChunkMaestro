"""Validate direct xUnit discovery/execution when VSTest's ARM artifact path fails."""
from collections import Counter
import json
from pathlib import Path
import sys


def validate(discovered, report):
    if not isinstance(discovered, list) or not discovered:
        raise ValueError('Policy discovery is empty or malformed')
    expected = Counter((row['Class'], row['Method']) for row in discovered)
    if len({row['ID'] for row in discovered}) != len(discovered):
        raise ValueError('Duplicate discovery identity')
    results = report['results']
    tests = results['tests']
    actual = Counter((row['extra']['type'], row['extra']['method']) for row in tests)
    if actual != expected or len(tests) != len(discovered):
        raise ValueError('Executed policy method/case inventory differs from discovery')
    # Pinned xUnit CTRF retains JSON-escaped display names inside JSON strings.
    # Compare exact case names too: method counts alone allow a replaced theory row.
    expected_cases = Counter((json.dumps(row['DisplayName'], ensure_ascii=False)[1:-1],
                              row['Class'], row['Method']) for row in discovered)
    actual_cases = Counter((row['name'], row['extra']['type'], row['extra']['method'])
                           for row in tests)
    if actual_cases != expected_cases:
        raise ValueError('Executed policy cases differ from discovered display names')
    identities = {(row['name'], row['extra']['type'], row['extra']['method']) for row in tests}
    if len(identities) != len(tests):
        raise ValueError('Duplicate executed policy case')
    if any(row['status'] != 'passed' for row in tests):
        raise ValueError('Policy execution contains failure, skip or incomplete cases')
    summary = results['summary']
    if (summary['tests'] != len(tests) or summary['passed'] != len(tests)
            or any(summary[field] != 0 for field in ('failed', 'pending', 'skipped', 'other'))):
        raise ValueError('Policy summary differs from all-passing execution')
    return {'verdict': 'PASS', 'cases': len(tests), 'methods': len(expected),
            'runner': 'direct pinned xUnit v3; no VSTest artifact postprocessor',
            'inventory': 'exact pre-enumerated display names and method/case multiplicities',
            'coverage': 'reported separately by unchanged x64 VSTest job'}


if __name__ == '__main__':
    root = Path(sys.argv[1])
    result = validate(json.loads((root / 'discovery.json').read_text()),
                      json.loads((root / 'results.ctrf.json').read_text()))
    (root / 'direct-runner-verdict.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result))
