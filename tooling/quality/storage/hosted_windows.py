"""Owner-authorized, bounded Windows collection. Never emits qualification PASS."""
import argparse
import json
import os
from pathlib import Path
import platform
import re
import subprocess
import sys
import time

import qualify
import replay
import replay_ci
import volume

AUTHORIZATION = 'PR6-one-Windows-run'
WORKFLOW = '.github/workflows/ci.yml'
TOTAL_SECONDS = 2880
STAGES = (('coverage-and-small-modules', 480), ('persistence-replay', 2220), ('full-volume', 180))


def context(head, authorization):
    env = os.environ
    event = json.loads(Path(env['GITHUB_EVENT_PATH']).read_bytes())
    trust = replay_ci.trusted_context(env, event, head)
    if (re.fullmatch('[0-9a-f]{40}', head) is None or platform.system() != 'Windows' or env.get('GITHUB_ACTIONS') != 'true' or
            env.get('RUNNER_ENVIRONMENT') != 'github-hosted' or
            trust['event'] != 'workflow_dispatch' or authorization != AUTHORIZATION or
            event.get('inputs', {}).get('authorization') != AUTHORIZATION or
            event.get('inputs', {}).get('head') != head or env.get('GITHUB_RUN_ATTEMPT') != '1' or
            not env.get('GITHUB_RUN_ID', '').isdigit() or
            env.get('GITHUB_WORKFLOW_REF') != replay_ci.REPOSITORY + '/' + WORKFLOW + '@' + env.get('GITHUB_REF', '') or
            env.get('GITHUB_WORKFLOW_SHA') != head):
        raise ValueError('Requires authorized first-attempt hosted Windows dispatch at workflow/source exact head')
    if subprocess.check_output(['git', 'status', '--porcelain'], cwd=qualify.ROOT, text=True).strip():
        raise ValueError('Requires clean exact head')
    if subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=qualify.ROOT, text=True).strip() != head:
        raise ValueError('Checkout differs from authorized head')
    return dict(trust, execution='github-hosted', authorization=authorization,
                run_id=env['GITHUB_RUN_ID'], run_attempt=1,
                workflow=WORKFLOW, workflow_sha=head,
                run_url='https://github.com/' + replay_ci.REPOSITORY + '/actions/runs/' + env['GITHUB_RUN_ID'])


def stage(name, output, trust):
    if name == 'coverage-and-small-modules':
        return qualify.run(output)
    if name == 'persistence-replay':
        code = replay_ci.collect(output)
        path = output / 'replay-collection.json'
        result = json.loads(path.read_bytes())
        result['hosted_context'] = trust
        replay.write(path, result)
        return code
    if name == 'full-volume':
        return volume.run(output / 'volume')
    raise ValueError('Unknown stage')


def collect(output, head, authorization):
    trust = context(head, authorization)
    output = output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    # All temporary tool builds/replay trees remain uploadable even if taskkill
    # prevents the child's finally/relocation logic from executing.
    scratch = output / 'scratch'
    scratch.mkdir()
    evidence = output / 'evidence'
    env = dict(os.environ, TEMP=str(scratch), TMP=str(scratch), TMPDIR=str(scratch), PYTHONUNBUFFERED='1')
    session = {'schema_version': 1, 'head': head, 'platform': 'Windows', 'trust': trust,
               'status': 'UNRESOLVED', 'qualification': 'NOT_EVALUATED',
               'total_deadline_seconds': TOTAL_SECONDS, 'stages': []}
    started = time.monotonic()
    path = output / 'hosted-session.json'
    replay.write(path, session)
    try:
        for name, deadline in STAGES:
            session['phase'] = name
            replay.write(path, session)
            remaining = TOTAL_SECONDS - (time.monotonic() - started)
            if remaining <= 0:
                raise ValueError('Total collection deadline exhausted')
            record = replay.command([sys.executable, '-u', str(Path(__file__).resolve()), str(evidence),
                                     '--head', head, '--authorization', authorization, '--stage', name],
                                    qualify.ROOT, output, name, env, min(deadline, remaining))
            session['stages'].append(dict(record, stage=name, deadline_seconds=deadline))
            replay.write(path, session)
            replay.require(record)
        session['status'] = 'COLLECTED'
    except (ValueError, OSError, KeyError, subprocess.SubprocessError) as error:
        session['error'] = str(error)
    finally:
        session['seconds'] = time.monotonic() - started
        replay.write(path, session)
    return 0 if session['status'] == 'COLLECTED' else 1


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('output', type=Path)
    parser.add_argument('--head', required=True)
    parser.add_argument('--authorization', required=True)
    parser.add_argument('--stage', choices=[name for name, _ in STAGES])
    args = parser.parse_args()
    try:
        if args.stage:
            trust = context(args.head, args.authorization)
            sys.exit(stage(args.stage, args.output, trust))
        sys.exit(collect(args.output, args.head, args.authorization))
    except (ValueError, OSError, KeyError, subprocess.SubprocessError) as error:
        print('[storage-hosted] UNRESOLVED: ' + str(error), flush=True)
        sys.exit(1)
