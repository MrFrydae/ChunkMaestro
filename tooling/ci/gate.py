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
    if not jobs or any(value != 'success' for value in jobs.values()):
        raise ValueError('One or more prerequisite jobs failed, were cancelled or skipped')
    inputs = digest_inputs()
    expected = json.loads(PLAN.read_text())['expected_cases']
    all_lines, all_branches = {}, {}
    passed = 0
    for system in ('Windows', 'Darwin'):
        for group, classes in plan(system).items():
            folder = root / ('storage-' + system + '-' + group)
            report = json.loads((folder / 'result.json').read_text())
            if (report.get('head') != head or report.get('system') != system or
                    report.get('group') != group or report.get('inputs') != inputs or report.get('status') != 'PASS'):
                raise ValueError('Missing, stale, wrong-platform or failing partition: ' + str(folder))
            for name, digest in report['files'].items():
                file = (folder / name).resolve()
                if not file.is_relative_to(folder.resolve()) or hashlib.sha256(file.read_bytes()).hexdigest() != digest:
                    raise ValueError('Raw partition file missing or changed')
            count = read_trx(folder, classes, {name: expected[system][name] for name in classes})
            if count != report.get('passed'):
                raise ValueError('Raw case counts differ from report')
            passed += count
            for target, incoming in zip((all_lines, all_branches), coverage_points(folder)):
                for key, hit in incoming.items():
                    target[key] = target.get(key, False) or hit
        validate_evidence(root / ('volume-' + system), head, system)
    native = root / 'native-Darwin'
    execution = json.loads((native / 'execution.json').read_text())
    if execution.get('head') != head or execution.get('inputs') != inputs or execution.get('system') != 'Darwin':
        raise ValueError('Native execution source/head mismatch')
    for name, category, size in (('filesystem.json', 'real-filesystem', 42),
                                 ('injected.json', 'injected-OS-failure-or-metadata', 10)):
        data = json.loads((native / name).read_text())
        cases = data.get('cases', [])
        if (data.get('category') != category or len(cases) != size or
                len({case['name'] for case in cases}) != size or
                any(case.get('passed') is not True or case['actual'] != case['expected'] for case in cases)):
            raise ValueError('Native fault-path tests missing or failed')
    native_coverage = json.loads((native / 'coverage.json').read_text())['data'][0]
    if (len(native_coverage['files']) != 1 or
            not native_coverage['files'][0]['filename'].replace('\\', '/').endswith('/src/ChunkMaestro.Persistence/Native/storage.c')):
        raise ValueError('Native coverage source mismatch')
    for metric, threshold in (('lines', 90), ('branches', 85)):
        total = native_coverage['totals'][metric]
        if total['count'] <= 0 or 100 * total['covered'] / total['count'] < threshold:
            raise ValueError('Native ' + metric + ' below required coverage')
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
    except (ValueError, KeyError, OSError, TypeError, ET.ParseError) as error:
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
