"""Standalone method partitions and mechanical accounting, never qualification.

Trusted collectors must authenticate inputs and execution; see test-group-shards.md.
"""
from collections import Counter
from copy import deepcopy
from decimal import Decimal, InvalidOperation
from pathlib import Path
import json
import re
import xml.etree.ElementTree as ET

import replay
import shards

require = shards.require
sha = shards.sha
canonical = shards.canonical


def method_name(name):
    # No escaping guesses: refuse VSTest operators, spaces, generics and row syntax.
    require(isinstance(name, str) and re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+', name),
            'unsupported exact-filter method name')
    return name


def case_key(case):
    require(set(case) == {'method', 'display'} and isinstance(case['display'], str) and case['display'], 'invalid case')
    return method_name(case['method']), case['display']


def read_trx(path):
    """Passed result cases, not unused discovery definitions; IDs are run-local."""
    raw = Path(path).read_bytes()
    tree = ET.fromstring(raw)
    definitions = {}
    for definition in tree.findall('.//{*}UnitTest'):
        identifier = definition.get('id')
        method = definition.find('{*}TestMethod')
        require(identifier and identifier not in definitions and method is not None, 'duplicate/invalid TRX definition')
        definitions[identifier] = method_name(method.get('className', '') + '.' + method.get('name', ''))
    cases, weights = [], Counter()
    results = tree.findall('.//{*}UnitTestResult')
    counters = tree.find('.//{*}Counters')
    require(counters is not None and results and
            all(int(counters.get(k, '-1')) == len(results) for k in ('total', 'executed', 'passed')),
            'incomplete TRX counters')
    for result in results:
        require(result.get('outcome') == 'Passed' and result.get('testId') in definitions, 'failed/skipped/unbound case')
        name = definitions[result.get('testId')]
        case = {'method': name, 'display': result.get('testName')}
        case_key(case)
        cases.append(case)
        duration = result.get('duration', '')
        require(re.fullmatch(r'\d+:\d{2}:\d{2}(?:\.\d+)?', duration), 'invalid duration')
        hours, minutes, seconds = duration.split(':')
        try:
            seconds = Decimal(seconds)
            require(int(minutes) < 60 and seconds < 60, 'invalid duration range')
            weights[name] += int((int(hours) * 3600 + int(minutes) * 60 + seconds) * 1000000)
        except InvalidOperation as error:
            raise ValueError('invalid duration') from error
    require(len({case_key(c) for c in cases}) == len(cases), 'duplicate baseline case')
    return {'cases': sorted(cases, key=case_key), 'weights': {k: max(1, v) for k, v in sorted(weights.items())},
            'trx_sha256': replay.digest(raw)}


def inventory(root, names):
    result = {}
    for name in names:
        path, relative = shards.report_path(root, name)
        require(relative not in result, 'duplicate input path')
        result[relative] = replay.digest(path.read_bytes())
    require(result, 'empty input inventory')
    return dict(sorted(result.items()))


def plan(catalogue, config, binding, baseline, inputs, group_count, duration_weights=False):
    """LPT method partition, deterministic lexical tie breaks, exact bounded width."""
    # Reuse pinned binding and full-module configuration policy without source partitioning.
    shards.plan(catalogue, config, binding)
    require('test-case-filter' not in config['stryker-config'], 'prefiltered baseline')
    cases = baseline['cases']
    keys = [case_key(c) for c in cases]
    require(keys and keys == sorted(set(keys)), 'noncanonical/duplicate baseline cases')
    require(re.fullmatch(r'[0-9a-f]{64}', baseline['trx_sha256']), 'unbound baseline')
    methods = sorted({c['method'] for c in cases})
    require(type(group_count) is int and 1 <= group_count <= min(20, len(methods)), 'invalid bounded group count')
    require(type(duration_weights) is bool and set(baseline['weights']) == set(methods) and
            all(type(w) is int and w > 0 for w in baseline['weights'].values()), 'invalid duration weights')
    require(inputs and all(isinstance(k, str) and re.fullmatch(r'[0-9a-f]{64}', v) for k, v in inputs.items()), 'unbound inputs')
    weights = baseline['weights'] if duration_weights else Counter(c['method'] for c in cases)
    groups = [{'id': i, 'methods': [], 'weight': 0} for i in range(group_count)]
    for method in sorted(methods, key=lambda m: (-weights[m], m)):
        group = min(groups, key=lambda g: (g['weight'], g['id']))
        group['methods'].append(method)
        group['weight'] += weights[method]
    for group in groups:
        group['methods'].sort()
        group['cases'] = [c for c in cases if c['method'] in group['methods']]
        group['filter'] = '|'.join('FullyQualifiedName=' + m for m in group['methods'])
    value = {'schema_version': 1, 'status': 'TEST_GROUP_INDEX', 'catalogue': deepcopy(catalogue),
             'baseline_config': deepcopy(config), 'binding': deepcopy(binding), 'baseline': deepcopy(baseline),
             'inputs': deepcopy(inputs), 'group_count': group_count, 'duration_weights': duration_weights,
             'groups': groups, 'original_denominator': sum(r['original']['status'] != 'CompileError' for r in catalogue['rows'])}
    value['plan_sha256'] = sha(value)
    return value


