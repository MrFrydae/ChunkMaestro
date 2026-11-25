#!/usr/bin/python3
"""One fixed campaign. Provision only by reviewed disposable-VM cloud-init integration."""
import sys
sys.dont_write_bytecode = True
sys.path.insert(0, '/opt/cm14-fixture')
import hashlib
import base64
import json
import os
from pathlib import Path
import time
import signal

from common import (Failure, Notify, ROOT, STATE, UNITS, ctl, dump, guard, properties,
                    read, require, run_raw, trusted, write, OwnedState, category, CONTAINMENT)

CASES = ('two-entry-armed', 'full-store-rejected-after-barrier', 'notify-main-rejection',
         'alive-holder-crash-handback', 'worker-agent-exit-hup', 'hup-fdpoll0-retention',
         'inactive-anchor-gc-retention', 'same-unit-dead-handback', 'no-second-worker-start',
         'partial-removal-crash', 'partial-handback-cleanup', 'repeated-empty-cleanup')
GAPS = ('worker-early-exit-no-work-proof', 'exited-readable-without-hup', 'pid-reuse',
        'worker-memory-max-oom', 'holder-oom', 'ancestor-oomd-policy-qualification',
        'agent-descendant-and-fd-exfil-closure', 'manager-reexec', 'anchor-removal-sensitivity',
        'pre-clr-tsync-and-no-old-rings', 'real-product-policy-protocol')


def progress(identity, phase):
    require(phase in ('guest-entered', 'sender-control-entered',
                      'sender-control-returned', 'campaign-finalizing'), 'progress-phase')
    record = {'schema': 1, 'nonce': identity['nonce'],
              'source_sha256': identity['source_sha256'], 'phase': phase}
    print('CM14-BOOT:' + base64.b64encode(json.dumps(record).encode()).decode(), flush=True)


def await_result(phase, end, sequence=None):
    while time.monotonic() < end:
        if (STATE / 'result').exists():
            result = read('result')
            require(result['phase'] != 'FAIL', result.get('category', 'holder-failure'))
            if result['phase'] == phase and (sequence is None or result.get('sequence') == sequence):
                return result
        time.sleep(0.05)
    raise Failure('observation-timeout')


