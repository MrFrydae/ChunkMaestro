"""Offline final-validator proof and real-artifact denial probes. No tool execution."""
from copy import deepcopy
import json
from pathlib import Path
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import replay
import shards


def main():
    artifact = Path(sys.argv[1]).resolve()
    plan = json.loads((artifact / 'plan.json').read_bytes())
    records = json.loads((artifact / 'records.json').read_bytes())
    root = artifact / 'fixture'
    original = next((artifact / 'full').rglob('mutation-report.json'))
    expected = {'.'.join(c[0]) for c in replay.test_cases(artifact / 'baseline')}
    output = Path(tempfile.mkdtemp(prefix='cm-shard-accounting-'))

    def validate(p, r):
        return shards.validate(p, r, root, plan['binding'], original, expected)

    result = validate(plan, records)
    assert result['attributed_killed'] == sorted(r['identity'] for r in plan['catalogue']['rows']
                                               if r['original']['status'] == 'Killed')
    denials = []

    def deny(name, p, r):
        try:
            validate(p, r)
        except (ValueError, KeyError) as error:
            denials.append({'probe': name, 'denial': str(error)})
        else:
            raise AssertionError('accepted: ' + name)

    deny('missing', plan, records[:-1])
    deny('duplicate', plan, records[:-1] + [records[0]])
    tampered = deepcopy(plan)
    tampered['catalogue']['rows'].pop()
    deny('refiltered original', tampered, records)
    tampered = deepcopy(plan)
    tampered['regions'][0]['patterns'].append('**/*.cs')
    deny('tampered patterns', tampered, records)
    for field, value in [('cancelled', True), ('timeout', True), ('exit_code', 1)]:
        changed = deepcopy(records)
        changed[0][field] = value
        deny(field, plan, changed)
    for probe in ('mask', 'partial', 'identity', 'owner Timeout', 'owner Ignored', 'unattributed', 'outside execution'):
        changed = deepcopy(records)
        record = changed[0]
        raw = json.loads(Path(record['report']).read_bytes())
        mutants = [m for f in raw['files'].values() for m in f['mutants']]
        if probe == 'mask':
            next(m for m in mutants if m['status'] == 'CompileError')['status'] = 'Ignored'
        elif probe == 'partial':
            next(f for f in raw['files'].values() if f['mutants'])['mutants'].pop()
        elif probe == 'identity':
            mutants[0]['replacement'] = 'changed'
        elif probe.startswith('owner '):
            next(m for m in mutants if m['status'] == 'Killed')['status'] = probe.split()[1]
        elif probe == 'unattributed':
            next(m for m in mutants if m['status'] == 'Killed')['killedBy'] = ['unbound']
        else:
            next(m for m in mutants if m['status'] == 'Ignored').update(status='Killed', killedBy=['unbound'])
        path = output / (probe.replace(' ', '-') + '.json')
        path.write_bytes(shards.canonical(raw))
        record.update(report=str(path), report_sha256=replay.digest(path.read_bytes()))
        deny(probe, plan, changed)
    result.update(real_artifact_denials=denials, input_artifact=str(artifact),
                  module_sha256=replay.digest(Path(shards.__file__).read_bytes()))
    (output / 'result.json').write_bytes(shards.canonical(result))
    (output / 'artifact-sha256.json').write_bytes(shards.canonical(replay.hashes(output)))
    print(output)
    print('ACCOUNTED; %d real-artifact denial probes rejected' % len(denials))


if __name__ == '__main__':
    main()
