"""Offline fresh fixture proof; writes only new derived evidence. Baseline case plan is independent of wire events."""
import argparse
import json
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import initial_events as events


def binding(root, source, label, destination):
    records = json.loads((root / 'commands.json').read_bytes())
    command = next(record for record in records if '--output' in record['args'] and record['args'][record['args'].index('--output') + 1] == str(root / label))
    # A command is a distinct immutable bound input, not the surrounding list.
    command_path = destination / (label + '-command.json')
    command_path.write_text(json.dumps(command, indent=2) + '\n')
    baseline = ET.parse(next((root / 'baseline').glob('*.trx'))).getroot()
    ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    definitions = {test.attrib['id']: test.find('t:TestMethod', ns).attrib for test in baseline.findall('.//t:UnitTest', ns)}
    cases = []
    for result in baseline.findall('.//t:UnitTestResult', ns):
        method = definitions[result.attrib['testId']]
        cases.append({'fqn': method['className'].split(',')[0] + '.' + method['name'].split('(')[0], 'display': result.attrib['testName']})
    args = command['args']
    config = json.loads(Path(args[args.index('--config-file') + 1]).read_bytes())['stryker-config']
    test_filter = config.get('test-case-filter')
    if test_filter is not None:
        methods = []
        for term in test_filter.split('|'):
            assert term.startswith('FullyQualifiedName=') and term.count('=') == 1
            methods.append(term.split('=', 1)[1])
        assert len(methods) == len(set(methods)) and all(any(c['fqn'] == method for c in cases) for method in methods)
        cases = [case for case in cases if case['fqn'] in methods]
    wire = root / label / 'logs/Runner 0-log.txt'
    phase = next((root / label / 'logs').glob('log-*.txt'))
    return {'source_commit': events.COMMIT, 'archive_sha256': events.ARCHIVE_SHA256,
            'source_root': str(source), 'source_hashes': {name: events.digest((source / name).read_bytes()) for name in events.SOURCE_FILES},
            'sdk_version': '10.0.404', 'sdk_sha256': events.digest(Path(args[0]).read_bytes()),
            'tool_sha256': events.digest(Path(args[1]).read_bytes()), 'command_path': str(command_path),
            'command_sha256': events.digest(command_path.read_bytes()), 'argv': args, 'cwd': command['cwd'],
            'cancelled': False, 'config_sha256': events.digest(Path(args[args.index('--config-file')+1]).read_bytes()),
            'wire_path': str(wire), 'wire_sha256': events.digest(wire.read_bytes()),
            'phase_path': str(phase), 'phase_sha256': events.digest(phase.read_bytes()),
            'assembly': str((root / 'fixture/Tests/bin/Release/net10.0/Tests.dll').resolve()), 'cases': cases,
            'test_case_filter': test_filter}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root', type=Path)
    parser.add_argument('source', type=Path)
    parser.add_argument('--output-directory', type=Path, required=True)
    args = parser.parse_args()
    args.output_directory.mkdir(exist_ok=False, parents=True)
    for label in ('passing', 'skipped'):
        plan = binding(args.root, args.source, label, args.output_directory)
        (args.output_directory / (label + '-binding.json')).write_text(json.dumps(plan, indent=2) + '\n')
        result = events.validate_initial_events(plan)
        (args.output_directory / (label + '-initial-result.json')).write_text(json.dumps(result, indent=2) + '\n')
        print(label, json.dumps(result))
        assert result['status'] == ('PASS' if label == 'passing' else 'UNRESOLVED')
        if label == 'passing':
            denials = {}
            for field, value in [('cancelled', True), ('wire_sha256', '0'*64), ('command_sha256', '0'*64), ('source_hashes', {}), ('cases', plan['cases'][:-1])]:
                changed = dict(plan, **{field: value})
                denial = events.validate_initial_events(changed)
                assert denial['status'] == 'UNRESOLVED' and denial['credit'] is False
                denials[field] = denial
            (args.output_directory / 'binding-denials.json').write_text(json.dumps(denials, indent=2) + '\n')


if __name__ == '__main__':
    main()