def campaign(report, end):
    sequence = 0

    def passed(case, evidence):
        require(case in CASES and case not in report['cases'], 'case-membership')
        report['cases'][case] = {'status': 'PASS', 'raw': evidence}

    def control(action, phase):
        nonlocal sequence
        sequence += 1
        write('control', {'sequence': sequence, 'action': action})
        return await_result(phase, min(end, time.monotonic() + 7), sequence)

    def recover(phase='RECOVERED'):
        ctl('kill', UNITS[0])
        ctl('stop', UNITS[0])
        ctl('reset-failed', UNITS[0])
        (STATE / 'result').unlink(missing_ok=True)
        (STATE / 'control').unlink(missing_ok=True)
        ctl('start', UNITS[0])
        return await_result(phase, min(end, time.monotonic() + 8))

    ctl('start', UNITS[3])  # Ordering reference only: never pulls holder or worker in.
    require(properties(UNITS[1])['MainPID'] == '0', 'anchor-started-worker')
    ctl('start', UNITS[2])
    write('journal', {'phase': 'RESERVED', 'epoch': os.urandom(16).hex()})
    ctl('start', UNITS[0])
    armed = await_result('ARMED', min(end, time.monotonic() + 8))
    passed(CASES[0], armed)
    report['oom_ancestry'] = {}
    for unit in UNITS[:3]:
        props = properties(unit)
        group = Path('/sys/fs/cgroup') / props['ControlGroup'].lstrip('/')
        ancestors = []
        for domain in (group, *group.parents):
            if domain == Path('/sys'):
                break
            if domain == Path('/sys/fs'):
                break
            entry = {'domain': str(domain)}
            for field in ('memory.oom.group', 'memory.max', 'memory.events', 'cgroup.events'):
                path = domain / field
                if path.exists():
                    entry[field] = path.read_text()
            require(entry.get('memory.oom.group', '0').strip() == '0', 'ancestor-group-oom')
            ancestors.append(entry)
            if domain == Path('/sys/fs/cgroup'):
                break
        report['oom_ancestry'][unit] = {'ancestors': ancestors,
            'ManagedOOMSwap': props.get('ManagedOOMSwap'),
            'ManagedOOMMemoryPressure': props.get('ManagedOOMMemoryPressure')}
    initial_worker = properties(UNITS[1])['InvocationID']
    counter = Path('/run/cm14-worker/starts')
    marker_end = min(end, time.monotonic() + 3)
    while not counter.exists() and time.monotonic() < marker_end:
        time.sleep(0.02)
    require(counter.exists(), 'worker-start-marker')
    starts = counter.read_text()
    require(starts == '1', 'worker-start-count')
    passed(CASES[1], control('full', 'FULL_REJECTED'))
    progress(report['authorization'], 'sender-control-entered')
    rejected = control('rejected-sender', 'SENDER_REJECTED')
    progress(report['authorization'], 'sender-control-returned')
    passed(CASES[2], rejected)
    recovered = recover()
    require(recovered['hup'] == {'worker': False, 'agent': False}, 'alive-handback')
    passed(CASES[3], recovered)
    ctl('stop', UNITS[1])
    ctl('stop', UNITS[2])
    ctl('stop', UNITS[0])  # Inactive, not merely failed: exercise reference GC retention.
    ctl('reset-failed', UNITS[0])
    run_raw(('/usr/bin/systemctl', 'daemon-reload'))
    require(properties(UNITS[3])['ActiveState'] == 'active', 'anchor-lost')
    props = properties(UNITS[0])
    require(props['ActiveState'] == 'inactive' and props['LoadState'] == 'loaded', 'anchor-gc')
    retained = dump()
    require(len(retained) == 2, 'hup-store-lost')
    passed(CASES[5], retained)
    passed(CASES[6], {'holder': {name: props[name] for name in ('ActiveState', 'LoadState')},
                     'raw_dump': retained})
    (STATE / 'result').unlink()
    (STATE / 'control').unlink(missing_ok=True)
    ctl('start', UNITS[0])
    dead = await_result('RECOVERED', min(end, time.monotonic() + 8))
    require(dead['hup'] == {'worker': True, 'agent': True}, 'hup-not-observed')
    passed(CASES[4], dead)
    passed(CASES[7], dead)
    require(Path('/run/cm14-worker/starts').read_text() == starts, 'second-worker-start')
    require(properties(UNITS[1])['InvocationID'] in ('', initial_worker), 'worker-replaced')
    passed(CASES[8], {'starts': starts, 'invocation': initial_worker})
    control('retire', 'RETIRED')
    partial = control('remove-worker', 'PARTIAL')
    recovered = recover()
    require(len(recovered['raw_dump']) == 1, 'partial-handback')
    passed(CASES[9], {'partial': partial, 'recovered': recovered})
    passed(CASES[10], control('cleanup', 'EMPTY'))
    passed(CASES[11], control('cleanup', 'EMPTY'))
    require(read('journal')['phase'] == 'RETIRED' and dump() == [], 'retirement-empty-proof')
    report['journal_snapshot'] = read('journal')
    report['retirement_verified'] = True


def first_failure(report, error, publication=False):
    value = category(error)
    if report['primary_failure'] is None:
        report['primary_failure'] = value
    if publication:
        report['cleanup_failures'].append('publication-' + value)
    if isinstance(error, Failure):
        report['cleanup_failures'].extend(error.secondary)


def may_remove(report):
    return (report['campaign_completed'] and report['retirement_verified']
            and report['primary_failure'] is None and not report['cleanup_failures'])


