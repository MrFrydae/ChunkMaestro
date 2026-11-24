#!/usr/bin/env python3
"""Bounded self-context diagnostics. No product authority. Python >= 3.11."""
import datetime
import errno
import hashlib
import json
import os
from pathlib import Path
import re
import sys
import tempfile
import time

SCHEMA = 'chunkmaestro.self-context.v1'
BLOB_LIMIT = 65536
READ_CALL_LIMIT = 64
TOTAL_LIMIT = 1048576
OUTPUT_LIMIT = 262144
SECONDS = 5.0  # Cooperative checks only; a coordinator must supervise native I/O.
RUN_ID = re.compile(r'[A-Za-z0-9][A-Za-z0-9_-]{0,31}\Z')
GAPS = {
    'G1': 'Patched running binary/modules/generated config/source/package closure UNKNOWN',
    'G2': 'Per-object statx/mount/idmap and initial namespace proof UNKNOWN',
    'G3': 'Complete LSM policy/domain/labels and seccomp/loader semantics UNKNOWN',
    'G4': 'Protected installer/effective launch/maintenance invalidation UNKNOWN',
    'G5': 'Real same-inspector-context ACL controls C0-C6 NOT RUN',
    'G6': 'Whole helper/socket/bus/proc/backend proof UNKNOWN',
}
# Paths are internal constants. The CLI accepts neither path nor PID overrides.
SOURCES = {
    'boot': ('/proc/sys/kernel/random/boot_id', 'uuid'),
    'release': ('/proc/sys/kernel/osrelease', 'release'),
    'status': ('/proc/self/status', 'status'),
    'stat': ('/proc/self/stat', 'stat'),
    'userNamespace': ('/proc/self/ns/user', 'user'),
    'mountNamespace': ('/proc/self/ns/mnt', 'mnt'),
    'uidMap': ('/proc/self/uid_map', 'map'),
    'gidMap': ('/proc/self/gid_map', 'map'),
    'mounts': ('/proc/self/mountinfo', 'mounts'),
    'lsm': ('/sys/kernel/security/lsm', 'lsm'),
    'subject': ('/proc/self/attr/current', 'subject'),
}
STATUS_FIELDS = {
    'Uid': (4, 4294967295), 'Gid': (4, 4294967295),
    'Groups': (None, 4294967295), 'NoNewPrivs': (1, 1),
    'Seccomp': (1, 2), 'Seccomp_filters': (1, 65535),
}
CAPS = ('CapInh', 'CapPrm', 'CapEff', 'CapBnd', 'CapAmb')


def sha(data):
    return hashlib.sha256(data).hexdigest()


def utc():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def number(value, maximum=18446744073709551615):
    if not re.fullmatch(r'[0-9]{1,20}', value) or int(value) > maximum:
        raise ValueError('invalid numeric field')
    return int(value)


