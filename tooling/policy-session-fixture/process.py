#!/usr/bin/python3
"""Synthetic service processes; no product protocol, shell or caller-chosen targets."""
import os
from pathlib import Path
import select
import sys
import time

from common import (Failure, Notify, ROOT, STATE, UNITS, confirm, ctl, dump, fd_identity,
                    guard, properties, read, require, trusted, write, category, publish_failure)


def hup(fd):
    poll = select.poll()
    poll.register(fd, select.POLLIN | select.POLLHUP | select.POLLERR)
    return any(events & select.POLLHUP for _, events in poll.poll(0))


def synthetic(role):
    require(os.geteuid() != 0 and role in ('worker', 'agent'), 'synthetic-user')
    require(Path('/sys/class/dmi/id/sys_vendor').read_text().strip() == 'QEMU', 'qemu-required')
    trusted(ROOT, True)
    # The synthetic processes have no input, policy descriptors, fork or IPC path.
    # This Python process is NOT the future pre-CLR/no-old-rings/TSYNC proof.
    directory = Path('/run/cm14-' + role)
    counter = directory / 'starts'
    count = int(counter.read_text()) if counter.exists() else 0
    counter.write_text(str(count + 1))
    end = time.monotonic() + 90
    while time.monotonic() < end:
        time.sleep(0.05)


def bind(unit):
    before = properties(unit)
    pid = int(before['MainPID'])
    require(pid > 1 and before['ActiveState'] == 'active', 'bind-live')
    fd = os.pidfd_open(pid, 0)
    after = properties(unit)
    require(before['InvocationID'] == after['InvocationID'] and before['MainPID'] == after['MainPID'],
            'bind-race')
    require(Path('/proc/' + str(pid) + '/ns/pid').stat().st_ino == Path('/proc/1/ns/pid').stat().st_ino,
            'bind-namespace')
    require(Path('/proc/' + str(pid) + '/cgroup').read_text().strip() == '0::' + after['ControlGroup'],
            'bind-domain')
    require(not hup(fd), 'bind-hup')
    return fd, after['InvocationID']


def terminal(unit, invocation):
    props = properties(unit)
    require(props['ActiveState'] in ('inactive', 'failed') and props['MainPID'] == '0', 'terminal')
    require(props['InvocationID'] in ('', invocation), 'terminal-generation')
    require(props['Job'] in ('', '0') or props['Job'].startswith('0 '), 'pending-job')
    group = props['ControlGroup']
    if group:
        domain = Path('/sys/fs/cgroup') / group.lstrip('/')
        require(not domain.exists() or ('populated 0' in (domain / 'cgroup.events').read_text()),
                'populated-domain')


HOLDER_STATE_ID = None