class Cancellation:
    def __init__(self, report):
        self.report = report
        self.previous = {}
        self.seen = False

    def handle(self, signum, frame):
        first_failure(self.report, KeyboardInterrupt())
        self.report['campaign_completed'] = False
        if not self.seen:
            self.seen = True
            raise KeyboardInterrupt()
        # Subsequent signals preserve first cause and cannot interrupt final evidence repeatedly.

    def install(self):
        for sig in (signal.SIGINT, signal.SIGTERM):
            self.previous[sig] = signal.signal(sig, self.handle)

    def restore(self):
        for sig, handler in self.previous.items():
            try:
                signal.signal(sig, handler)
            except BaseException as error:
                first_failure(self.report, error)
                self.report['campaign_completed'] = False
                self.report['cleanup_failures'].append('signal-restoration-' + category(error))
                # Attempt the other handler once even when this restoration is uncertain.


def receipt(report, stage):
    # Export only this invocation's memory, never a path in possibly occupied state.
    encoded = None
    try:
        envelope = {'schema': 1, 'stage': stage, 'report': report}
        raw = json.dumps(envelope, sort_keys=True, separators=(',', ':')).encode()
        require(len(raw) <= 196608, 'serial-evidence-limit')
        encoded = base64.b64encode(raw).decode()
    except BaseException as error:
        # A full export failure cannot suppress the original sanitized receipt.
        first_failure(report, error, publication=True)
    print(json.dumps({'cm14_evidence': encoded, 'qualification': 'INCOMPLETE', 'primary_failure': report['primary_failure'],
                      'cleanup_failures': report['cleanup_failures'],
                      'passed_cases': sum(c['status'] == 'PASS' for c in report['cases'].values()),
                      'required_cases': len(CASES), 'not_run_gaps': len(GAPS),
                      'command_containment': CONTAINMENT,
                      'campaign_completed': report['campaign_completed'],
                      'receipt_stage': stage}), flush=True)


def finalize(report, installed, started, owned, cancellation):
    if owned.fd is not None:
        try:
            owned.publish(report)
        except BaseException as error:
            first_failure(report, error, publication=True)
    # Stop every owned cgroup before unit removal. Do not discard either failure.
    if started:
        for unit in UNITS[:3]:
            try:
                ctl('stop', unit)
                require(properties(unit)['MainPID'] == '0', 'cleanup-mainpid')
            except BaseException as error:
                if isinstance(error, (KeyboardInterrupt, SystemExit)):
                    first_failure(report, error)
                report['cleanup_failures'].append('stop-' + unit)
        if may_remove(report):
            try:
                ctl('stop', UNITS[3])
            except BaseException:
                report['cleanup_failures'].append('stop-anchor')
    # Preserve units, store and evidence on uncertain process cleanup; destroy VM externally.
    if may_remove(report):
        for dest in installed:
            if not may_remove(report):
                break
            try:
                dest.unlink()
            except BaseException:
                report['cleanup_failures'].append('unit-removal')
        if installed:
            try:
                run_raw(('/usr/bin/systemctl', 'daemon-reload'))
            except BaseException:
                report['cleanup_failures'].append('reload')
    for case in CASES:
        report['cases'].setdefault(case, {'status': 'NOT RUN'})
    # Gaps ensure this cannot emit a blanket qualification PASS.
    if owned.fd is not None:
        try:
            owned.publish(report)
        except BaseException as error:
            first_failure(report, error, publication=True)
        try:
            owned.close()
        except BaseException:
            report['cleanup_failures'].append('state-close-unknown')
    # Flush the first-cause receipt before restoring interruptible/default handlers.
    # A later signal may prevent another receipt, but cannot erase this prior output.
    try:
        receipt(report, 'before-handler-restoration')
    except BaseException as error:
        first_failure(report, error, publication=True)
    try:
        cancellation.restore()
    except BaseException as error:
        first_failure(report, error)
        report['campaign_completed'] = False
        report['cleanup_failures'].append('signal-restoration-' + category(error))
    try:
        receipt(report, 'after-handler-restoration')
    except BaseException as error:
        first_failure(report, error, publication=True)