def parse(kind, data):
    """Pure offline parser. No raw private process/path/label text is returned."""
    if not data or len(data) > BLOB_LIMIT or b'\x00' in data:
        raise ValueError('empty, oversized or NUL input')
    text = data.decode('ascii', errors='strict')
    if any(len(line) > 8192 for line in text.splitlines()):
        raise ValueError('overlong record')
    if kind == 'status':
        fields = {}
        for line in text.splitlines():
            key, sep, value = line.partition(':')
            if not sep or not re.fullmatch(r'[A-Za-z_][A-Za-z_0-9]{0,63}', key) or key in fields:
                raise ValueError('malformed or duplicate status field')
            fields[key] = value.strip()
        result = {}
        for key, (count, maximum) in STATUS_FIELDS.items():
            values = fields[key].split()
            if (count is not None and len(values) != count) or len(values) > 256:
                raise ValueError('invalid field cardinality')
            result[key] = [number(value, maximum) for value in values]
        for key in CAPS:
            if not re.fullmatch(r'[0-9a-fA-F]{16}', fields[key]):
                raise ValueError('invalid capability mask')
            result[key] = fields[key].lower()
        # Unknown kernel fields are deliberately discarded, never authority inputs.
        return result
    if kind == 'stat':
        match = re.fullmatch(r'([0-9]+) \(.*\) ([A-Za-z]) (.*)\n', text)
        if not match:
            raise ValueError('invalid process stat')
        tail = match[3].split()
        if not 19 <= len(tail) <= 64:
            raise ValueError('incomplete process stat')
        for value in tail:
            if not re.fullmatch(r'-?[0-9]{1,20}', value):
                raise ValueError('invalid stat number')
        return {'pid': number(match[1], 4194304), 'startTicks': number(tail[18])}
    if kind == 'map':
        rows = []
        for line in text.splitlines():
            values = line.split()
            if len(values) != 3 or len(rows) >= 16:
                raise ValueError('invalid mapping')
            row = [number(value, 4294967295) for value in values]
            if row[2] == 0 or any(row[i] + row[2] > 4294967296 for i in (0, 1)):
                raise ValueError('mapping overflow')
            if any(any(max(row[i], old[i]) < min(row[i] + row[2], old[i] + old[2])
                       for i in (0, 1)) for old in rows):
                raise ValueError('overlapping mapping')
            rows.append(row)
        if not rows:
            raise ValueError('empty mapping')
        return rows
    if kind in ('user', 'mnt'):
        match = re.fullmatch(kind + r':\[([0-9]{1,20})\]', text)
        if not match:
            raise ValueError('invalid namespace handle')
        return number(match[1])
    if kind == 'uuid':
        if not re.fullmatch(r'[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\n', text):
            raise ValueError('invalid boot identity')
        return text.strip()
    if kind == 'release':
        if not re.fullmatch(r'[A-Za-z0-9_.+~-]{1,256}\n', text):
            raise ValueError('invalid release')
        return text.strip()  # Observation only, not binary authentication.
    if kind == 'lsm':
        if not re.fullmatch(r'[a-z0-9_-]{1,32}(?:,[a-z0-9_-]{1,32}){0,31}\n?', text):
            raise ValueError('invalid LSM list')
        values = text.strip().split(',')
        if len(set(values)) != len(values):
            raise ValueError('duplicate LSM')
        return values
    if kind == 'subject':
        if not re.fullmatch(r'[ -~]{1,4096}\n?', text):
            raise ValueError('invalid subject')
        return {'labelSha256': sha(data), 'semantics': 'UNKNOWN'}
    if kind == 'mounts':
        rows, ids = [], set()
        for line in text.splitlines():
            parts = line.split(' ')
            if parts.count('-') != 1 or len(rows) >= 128:
                raise ValueError('invalid or excessive mount records')
            divider = parts.index('-')
            if divider < 6 or len(parts) != divider + 4 or any(not p for p in parts):
                raise ValueError('malformed mount record')
            mount_id, parent = number(parts[0]), number(parts[1])
            if mount_id in ids or not re.fullmatch(r'[0-9]{1,10}:[0-9]{1,10}', parts[2]):
                raise ValueError('duplicate mount or bad device')
            ids.add(mount_id)
            if not parts[3].startswith('/') or not parts[4].startswith('/'):
                raise ValueError('invalid mount path')
            filesystem = parts[divider + 1]
            if not re.fullmatch(r'[a-zA-Z0-9_.-]{1,32}', filesystem):
                raise ValueError('invalid filesystem')
            options = parts[5].split(',')
            if (len(options) != len(set(options)) or
                    len(set(options) & {'ro', 'rw'}) != 1 or
                    any(not re.fullmatch(r'[A-Za-z0-9_=.:+-]{1,128}', option) for option in options)):
                raise ValueError('duplicate mount option')
            # No roots, targets, device sources, arbitrary options or path hashes.
            rows.append({'id': mount_id, 'parent': parent, 'device': parts[2],
                         'filesystem': filesystem if filesystem in ('ext4', 'proc', 'sysfs', 'tmpfs', 'overlay') else 'OTHER',
                         'flags': sorted(set(options) & {'ro', 'rw', 'nosuid', 'nodev', 'noexec', 'noacl'})})
        if not rows:
            raise ValueError('empty topology')
        return rows
    raise ValueError('unsupported parser')


class LinuxSelfReader:
    """Only fixed self-context paths. Never receives caller-selected paths."""
    def read(self, key, limit=BLOB_LIMIT + 1):
        path, kind = SOURCES[key]
        if not 1 <= limit <= BLOB_LIMIT + 1:
            raise ValueError('invalid read bound')
        if kind in ('user', 'mnt'):
            return os.readlink(path).encode('ascii')
        with open(path, 'rb', buffering=0) as stream:
            chunks = []
            remaining = limit
            for _ in range(READ_CALL_LIMIT):
                chunk = stream.read(remaining)
                if not chunk:
                    return b''.join(chunks)
                if len(chunk) > remaining:
                    raise OverflowError('read exceeded byte bound')
                chunks.append(chunk)
                remaining -= len(chunk)
                if remaining == 0:
                    return b''.join(chunks)
            raise OverflowError('read fragment cap exceeded')