def configuration(value, group_id):
    require(type(group_id) is int and 0 <= group_id < len(value['groups']), 'unknown group')
    config = deepcopy(value['baseline_config'])
    config['stryker-config']['test-case-filter'] = value['groups'][group_id]['filter']
    return config


def validate(value, records, root, expected_binding, original_report, full_baseline, full_restored,
             original_host_root=None):
    """All groups mandatory; original mask fixed; actual group-attributed kills only."""
    require(value['binding'] == expected_binding, 'stale binding')
    rebuilt = plan(value['catalogue'], value['baseline_config'], expected_binding, value['baseline'],
                   value['inputs'], value['group_count'], value['duration_weights'])
    require(value == rebuilt, 'tampered plan')
    require(inventory(root, value['inputs']) == value['inputs'], 'changed source/test/config inputs')
    require(read_trx(full_baseline) == value['baseline'] and
            read_trx(full_restored)['cases'] == value['baseline']['cases'], 'changed full baseline/restoration')
    original = shards.read_catalogue(original_report, root, original_host_root)
    require(original == value['catalogue'], 'changed original official catalogue')
    originals = {r['identity']: r for r in original['rows']}
    require(len(records) == len(value['groups']) and all(type(r['group_id']) is int for r in records) and
            sorted(r['group_id'] for r in records) == list(range(len(value['groups']))), 'missing/duplicate group')
    kills, timeouts = set(), set()
    for record in records:
        group = value['groups'][record['group_id']]
        require(record['plan_sha256'] == value['plan_sha256'] and record['config'] == configuration(value, group['id']),
                'wrong plan/config')
        require(type(record['exit_code']) is int and record['exit_code'] in (0, 2) and
                record['timeout'] is False and record['cancelled'] is False, 'incomplete command')
        require(read_trx(record['baseline'])['cases'] == group['cases'] and
                read_trx(record['restored'])['cases'] == group['cases'], 'incorrect selected cases')
        current = shards.read_catalogue(record['report'], root, record.get('workspace_root'))
        require(current['report_sha256'] == record['report_sha256'] and current['sources'] == original['sources'] and
                {r['identity'] for r in current['rows']} == set(originals), 'changed/partial group catalogue')
        # Some reporters list all discovered tests; others list only selected tests.
        # Require an explicit mode and exact case multiplicities, never infer selection from definitions.
        mode = record['test_definitions']
        require(mode in ('all', 'selected'), 'unknown test definition mode')
        expected_cases = value['baseline']['cases'] if mode == 'all' else group['cases']
        raw = json.loads(Path(record['report']).read_bytes())
        tests = shards.bound_tests(raw, root, {c['method'] for c in expected_cases}, record.get('workspace_root'))
        require(Counter(tests.values()) == Counter(c['method'] for c in expected_cases), 'wrong raw definition multiplicities')
        for row in current['rows']:
            status = row['original']['status']
            require((status == 'CompileError') == (originals[row['identity']]['original']['status'] == 'CompileError'),
                    'changed original CompileError mask')
            require(status in ('CompileError', 'Killed', 'Survived', 'NoCoverage', 'Timeout'), 'unresolved private row')
            if status == 'Timeout':
                timeouts.add(row['identity'])
            if status == 'Killed':
                attributed = row['original'].get('killedBy')
                require(attributed and all(t in tests and tests[t] in group['methods'] for t in attributed),
                        'kill outside selected group')
                kills.add(row['identity'])
    return {'schema_version': 1, 'status': 'ACCOUNTED', 'qualification': 'NOT_EVALUATED',
            'plan_sha256': value['plan_sha256'], 'original_denominator': value['original_denominator'],
            'attributed_killed': sorted(kills), 'uncredited_mutant_timeouts': sorted(timeouts), 'groups': len(records)}
