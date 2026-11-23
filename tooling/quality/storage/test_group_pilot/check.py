"""Read-only real-artifact accounting and negative probes; never execute tools."""
from copy import deepcopy
import json
from pathlib import Path
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import replay
import test_groups as groups


def main():
    output = Path(sys.argv[1])
    value = json.loads((output / 'plan.json').read_bytes())
    records = json.loads((output / 'records.json').read_bytes())
    root = output / 'fixture'
    original = next((output / 'official').rglob('mutation-report.json'))
    baseline = next((output / 'baseline').glob('*.trx'))
    restored = next((output / 'restored').glob('*.trx'))

    def validate(v, r):
        return groups.validate(v, r, root, value['binding'], original, baseline, restored)

    result = validate(value, records)
    denials = []
    changes = {
        'missing group': lambda v, r: r.pop(),
        'duplicate group': lambda v, r: r.append(r[0]),
        'command timeout': lambda v, r: r[0].update(timeout=True),
        'cancelled group': lambda v, r: r[0].update(cancelled=True),
        'failed group': lambda v, r: r[0].update(exit_code=1),
        'changed binding': lambda v, r: v['binding'].update(head='f' * 40),
        'changed filter': lambda v, r: r[0]['config']['stryker-config'].update({'test-case-filter': 'FullyQualifiedName=Other.Method'}),
        'partial selected baseline': lambda v, r: r[0].update(baseline=str(baseline)),
    }
    for name, change in changes.items():
        v, r = deepcopy((value, records))
        change(v, r)
        try:
            validate(v, r)
        except ValueError as error:
            denials.append({'probe': name, 'denial': str(error)})
        else:
            raise ValueError('unexpected acceptance: ' + name)
    with tempfile.TemporaryDirectory(prefix='cm-group-denials-') as temporary:
        for name in ('mask', 'attribution', 'identity', 'definitions'):
            r = deepcopy(records)
            raw = json.loads(Path(r[0]['report']).read_bytes())
            mutants = [m for f in raw['files'].values() for m in f['mutants']]
            if name == 'mask':
                next(m for m in mutants if m['status'] == 'CompileError')['status'] = 'NoCoverage'
            elif name == 'attribution':
                next(m for m in mutants if m['status'] == 'Killed')['killedBy'] = ['unbound']
            elif name == 'identity':
                mutants[0]['replacement'] += 'changed'
            else:
                next(iter(raw['testFiles'].values()))['tests'].clear()
            path = Path(temporary) / (name + '.json')
            path.write_bytes(groups.canonical(raw))
            r[0].update(report=str(path), report_sha256=replay.digest(path.read_bytes()))
            try:
                validate(value, r)
            except ValueError as error:
                denials.append({'probe': name, 'denial': str(error), 'modified_report_sha256': r[0]['report_sha256']})
            else:
                raise ValueError('unexpected acceptance: ' + name)
    replay.write(output / 'offline-accounting.json', {'result': result, 'denials': denials,
                 'checker_sha256': replay.digest(Path(__file__).read_bytes()),
                 'helper_sha256': replay.digest(Path(groups.__file__).read_bytes())})
    print(json.dumps({'status': result['status'], 'qualification': result['qualification'], 'denied': len(denials)}))


if __name__ == '__main__':
    main()