class Budget:
    def __init__(self, clock=time.monotonic):
        self.clock = clock
        self.start = clock()
        self.used = 0

    def check(self):
        if self.used >= TOTAL_LIMIT:
            raise OverflowError('total read cap exhausted')
        if self.clock() - self.start >= SECONDS:
            raise TimeoutError('cooperative deadline exceeded')

    def charge(self, count):
        self.used += count
        if self.used > TOTAL_LIMIT:
            raise OverflowError('total read cap exceeded')

    def release(self, count):
        if not 0 <= count <= self.used:
            raise ValueError('invalid reservation release')
        self.used -= count


def sample(reader, key, budget, run_id, evidence_class):
    started, mono = utc(), budget.clock()
    record = {'source': SOURCES[key][0], 'api': 'readlink' if key.endswith('Namespace') else 'read',
              'runId': run_id, 'utcStart': started, 'monotonicStart': mono,
              'identityRef': 'selfIdentity', 'namespaceRef': 'namespaceBinding',
              'sourceRevision': 'UNKNOWN', 'scope': 'SELF_ONLY', 'sanitizedSha256': None,
              'status': 'UNKNOWN', 'errno': None, 'bytes': 0, 'bytesKnown': True,
              'readBudgetBytes': 0, 'sha256': None,
              'evidenceClass': 'unknown', 'rawRetention': 'NOT RETAINED', 'value': None}
    try:
        budget.check()
        allowance = min(BLOB_LIMIT + 1, TOTAL_LIMIT - budget.used)
        # readlink has no caller buffer-size argument. Reserve the full source
        # allowance before querying these fixed Linux namespace handles.
        if SOURCES[key][1] in ('user', 'mnt') and allowance < BLOB_LIMIT + 1:
            raise OverflowError('insufficient namespace read reservation')
        budget.charge(allowance)
        record.update(readBudgetBytes=allowance, bytesKnown=False)
        # Keep the reservation on any failure: earlier short reads may already
        # have consumed bytes that the exception cannot report back to us.
        data = reader.read(key, allowance)
        record.update(bytes=len(data), sha256=sha(data))
        if len(data) > allowance:
            raise OverflowError('reader exceeded reservation')
        budget.release(allowance - len(data))
        record.update(readBudgetBytes=len(data), bytesKnown=True)
        budget.check()
        if len(data) > BLOB_LIMIT:
            raise OverflowError('per-read cap exceeded')
        record['value'] = parse(SOURCES[key][1], data)
        record['sanitizedSha256'] = sha(json.dumps(record['value'], sort_keys=True).encode('ascii'))
        record.update(status='OBSERVED' if evidence_class == 'observed' else 'OFFLINE_FIXTURE',
                      evidenceClass=evidence_class)
    except (ValueError, KeyError, UnicodeError):
        record['status'] = 'MALFORMED'
    except OverflowError:
        record['status'] = 'OVERSIZE'
    except TimeoutError:
        record['status'] = 'TIMEOUT'
    except OSError as error:
        record['errno'] = error.errno
        record['status'] = {errno.EACCES: 'DENIED', errno.EPERM: 'DENIED', errno.ENOENT: 'MISSING',
                            errno.EOPNOTSUPP: 'UNSUPPORTED', errno.EINTR: 'INTERRUPTED'}.get(error.errno, 'IO_ERROR')
    except KeyboardInterrupt:
        record['status'] = 'INTERRUPTED'
    record.update(utcEnd=utc(), monotonicEnd=budget.clock())
    return record


