"""Direct storage test partitions and raw-result validation. No mutation campaign."""
import argparse
import hashlib
import json
from pathlib import Path
import platform
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
PLAN = Path(__file__).with_name('storage-plan.json')


def plan(system):
    data = json.loads(PLAN.read_text())
    groups = data['groups']
    assigned = [name for names in groups.values() for name in names]
    sources = {p.stem for p in (ROOT / 'tests/ChunkMaestro.PersistenceTests').glob('*Tests.cs')}
    if len(assigned) != len(set(assigned)) or set(assigned) | set(data['excluded']) != sources:
        raise ValueError('Storage test partition is incomplete or overlapping; update storage-plan.json')
    if set(assigned) & set(data['excluded']):
        raise ValueError('A test cannot be both included and excluded')
    if system not in ('Darwin', 'Windows'):
        raise ValueError('Storage safety requires a supported controller OS')
    return {group: [name for name in names if data['platform_only'].get(name, system) == system]
            for group, names in groups.items()}


def read_trx(folder, classes, expected=None):
    files = list(folder.rglob('*.trx'))
    if len(files) != 1 or files[0].stat().st_size > 16 * 1024 * 1024:
        raise ValueError('Expected one bounded raw TRX')
    root = ET.parse(files[0]).getroot()
    definitions = {}
    for test in root.findall('.//{*}UnitTest'):
        method = test.find('./{*}TestMethod')
        identity = test.get('id')
        if not identity or identity in definitions or method is None or not method.get('className'):
            raise ValueError('Missing or ambiguous executed test definition')
        definitions[identity] = method.attrib['className'].split(',')[0].split('.')[-1]
    rows = root.findall('.//{*}UnitTestResult')
    counters = root.find('.//{*}Counters')
    if (not rows or counters is None or any(row.get('outcome') != 'Passed' for row in rows) or
            any(int(counters.get(key, '-1')) != len(rows) for key in ('total', 'executed', 'passed')) or
            any(int(value) != 0 for key, value in counters.attrib.items() if key not in ('total', 'executed', 'passed'))):
        raise ValueError('Missing, failed, skipped or incomplete test execution')
    if {row.get('testId') for row in rows} != set(definitions):
        raise ValueError('Execution and definition inventories differ')
    seen = {definitions[row.attrib['testId']] for row in rows}
    if seen != set(classes):
        raise ValueError('Executed classes differ from this partition')
    if expected is not None:
        from collections import Counter
        counts = Counter(definitions[row.attrib['testId']] for row in rows)
        if counts != expected:
            raise ValueError('Executed case counts differ; missing or unexpected test cases')
    identities = [(row.attrib['testId'], row.attrib['testName']) for row in rows]
    if len(identities) != len(set(identities)):
        raise ValueError('Duplicate test result')
    return len(rows)


def digest_inputs():
    paths = subprocess.check_output(['git', 'ls-files'], cwd=ROOT, text=True).splitlines()
    return hashlib.sha256(''.join(name + ':' + hashlib.sha256((ROOT / name).read_bytes()).hexdigest() + '\n'
                                  for name in paths if (ROOT / name).is_file()).encode()).hexdigest()


def record(folder, group, system, head):
    # Outcome is derived from retained raw execution, never a caller-supplied PASS.
    classes = plan(system)[group]
    report = {'schema_version': 1, 'head': head, 'system': system, 'group': group,
              'status': 'FAIL', 'classes': classes, 'inputs': digest_inputs()}
    try:
        if system != platform.system():
            raise ValueError('Declared platform differs from the executing OS')
        if head != subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip():
            raise ValueError('Execution head differs from checkout')
        if subprocess.check_output(['git', 'status', '--porcelain', '--untracked-files=no'], cwd=ROOT, text=True).strip():
            raise ValueError('Tracked inputs changed after checkout')
        counts = json.loads(PLAN.read_text())['expected_cases'][system]
        report['passed'] = read_trx(folder, classes, {name: counts[name] for name in classes})
        coverage = list(folder.rglob('coverage.opencover.xml'))
        if not coverage:
            raise ValueError('Missing raw coverage')
        report['status'] = 'PASS'
    except (ValueError, KeyError, ET.ParseError, OSError) as error:
        report['error'] = str(error)
    folder.mkdir(parents=True, exist_ok=True)
    report['files'] = {p.relative_to(folder).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
                       for p in folder.rglob('*') if p.is_file() and p.name != 'result.json'}
    (folder / 'result.json').write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps(report, indent=2), flush=True)
    return report['status'] != 'PASS'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    for name in ('filter', 'record'):
        command = sub.add_parser(name)
        command.add_argument('--group', required=True)
        command.add_argument('--system', default=platform.system())
        if name == 'record':
            command.add_argument('--results', required=True, type=Path)
            command.add_argument('--head', required=True)
    args = parser.parse_args()
    if args.command == 'filter':
        classes = plan(args.system)[args.group]
        print('|'.join('FullyQualifiedName~ChunkMaestro.PersistenceTests.' + name + '.' for name in classes))
        return 0
    return record(args.results, args.group, args.system, args.head)


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (ValueError, KeyError, OSError) as error:
        print('Storage safety FAIL: ' + str(error), file=sys.stderr)
        sys.exit(1)
