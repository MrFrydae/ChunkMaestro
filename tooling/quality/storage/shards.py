"""Pinned Stryker source-region planning and fail-closed mechanical accounting.

No execution, trust attestation, qualification score or global PASS is produced.
See docs/qa/mutation-shards.md for the collector boundary and JSON contract.
"""
from copy import deepcopy
import json
from pathlib import Path
import re

import replay
import replay_gate

EXCLUSIONS = ['!**/Migrations/*.Designer.cs', '!**/Migrations/*ModelSnapshot.cs']
BASE_PATTERNS = ['**/*.cs', *EXCLUSIONS]
FILTER_REASON = 'Removed by mutate filter'
TERMINAL = {'Killed', 'Survived', 'NoCoverage', 'Timeout', 'Ignored', 'CompileError'}


def require(value, message):
    if not value:
        raise ValueError('Source shards: ' + message)


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':'), ensure_ascii=True).encode()


def sha(value):
    return replay.digest(canonical(value))


def read_catalogue(report, root):
    """Use existing source/UTF16 parser; additionally bind empty source/test files."""
    root = Path(root).resolve()
    report = Path(report)
    raw = json.loads(report.read_bytes())
    sources = {}
    for name, file in raw['files'].items():
        path = Path(name.replace('\\', '/'))
        if path.is_absolute():
            require(path.resolve().is_relative_to(root), 'unbound absolute source')
            name = path.resolve().relative_to(root).as_posix()
        path = replay_gate.child(root, name)
        require(re.fullmatch(r'[A-Za-z0-9_./-]+\.cs', name), 'unsafe pattern filename')
        require(name not in sources and path.read_bytes() == file['source'].encode(), 'ambiguous/changed source')
        require(not re.search(r'^\s*#line\b', file['source'], re.M), 'mapped source')
        sources[name] = replay.digest(path.read_bytes())
    require(not any(a != b and a.endswith('/' + b) for a in sources for b in sources),
            'ambiguous source pattern suffix')
    rows = replay.catalogue(report, root)
    require(rows and len({r['identity'] for r in rows}) == len(rows), 'empty/ambiguous catalogue')
    for row in rows:
        require(row['original']['status'] in TERMINAL, 'nonterminal original')
        require(row['span_utf16'][0] < row['span_utf16'][1], 'empty span is ambiguous')
    return {'rows': rows, 'sources': sources, 'report_sha256': replay.digest(report.read_bytes())}


def in_scope(row):
    name = row['file']
    return not ('/Migrations/' in '/' + name and
                (name.endswith('.Designer.cs') or name.endswith('ModelSnapshot.cs')))


def contains(a, b):
    return a[0] <= b[0] and b[1] <= a[1]


def pattern(node, exclude=False):
    return ('!' if exclude else '') + '**/' + node['file'] + '{%d..%d}' % tuple(node['span'])


def forest(rows):
    groups = {}
    for row in rows:
        if in_scope(row):
            key = (row['file'], *row['span_utf16'])
            groups.setdefault(key, []).append(row['identity'])
    nodes = [{'file': k[0], 'span': list(k[1:]), 'identities': sorted(v), 'children': []}
             for k, v in sorted(groups.items(), key=lambda kv: (kv[0][0], kv[0][1], -kv[0][2]))]
    roots, stack = [], []
    for node in nodes:
        while stack and (stack[-1]['file'] != node['file'] or stack[-1]['span'][1] <= node['span'][0]):
            stack.pop()
        if stack:
            require(contains(stack[-1]['span'], node['span']), 'crossing/nonlaminar spans')
            stack[-1]['children'].append(node)
        else:
            roots.append(node)
        stack.append(node)
    return roots


def plan(catalogue, config, binding, target=100, weights=None):
    """Deterministic bottom-up connected regions; equal spans are indivisible.

    target is a positive work ceiling where possible, not a timing prediction.
    Large equal-span groups may exceed it. No disjoint regions are coalesced.
    """
    require(type(target) is int and target > 0, 'invalid target')
    require(config['stryker-config']['mutate'] == BASE_PATTERNS, 'baseline scope changed')
    required = {'head', 'sdk_version', 'sdk_sha256', 'tool_sha256', 'upstream_commit', 'platform'}
    require(set(binding) == required and all(binding.values()), 'incomplete binding')
    require(binding['sdk_version'] == '10.0.404' and binding['upstream_commit'] == replay.COMMIT,
            'unpinned SDK/tool')
    require(all(re.fullmatch(r'[0-9a-f]{64}', binding[k]) for k in ('sdk_sha256', 'tool_sha256'))
            and re.fullmatch(r'[0-9a-f]{40}', binding['head']), 'invalid binding hashes')
    rows = catalogue['rows']
    identities = {r['identity'] for r in rows if in_scope(r)}
    weights = weights if weights is not None else {i: 1 for i in sorted(identities)}
    require(set(weights) == identities and all(type(w) is int and w > 0 for w in weights.values()), 'unbound weights')
    regions = []

    def visit(node):
        region = {'root': {k: node[k] for k in ('file', 'span')}, 'exclusions': [],
                  'owners': list(node['identities']), 'weight': sum(weights[i] for i in node['identities'])}
        for child in node['children']:
            part = visit(child)
            if region['weight'] + part['weight'] <= target:
                region['owners'] += part['owners']
                region['weight'] += part['weight']
                region['exclusions'] += part['exclusions']
            else:
                regions.append(part)
                region['exclusions'].append(part['root'])
        return region

    for root in forest(rows):
        regions.append(visit(root))
    regions.sort(key=lambda r: (r['root']['file'], r['root']['span']))
    for index, region in enumerate(regions):
        region['id'] = index
        region['owners'].sort()
        region['exclusions'].sort(key=lambda n: (n['file'], n['span']))
        region['patterns'] = [pattern(region['root']), *EXCLUSIONS,
                              *(pattern(n, True) for n in region['exclusions'])]
        require(set(region['owners']) == selected(rows, region), 'selection differs from ownership')
    owners = [i for region in regions for i in region['owners']]
    require(len(owners) == len(set(owners)) and set(owners) == identities, 'incomplete/duplicate ownership')
    result = {'schema_version': 1, 'status': 'INDEXED', 'binding': deepcopy(binding),
              'catalogue': deepcopy(catalogue), 'baseline_config': deepcopy(config),
              'target': target, 'weights': deepcopy(weights), 'regions': regions,
              'invocations': len(regions), 'original_denominator': sum(r['original']['status'] != 'CompileError' for r in rows)}
    result['plan_sha256'] = sha(result)
    return result