def main():
    require(len(sys.argv) == 1, 'no-arguments')
    identity = guard()  # No mutations or service calls before authorization.
    progress(identity, 'guest-entered')
    report = {'kind': 'PROTOCOL_FIXTURE_ONLY', 'authorization': identity, 'cases': {},
              'gaps': {name: 'NOT RUN' for name in GAPS}, 'primary_failure': None,
              'cleanup_failures': [], 'qualification': 'INCOMPLETE',
              'command_containment': CONTAINMENT, 'campaign_completed': False,
              'retirement_verified': False}
    installed = []
    started = False
    owned = OwnedState(STATE)
    cancellation = Cancellation(report)
    cancellation.install()
    try:
        Notify()  # Required symbols, public SONAME only; no install/fetch fallback.
        require(hasattr(os, 'pidfd_open'), 'pidfd-unavailable')
        require(not os.path.lexists(STATE), 'occupied-fixture')
        for unit in UNITS:
            props = properties(unit)
            require(props['LoadState'] == 'not-found', 'unit-collision')
            for directory in ('/run/systemd/system', '/etc/systemd/system', '/usr/lib/systemd/system'):
                require(not (Path(directory) / unit).exists(), 'unit-file-collision')
        for role in ('worker', 'agent'):
            require(not Path('/run/cm14-' + role).exists(), 'synthetic-directory-collision')
        owned.acquire()
        for unit in UNITS:
            source = trusted(ROOT / 'units' / unit)
            dest = Path('/run/systemd/system') / unit
            with dest.open('xb') as stream:
                stream.write(source.read_bytes())
            installed.append(dest)
        run_raw(('/usr/bin/systemctl', 'daemon-reload'))
        started = True
        for unit in UNITS:
            props = properties(unit)
            require(props['FragmentPath'] == '/run/systemd/system/' + unit
                    and props.get('DropInPaths', '') == '', 'effective-unit-drift')
            if unit.endswith('.service'):
                require(props['Restart'] == 'no', 'automatic-restart')
        require(properties(UNITS[0])['NotifyAccess'] == 'main', 'notify-access')
        # Separate sibling cgroups have no OOM-limited shared fixture slice.
        # These reads are diagnostic; actual OOM kill and oomd qualification remain gaps.
        report['manager_version'] = run_raw(('/usr/bin/systemctl', '--version'))
        report['kernel'] = Path('/proc/version').read_text()
        report['python_sha256'] = hashlib.sha256(Path('/proc/self/exe').read_bytes()).hexdigest()
        report['libsystemd_mappings'] = [line for line in Path('/proc/self/maps').read_text().splitlines()
                                        if 'libsystemd.so.' in line]
        libraries = {line.split()[-1] for line in report['libsystemd_mappings']}
        require(len(libraries) == 1, 'libsystemd-map')
        report['libsystemd_sha256'] = {p: hashlib.sha256(Path(p).read_bytes()).hexdigest() for p in libraries}
        campaign(report, time.monotonic() + 100)
        require(all(report['cases'].get(case, {}).get('status') == 'PASS' for case in CASES)
                and report['retirement_verified'], 'campaign-incomplete')
        report['campaign_completed'] = True
    except BaseException as error:
        first_failure(report, error)
    finally:
        try:
            progress(identity, 'campaign-finalizing')
        except BaseException as error:
            first_failure(report, error, publication=True)
        finalize(report, installed, started, owned, cancellation)
    return 1 if report['primary_failure'] or report['cleanup_failures'] else 2


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception as error:
        print(json.dumps({'status': 'UNAVAILABLE', 'category': str(error) if isinstance(error, Failure)
                          else 'prerequisite-unavailable'}))
        sys.exit(3)