def collect(reader, run_id, source_hash, *, fixture=False, clock=time.monotonic, pid=None):
    """Internal fixture boundary, never a CLI trust option or qualification API."""
    if not RUN_ID.fullmatch(run_id) or not re.fullmatch(r'[0-9a-f]{64}', source_hash):
        raise ValueError('invalid run ID')
    budget = Budget(clock)
    report = {'schema': SCHEMA, 'runId': run_id, 'runIdClass': 'supplied',
              'sourceSha256': source_hash, 'sourceHashClass': 'supplied' if fixture else 'observed',
              'sourceClosure': 'UNKNOWN', 'sourceRevision': 'UNKNOWN',
              'runtimeQualification': 'UNRESOLVED', 'productPromotion': False,
              'targetInspectorContext': 'NOT ESTABLISHED', 'remainingProofGaps': dict(GAPS),
              'collectionCompletion': 'INCOMPLETE', 'domain': 'OFFLINE_FIXTURE' if fixture else 'SELF_CONTEXT',
              'utcStart': utc(), 'monotonicStart': budget.start, 'evidence': [], 'drift': []}
    samples = []
    expected = 'OFFLINE_FIXTURE' if fixture else 'OBSERVED'
    for sweep in range(2):
        current = {}
        for key in SOURCES:
            record = sample(reader, key, budget, run_id, 'supplied' if fixture else 'observed')
            record.update(key=key, sweep=sweep)
            current[key] = record
            report['evidence'].append(record)
        samples.append(current)
    for key in SOURCES:
        before, after = (s[key] for s in samples)
        if before['status'] == after['status'] == expected and before['sha256'] != after['sha256']:
            # Ignore unrelated status/stat text, but compare their selected context/identity.
            if key not in ('status', 'stat') or before['value'] != after['value']:
                report['drift'].append(key)
    actual_pid = os.getpid() if pid is None else pid
    for current in samples:
        identity = current['stat']['value']
        if identity is not None and identity['pid'] != actual_pid:
            report['drift'].append('selfPid')
    report['selfIdentity'] = samples[0]['stat']['value']
    report['namespaceBinding'] = {key: samples[0][key]['value'] for key in ('userNamespace', 'mountNamespace')}
    if all(r['status'] == expected for r in report['evidence']) and not report['drift']:
        report['collectionCompletion'] = 'COMPLETE'
    known_bytes = sum(record['bytes'] for record in report['evidence'] if record['bytesKnown'])
    report.update(utcEnd=utc(), monotonicEnd=budget.clock(), readBudgetBytes=budget.used,
                  readBytesKnown=known_bytes,
                  readBytes=known_bytes if all(record['bytesKnown'] for record in report['evidence']) else None)
    return report


def encode(report):
    data = (json.dumps(report, sort_keys=True, indent=2) + '\n').encode('ascii')
    if len(data) > OUTPUT_LIMIT:
        raise OverflowError('output cap exceeded')
    return data


def write_new(directory, name, data):
    with open(directory / name, 'xb') as stream:
        os.chmod(directory / name, 0o600)
        if stream.write(data) != len(data):
            raise OSError(errno.EIO, 'short output')
        stream.flush()
        os.fsync(stream.fileno())


def publish(directory, report):
    """New private directory only; final receipt link commits the publication."""
    data = encode(report)
    write_new(directory, 'report.partial', data)
    os.link(directory / 'report.partial', directory / 'report.json')
    (directory / 'report.partial').unlink()
    write_new(directory, 'receipt.partial', encode({'schema': SCHEMA, 'reportSha256': sha(data),
              'collectionCompletion': report['collectionCompletion'], 'runtimeQualification': 'UNRESOLVED',
              'productPromotion': False}))
    os.link(directory / 'receipt.partial', directory / 'receipt.json')


def main(argv=None):
    args = sys.argv[1:] if argv is None else argv
    # Refuse before any source/proc/output file access, including on non-Linux.
    if len(args) != 2 or args[0] != 'collect' or not RUN_ID.fullmatch(args[1]):
        print('Refused: requires collect RUN_ID (1-32 ASCII letters/digits/_/-).', file=sys.stderr)
        return 2
    if sys.platform != 'linux' or sys.version_info < (3, 11):
        print('Refused: actual collection requires Linux and Python >=3.11.', file=sys.stderr)
        return 2
    directory = None
    try:
        directory = Path(tempfile.mkdtemp(prefix='cm-self-' + args[1] + '-', dir='/tmp'))
        write_new(directory, 'INCOMPLETE', b'No receipt means incomplete; partial files are not evidence completion.\n')
        with open(__file__, 'rb') as source:
            data = source.read(BLOB_LIMIT + 1)
        if len(data) > BLOB_LIMIT:
            raise OverflowError('source cap exceeded')
        report = collect(LinuxSelfReader(), args[1], sha(data))
        publish(directory, report)
        print(str(directory))
        return 0 if report['collectionCompletion'] == 'COMPLETE' else 1
    except (OSError, ValueError, OverflowError, KeyboardInterrupt):
        print('Collection/output incomplete' + (': ' + str(directory) if directory else ''), file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