def selected(rows, region):
    return {r['identity'] for r in rows if in_scope(r) and r['file'] == region['root']['file']
            and contains(region['root']['span'], r['span_utf16'])
            and not any(n['file'] == r['file'] and contains(n['span'], r['span_utf16'])
                        for n in region['exclusions'])}


def configuration(plan_value, region_id):
    config = deepcopy(plan_value['baseline_config'])
    config['stryker-config']['mutate'] = plan_value['regions'][region_id]['patterns'][:]
    return config


def validate(plan_value, records, root, expected_binding, original_report, expected_tests):
    """Validate ALL raw shards. Returns accounting only; Timeout owners deny.

    Each record binds region_id, plan_sha256, config, report (local path),
    report_sha256, exit_code, timeout, cancelled. Collector authenticates execution.
    """
    require(expected_tests and all(isinstance(t, str) and t for t in expected_tests), 'unbound baseline tests')
    require(plan_value['binding'] == expected_binding, 'stale/wrong binding')
    rebuilt = plan(plan_value['catalogue'], plan_value['baseline_config'], expected_binding,
                   plan_value['target'], plan_value['weights'])
    require(plan_value == rebuilt, 'tampered/noncanonical plan')
    require(read_catalogue(original_report, root) == rebuilt['catalogue'],
            'original raw catalogue changed/refiltered')
    require(all(type(r['region_id']) is int for r in records) and
            len(records) == len(rebuilt['regions']) and
            sorted(r['region_id'] for r in records) == list(range(len(rebuilt['regions']))),
            'missing/duplicate shard')
    originals = {r['identity']: r for r in rebuilt['catalogue']['rows']}
    kills = set()
    for record in records:
        region = rebuilt['regions'][record['region_id']]
        require(record['plan_sha256'] == rebuilt['plan_sha256'] and
                record['config'] == configuration(rebuilt, region['id']), 'wrong plan/config')
        require(type(record['exit_code']) is int and record['exit_code'] in (0, 2) and record['timeout'] is False and record['cancelled'] is False,
                'failed/cancelled/timedout shard')
        current = read_catalogue(record['report'], root)
        require(current['report_sha256'] == record['report_sha256'], 'changed raw report')
        require(current['sources'] == rebuilt['catalogue']['sources'] and
                {r['identity'] for r in current['rows']} == set(originals), 'partial/changed catalogue')
        raw = json.loads(Path(record['report']).read_bytes())
        tests = {}
        for name, file in raw.get('testFiles', {}).items():
            path = Path(name.replace('\\', '/'))
            if path.is_absolute():
                require(path.resolve().is_relative_to(Path(root).resolve()), 'unbound test path')
                name = path.resolve().relative_to(Path(root).resolve()).as_posix()
            require(replay_gate.child(Path(root).resolve(), name).read_bytes() == file['source'].encode(), 'changed test source')
            for test in file['tests']:
                require(test['id'] not in tests, 'duplicate test id')
                tests[test['id']] = test['name']
        require(set(tests.values()) == set(expected_tests), 'missing/unbound baseline test identities')
        require(tests and set(tests.values()) == set(expected_tests), 'incomplete/unbound baseline test methods')
        for row in current['rows']:
            old = originals[row['identity']]
            require(all(row[k] == old[k] for k in ('file', 'source_sha256', 'span_utf16', 'span_sha256')),
                    'changed UTF16 identity')
            mutant = row['original']
            status = mutant['status']
            require((status == 'CompileError') == (old['original']['status'] == 'CompileError'), 'changed CompileError mask')
            if status == 'CompileError':
                continue
            if row['identity'] in region['owners']:
                require(status in ('Killed', 'Survived', 'NoCoverage'), 'unresolved selected owner')
                if status == 'Killed':
                    require(mutant.get('killedBy') and all(k in tests for k in mutant['killedBy']), 'unattributed kill')
                    kills.add(row['identity'])
            else:
                require(status == 'Ignored' and mutant.get('statusReason') == FILTER_REASON,
                        'unexpected out-of-owner execution/filter')
    return {'schema_version': 1, 'status': 'ACCOUNTED', 'plan_sha256': rebuilt['plan_sha256'],
            'original_denominator': rebuilt['original_denominator'], 'attributed_killed': sorted(kills),
            'invocations': len(records), 'qualification': 'NOT_EVALUATED'}
