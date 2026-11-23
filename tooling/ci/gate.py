"""Aggregate this workflow's direct raw tests and coverage, never a lead receipt."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

from storage import ROOT, PLAN, digest_inputs, plan, read_trx
from volume import validate_evidence
from evidence import read_json, verify_inventory


def native_metrics(report):
    data = report.get('data') if isinstance(report, dict) else None
    if not isinstance(data, list) or len(data) != 1 or not isinstance(data[0], dict):
        raise ValueError('Malformed native coverage data')
    data = data[0]
    files = data.get('files')
    if (not isinstance(files, list) or len(files) != 1 or not isinstance(files[0], dict) or
            not isinstance(files[0].get('filename'), str) or
            not files[0]['filename'].replace('\\', '/').endswith('/src/ChunkMaestro.Persistence/Native/storage.c')):
        raise ValueError('Native coverage source mismatch')
    totals = data.get('totals')
    if not isinstance(totals, dict):
        raise ValueError('Missing native coverage totals')
    for metric, threshold in (('lines', 90), ('branches', 85)):
        total = totals.get(metric)
        if (not isinstance(total, dict) or type(total.get('count')) is not int or
                type(total.get('covered')) is not int or total['count'] <= 0 or
                not 0 <= total['covered'] <= total['count']):
            raise ValueError('Invalid native ' + metric + ' coverage counts')
        if 100 * total['covered'] / total['count'] < threshold:
            raise ValueError('Native ' + metric + ' below required coverage')
    return totals


def coverage_points(folder):
    reports = list(folder.rglob('coverage.opencover.xml'))
    if not reports:
        raise ValueError('Missing raw coverage')
    lines, branches = {}, {}
    for report in reports:
        if report.stat().st_size > 32 * 1024 * 1024:
            raise ValueError('Oversized coverage report')
        for module in ET.parse(report).findall('.//Module'):
            files = {}
            for item in module.findall('./Files/File'):
                path = item.attrib['fullPath'].replace('\\', '/')
                position = path.find('/src/')
                if position < 0:
                    raise ValueError('Coverage is not bound to an owned source')
                files[item.attrib['uid']] = path[position + 1:]
            for method in module.findall('.//Method'):
                for point in method.findall('./SequencePoints/SequencePoint'):
                    key = (files[point.attrib['fileid']], point.attrib['sl'])
                    lines[key] = lines.get(key, False) or int(point.attrib['vc']) > 0
                for point in method.findall('./BranchPoints/BranchPoint'):
                    key = (files[point.attrib['fileid']], method.findtext('Name'), point.attrib['offset'],
                           point.attrib['path'], point.attrib['offsetend'])
                    branches[key] = branches.get(key, False) or int(point.attrib['vc']) > 0
    return lines, branches


def evaluate(root, head, jobs):
    if not jobs:
        raise ValueError('Missing prerequisite job results')
    failed = [f'{name}={value}' for name, value in sorted(jobs.items()) if value != 'success']
    if failed:
        raise ValueError('Failed prerequisite jobs: ' + ', '.join(failed) +
                         '; inspect the matching storage-*, volume-* or native-* artifacts')
    inputs = digest_inputs()
    expected = json.loads(PLAN.read_text())['expected_cases']
    all_lines, all_branches = {}, {}
    passed = 0
    for system in ('Windows', 'Darwin'):
        for group, classes in plan(system).items():
            folder = root / ('storage-' + system + '-' + group)
            report = read_json(folder / 'result.json')
            if (not isinstance(report, dict) or report.get('head') != head or report.get('system') != system or
                    report.get('group') != group or report.get('inputs') != inputs or report.get('status') != 'PASS'):
                raise ValueError('Missing, stale, wrong-platform or failing partition: ' + str(folder))
            verify_inventory(folder, report['files'], exclude=('result.json',))
            discovered = read_json(folder / 'case-inventory.json')
            if any(discovered.get(key) != value for key, value in (('head', head), ('system', system), ('group', group))):
                raise ValueError('Discovery source/platform/group mismatch')
            count = read_trx(folder, classes, {name: expected[system][name] for name in classes}, discovered['cases'])
            if count != report.get('passed'):
                raise ValueError('Raw case counts differ from report')
            passed += count
            for target, incoming in zip((all_lines, all_branches), coverage_points(folder)):
                for key, hit in incoming.items():
                    target[key] = target.get(key, False) or hit
        validate_evidence(root / ('volume-' + system), head, system)
    native = root / 'native-Darwin'
    execution = read_json(native / 'execution.json')
    if execution.get('head') != head or execution.get('inputs') != inputs or execution.get('system') != 'Darwin':
        raise ValueError('Native execution source/head mismatch')
    verify_inventory(native, execution['files'], exclude=('execution.json',))
    native_plan = read_json(Path(__file__).with_name('native-plan.json'))
    for name, category, size in (('filesystem.json', 'real-filesystem', 42),
                                 ('injected.json', 'injected-OS-failure-or-metadata', 10)):
        data = read_json(native / name)
        cases = data.get('cases', [])
        if (not isinstance(cases, list) or data.get('category') != category or len(cases) != size or
                len({case['name'] for case in cases}) != size or
                {case['name'] for case in cases} != set(native_plan[name]) or
                any(case.get('passed') is not True or case['actual'] != case['expected'] for case in cases)):
            raise ValueError('Native fault-path tests missing or failed')
    native_metrics(read_json(native / 'coverage.json', limit=32 * 1024 * 1024))
    metrics, failures = {}, []
    for module in ('Domain', 'Application', 'Persistence'):
        prefix = 'src/ChunkMaestro.' + module + '/'
        metrics[module] = {}
        for name, points, threshold in (('line', all_lines, 90), ('branch', all_branches, 85)):
            values = [hit for key, hit in points.items() if key[0].startswith(prefix)]
            if not values and not (module == 'Application' and name == 'branch'):
                failures.append(module + ' missing ' + name + ' coverage')
            percent = 100 * sum(values) / len(values) if values else None
            metrics[module][name] = {'covered': sum(values), 'total': len(values), 'percent': percent}
            if percent is not None and percent < threshold:
                failures.append(f'{module} {name} coverage {percent:.2f}% below {threshold}%')
    return {'head': head, 'passed': passed, 'metrics': metrics, 'failures': failures,
            'status': 'FAIL' if failures else 'PASS',
            'policy': 'direct fault/recovery tests; owner replaced storage mutation gate on 2026-11-22'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('artifacts', type=Path)
    parser.add_argument('--head', required=True)
    parser.add_argument('--jobs', required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    try:
        needs = json.loads(args.jobs)
        report = evaluate(args.artifacts, args.head, {name: value['result'] for name, value in needs.items()})
    except (ValueError, KeyError, OSError, TypeError, AttributeError, IndexError, ET.ParseError) as error:
        report = {'head': args.head, 'status': 'FAIL', 'failures': [str(error)]}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps(report, indent=2), flush=True)
    if os.environ.get('GITHUB_STEP_SUMMARY'):
        with open(os.environ['GITHUB_STEP_SUMMARY'], 'a') as stream:
            stream.write('## Storage safety: ' + report['status'] + '\n\n')
            stream.write('\n'.join('- ' + failure for failure in report['failures']) + '\n')
            stream.write('\nSee the retained TRX, coverage and fault-test artifacts. No mutation campaign or manual receipt.\n')
    return report['status'] != 'PASS'


if __name__ == '__main__':
    sys.exit(main())