def holder():
    global HOLDER_STATE_ID
    guard()
    require(properties(UNITS[3])['ActiveState'] == 'active', 'reference-inactive')
    notify = Notify()
    inherited = notify.inherited()
    journal = read('journal')
    state_stat = trusted(STATE, True).stat()
    HOLDER_STATE_ID = (state_stat.st_dev, state_stat.st_ino)
    recovery = journal['phase'] != 'RESERVED'
    if not recovery:
        require(not inherited and not dump(), 'pristine-store')
        journal['phase'] = 'START_ATTEMPTED'
        write('journal', journal)  # Consume before effect. Never reset this on failure.
        ctl('start', UNITS[1])
        worker, worker_id = bind(UNITS[1])
        agent, agent_id = bind(UNITS[2])
        fds = {'worker': worker, 'agent': agent}
        expected = {role + '-' + journal['epoch']: fd_identity(fd) for role, fd in fds.items()}
        require(expected['worker-' + journal['epoch']][3] != expected['agent-' + journal['epoch']][3],
                'legacy-pidfs-identities')
        journal.update(phase='BOUND', expected=expected, worker_id=worker_id, agent_id=agent_id)
        write('journal', journal)
        for role, fd in fds.items():
            notify.send('FDSTORE=1\nFDPOLL=0\nFDNAME=' + role + '-' + journal['epoch'], fd)
        notify.barrier()
        rows = dump()
        confirm(rows, expected)
        journal['phase'] = 'ARMED'
        write('journal', journal)
        write('result', {'phase': 'ARMED', 'raw_dump': rows, 'recovery': False})
    else:
        require(journal['phase'] in ('ARMED', 'RETIRED'), 'prearmed-no-work-unqualified')
        rows = dump()
        expected = journal['expected']
        if journal['phase'] == 'RETIRED':
            expected = {row[0]: journal['expected'][row[0]] for row in rows
                        if row[0] in journal['expected']}
        confirm(rows, expected)
        require(set(inherited) == set(expected), 'handback-membership')
        for name, fd in inherited.items():
            require(fd_identity(fd) == expected[name], 'handback-identity')
        fds = {name.split('-')[0]: fd for name, fd in inherited.items()}
        write('result', {'phase': 'RECOVERED', 'raw_dump': rows,
                         'hup': {role: hup(fd) for role, fd in fds.items()}, 'recovery': True})
    sequence = -1
    end = time.monotonic() + 75
    while time.monotonic() < end:
        if (STATE / 'control').exists():
            control = read('control')
            if control['sequence'] > sequence:
                sequence = control['sequence']
                action = control['action']
                require(action in ('full', 'rejected-sender', 'retire', 'remove-worker', 'cleanup'),
                        'control-action')
                if action == 'full':
                    third = os.pidfd_open(os.getpid(), 0)
                    try:
                        sent = notify.send('FDSTORE=1\nFDPOLL=0\nFDNAME=overflow', third)
                        notify.barrier()
                        rows = dump()
                        confirm(rows, journal['expected'])
                        write('result', {'phase': 'FULL_REJECTED', 'send': sent,
                                         'barrier': 'processed', 'raw_dump': rows, 'sequence': sequence})
                    finally:
                        os.close(third)
                elif action == 'rejected-sender':
                    # Intentional wrong main sender: root child in same service is still rejected.
                    child = os.fork()
                    if child == 0:
                        try:
                            notify.send('FDSTOREREMOVE=1\nFDNAME=worker-' + journal['epoch'])
                            os._exit(0)
                        except Exception:
                            os._exit(2)
                    _, status = os.waitpid(child, 0)
                    require(os.waitstatus_to_exitcode(status) == 0, 'rejection-send')
                    notify.barrier()
                    rows = dump()
                    confirm(rows, journal['expected'])
                    write('result', {'phase': 'SENDER_REJECTED', 'raw_dump': rows, 'sequence': sequence})
                elif action == 'retire':
                    require(set(fds) == {'worker', 'agent'} and all(hup(fd) for fd in fds.values()),
                            'original-hup-required')
                    terminal(UNITS[1], journal['worker_id'])
                    terminal(UNITS[2], journal['agent_id'])
                    journal['phase'] = 'RETIRED'
                    write('journal', journal)
                    write('result', {'phase': 'RETIRED', 'sequence': sequence})
                else:
                    require(journal['phase'] == 'RETIRED', 'retired-required')
                    names = ['worker'] if action == 'remove-worker' else ['worker', 'agent']
                    for role in names:
                        notify.send('FDSTOREREMOVE=1\nFDNAME=' + role + '-' + journal['epoch'])
                        notify.barrier()
                    rows = dump()
                    remaining = ({'agent-' + journal['epoch']: journal['expected']['agent-' + journal['epoch']]}
                                 if action == 'remove-worker' else {})
                    confirm(rows, remaining)
                    write('result', {'phase': 'PARTIAL' if rows else 'EMPTY',
                                     'raw_dump': rows, 'sequence': sequence})
        time.sleep(0.05)
    raise Failure('holder-lease-expired')


if __name__ == '__main__':
    try:
        require(len(sys.argv) == 2 and sys.argv[1] in ('holder', 'worker', 'agent'), 'fixed-role')
        if sys.argv[1] == 'holder':
            holder()
        else:
            synthetic(sys.argv[1])
    except BaseException as error:
        first = category(error)
        secondary = list(error.secondary) if isinstance(error, Failure) else []
        if len(sys.argv) == 2 and sys.argv[1] == 'holder' and HOLDER_STATE_ID is not None:
            secondary.extend(publish_failure('result', {'phase': 'FAIL', 'category': first,
                                                       'cleanup_failures': secondary},
                                                       expected_state=HOLDER_STATE_ID))
        print('FAIL:' + first + ':secondary=' + ','.join(secondary[:16]), flush=True)
        sys.exit(1)
