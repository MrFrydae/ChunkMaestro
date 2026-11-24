#!/usr/bin/env python3
"""Owner-dispatched infrastructure smoke. No product acceptance implied."""
import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import platform
import resource
import shutil
import signal
import socket
import re
import secrets
import sys
import subprocess
import tempfile
import time

HERE = Path(__file__).resolve().parent
PIN = json.loads((HERE / 'image.json').read_text())
TOOLS = ('curl', 'qemu-system-x86_64', 'qemu-img', 'cloud-localds', 'ssh-keygen', 'ssh')
SMOKE = '''set -eu
. /etc/os-release
test "$ID" = debian
test "$VERSION_ID" = 13
test "$(cat /proc/1/comm)" = systemd
test "$(id -un)" = fixture
systemctl show --property=Version --value | grep -Eq '^[0-9]'
printf 'debian13 systemd-pid1 manager-query-ok\\n'
'''
EXPECTED = b'debian13 systemd-pid1 manager-query-ok\n'


class Failure(Exception):
    pass


def event(phase, status):
    # Never print command arguments, child output, paths or environment.
    print(json.dumps({'phase': phase, 'status': status}), flush=True)


def failure_name(error):
    if isinstance(error, Failure) and re.fullmatch(r'[a-z0-9-]{1,80}', str(error)):
        return str(error)
    return 'internal'


def record_failure(phase, error):
    event('fixture-' + phase + '-' + failure_name(error), 'failed')


def child_env(home):
    return {'PATH': '/usr/bin:/bin', 'HOME': str(home), 'LANG': 'C', 'LC_ALL': 'C'}


def limits():
    os.umask(0o077)
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
    resource.setrlimit(resource.RLIMIT_FSIZE, (4 * 1024**3, 4 * 1024**3))
    resource.setrlimit(resource.RLIMIT_CPU, (900, 900))


def inventory():
    statuses = {
        'platform': 'ready' if platform.system() == 'Linux' and platform.machine() == 'x86_64' else 'unsupported',
        'nonroot': 'ready' if os.geteuid() != 0 else 'root-refused',
        'python': 'ready' if sys.version_info >= (3, 11) else 'unsupported',
    }
    for tool in TOOLS:
        statuses[tool] = 'ready' if shutil.which(tool, path='/usr/bin:/bin') else 'missing'
    statuses['execution-mode'] = 'tcg-system-qemu64-multi'
    for name, status in statuses.items():
        event('prerequisite-' + name, status)
    return statuses


def prerequisites():
    statuses = inventory()
    for name in ('platform', 'nonroot', 'python'):
        if statuses[name] != 'ready':
            raise Failure('preflight-' + ('root' if name == 'nonroot' else name))
    if any(statuses[t] != 'ready' for t in TOOLS):
        raise Failure('preflight-tools')


def validate_provenance(sums, metadata):
    if hashlib.sha256(sums).hexdigest() != PIN['checksums_sha256']:
        raise Failure('provenance-checksums')
    lines = sums.decode('ascii').splitlines()
    if lines.count(PIN['sha512'] + '  ' + PIN['filename']) != 1:
        raise Failure('provenance-entry')
    if hashlib.sha512(metadata).hexdigest() != PIN['metadata_sha512']:
        raise Failure('provenance-metadata')
    info = json.loads(metadata)['items'][0]['data']['info']
    if any(info[k] != v for k, v in dict(arch='amd64', release='trixie',
            release_id='13', type='official', vendor='genericcloud',
            version='20261103-2651').items()):
        raise Failure('provenance-identity')


class Harness:
    def __init__(self, root):
        self.root = root
        self.children = []
        self.campaign_output = None
        self.deadline = time.monotonic() + 1200

    def environment(self):
        return child_env(self.root)

    def start(self, args):
        out = tempfile.TemporaryFile(dir=self.root)
        p = subprocess.Popen(args, cwd=self.root, env=self.environment(),
                             stdin=subprocess.DEVNULL, stdout=out, stderr=out,
                             start_new_session=True, preexec_fn=limits)
        self.children.append((p, out))
        return p, out

    def wait(self, p, out, phase, seconds):
        end = min(time.monotonic() + seconds, self.deadline)
        beat = 0
        while p.poll() is None:
            now = time.monotonic()
            self.check_output_limits(phase)
            if now >= end:
                raise Failure(phase + '-timeout-or-output-limit')
            if now >= beat:
                event(phase, 'waiting')
                beat = now + 10
            time.sleep(0.1)
        if p.returncode:
            raise Failure(phase + '-exit')
        self.check_output_limits(phase)
        out.seek(0)
        return out.read(1024**2)

    def check_output_limits(self, phase):
        # QEMU remains live while boot and SSH commands are supervised.
        if any(os.fstat(output.fileno()).st_size > 1024**2
               for _, output in self.children):
            raise Failure(phase + '-output-limit')

    def run(self, args, phase, seconds=30):
        event(phase, 'start')
        return self.wait(*self.start(args), phase, seconds)

    def cleanup(self):
        event('cleanup', 'start')
        # All commands get their own process group. Kill groups even if leader exited.
        for sig in (signal.SIGTERM, signal.SIGKILL):
            for p, _ in self.children:
                try:
                    os.killpg(p.pid, sig)
                except ProcessLookupError:
                    pass
            if sig == signal.SIGTERM:
                time.sleep(1)
                # Reap dead leaders before signalling remaining groups. On macOS,
                # signalling a group containing only a zombie may return EPERM.
                for p, _ in self.children:
                    p.poll()
        for p, _ in self.children:
            p.wait(timeout=5)
        # Linux subreaper adopts orphaned grandchildren; reap before deleting disks.
        end = time.monotonic() + 5
        while True:
            try:
                pid, _ = os.waitpid(-1, os.WNOHANG)
            except ChildProcessError:
                break
            if pid == 0:
                if time.monotonic() > end:
                    raise Failure('cleanup-descendants-live')
                time.sleep(0.05)
        try:
            if self.campaign_output is not None:
                self.check_output_limits('cleanup')
                campaign, output = self.campaign_output
                campaign.capture(os.pread(output.fileno(), 1024**2, 0), final=True)
        finally:
            for _, out in self.children:
                out.close()
        event('cleanup', 'reaped')

    def download(self, name, cap, seconds):
        self.run(['curl', '--fail', '--silent', '--show-error', '--proto', '=https',
                  '--location', '--proto-redir', '=https', '--max-redirs', '3',
                  '--tlsv1.2', '--connect-timeout', '20', '--max-time', str(seconds),
                  '--max-filesize', str(cap), '--output', name, PIN['base_url'] + name],
                 'download', seconds + 5)
        if (self.root / name).stat().st_size > cap:
            raise Failure('download-size')


def ssh_args(root, port):
    return ['ssh', '-F', '/dev/null', '-p', str(port), '-i', str(root / 'client'),
            '-o', 'BatchMode=yes', '-o', 'IdentitiesOnly=yes', '-o', 'IdentityAgent=none',
            '-o', 'StrictHostKeyChecking=yes', '-o', 'HostKeyAlgorithms=ssh-ed25519',
            '-o', 'UserKnownHostsFile=' + str(root / 'known_hosts'),
            '-o', 'GlobalKnownHostsFile=/dev/null', '-o', 'ConnectTimeout=5',
            '-o', 'ConnectionAttempts=1', '-o', 'ForwardAgent=no',
            '-o', 'ClearAllForwardings=yes', '-o', 'LogLevel=ERROR', 'fixture@127.0.0.1']


def readiness_marker(nonce):
    if not re.fullmatch(r'[0-9a-f]{32}', nonce):
        raise Failure('readiness-nonce')
    return 'CM-FIXTURE-READY-' + nonce


def wait_ready(h, vm, output, nonce):
    marker = readiness_marker(nonce).encode()
    end = min(time.monotonic() + 240, h.deadline)
    event('boot', 'start')
    while True:
        h.check_output_limits('boot')
        if vm.poll() is not None:
            raise Failure('boot-qemu-exit')
        if time.monotonic() >= end:
            raise Failure('boot-timeout')
        # pread does not move QEMU's shared stdout offset. Never emit console bytes.
        data = os.pread(output.fileno(), 1024**2, 0)
        if marker in [line.removesuffix(b'\r') for line in data.split(b'\n')[:-1]]:
            event('boot', 'fixture-bootstrap-ready')
            event('private-serial-sha256', hashlib.sha256(data).hexdigest())
            return
        event('boot', 'waiting')
        time.sleep(min(5, max(0, end - time.monotonic())))


def cloud_config(root, nonce):
    # JSON is valid YAML; no manual interpolation of secret key material.
    marker = readiness_marker(nonce)
    # Runs once in cloud-final's scripts-user stage, after users/SSH/write_files.
    # Restart only this disposable guest's SSH service so the fixed policy is active.
    ready = ('test "$(id -un fixture)" = fixture; '
             'test -s /home/fixture/.ssh/authorized_keys; '
             '/usr/sbin/sshd -t; systemctl restart ssh; '
             'printf "\\n' + marker + '\\n" > /dev/ttyS0')
    return '#cloud-config\n' + json.dumps({
        'users': [{'name': 'fixture', 'lock_passwd': True, 'shell': '/bin/sh',
                   'sudo': False, 'ssh_authorized_keys': [(root / 'client.pub').read_text().strip()]}],
        'disable_root': True, 'ssh_pwauth': False, 'ssh_deletekeys': True,
        'ssh_genkeytypes': [], 'ssh': {'emit_keys_to_console': False},
        'ssh_keys': {'ed25519_private': (root / 'host').read_text(),
                     'ed25519_public': (root / 'host.pub').read_text().strip()},
        'write_files': [{'path': '/etc/ssh/sshd_config.d/00-fixture.conf',
                         'content': 'PasswordAuthentication no\nKbdInteractiveAuthentication no\nPermitRootLogin no\nAllowUsers fixture\nAllowTcpForwarding no\nX11Forwarding no\nAllowAgentForwarding no\n'}],
        'runcmd': [['/bin/sh', '-ec', ready]],
        'package_update': False, 'package_upgrade': False})


def exercise(h, campaign=None):
    r = h.root
    h.download('SHA512SUMS', 65536, 30)
    meta = PIN['filename'].replace('.qcow2', '.json')
    h.download(meta, 131072, 30)
    validate_provenance((r / 'SHA512SUMS').read_bytes(), (r / meta).read_bytes())
    h.download(PIN['filename'], 1024**3, 300)
    base = r / PIN['filename']
    with base.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha512').hexdigest()
    event('image-size-bytes', str(base.stat().st_size))
    event('image-sha512', digest)
    if digest != PIN['sha512']:
        raise Failure('image-hash')
    base.chmod(0o400)
    info = json.loads(h.run(['qemu-img', 'info', '--output=json', str(base)], 'disk-info'))
    if info['format'] != 'qcow2' or info['virtual-size'] > 4 * 1024**3 or info.get('backing-filename'):
        raise Failure('disk-format-size-backing')
    h.run(['qemu-img', 'create', '-f', 'qcow2', '-F', 'qcow2', '-b', str(base), 'overlay', '4G'], 'overlay')
    for key in ('client', 'host'):
        h.run(['ssh-keygen', '-q', '-t', 'ed25519', '-N', '', '-C', 'synthetic-fixture', '-f', key], 'keys')
    nonce = campaign.nonce if campaign is not None else secrets.token_hex(16)
    config = cloud_config(r, nonce)
    firmware = []
    if campaign is not None:
        config, firmware = campaign.configure(config)
    (r / 'user-data').write_text(config)
    (r / 'meta-data').write_text('instance-id: synthetic-fixture\nlocal-hostname: synthetic-fixture\n')
    h.run(['cloud-localds', 'seed.img', 'user-data', 'meta-data'], 'seed')
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0))
        port = s.getsockname()[1]
    # QEMU fails closed if another process wins the release/bind race.
    (r / 'known_hosts').write_text(f'[127.0.0.1]:{port} ' + (r / 'host.pub').read_text())
    event('execution-mode', 'tcg-system-qemu64-multi')
    vm, output = h.start(['qemu-system-x86_64', '-machine', 'q35',
        '-accel', 'tcg,thread=multi', '-cpu', 'qemu64', '-smp', '2', '-m', '2048', '-display', 'none',
        '-serial', 'stdio', '-monitor', 'none', '-no-reboot',
        '-drive', 'file=overlay,format=qcow2,if=virtio',
        '-drive', 'file=seed.img,format=raw,if=virtio,readonly=on',
        '-netdev', f'user,id=n,restrict=on,hostfwd=tcp:127.0.0.1:{port}-:22',
        '-device', 'virtio-net-pci,netdev=n'] + firmware)
    if campaign is not None:
        h.campaign_output = (campaign, output)
        try:
            wait_campaign(h, vm, output, campaign)
        except BaseException:
            try:
                campaign.capture(os.pread(output.fileno(), 1024**2, 0))
            except BaseException as error:
                campaign.parse_error = failure_name(error)
            raise
    wait_ready(h, vm, output, nonce)
    result = h.run(ssh_args(r, port) + [SMOKE], 'probe', 30)
    if result != EXPECTED:
        raise Failure('probe-identity')
    event('probe', 'debian13-systemd-pid1-manager-query-ok')
    if campaign is not None:
        # Deliberately substitute a different synthetic host key and require denial.
        original = (r / 'known_hosts').read_bytes()
        try:
            (r / 'known_hosts').write_text(f'[127.0.0.1]:{port} ' + (r / 'client.pub').read_text())
            try:
                h.run(ssh_args(r, port) + [SMOKE], 'wrong-host-key', 10)
            except Failure as error:
                if str(error) != 'wrong-host-key-exit':
                    raise
                if h.children[-1][0].returncode != 255:
                    raise Failure('campaign-wrong-key-denial')
            else:
                raise Failure('campaign-wrong-key-accepted')
        finally:
            (r / 'known_hosts').write_bytes(original)
        campaign.ssh = True
    # No sudo in guest: signal QEMU via ACPI QMP would add control surface.
    # Termination and mandatory reaping are the bounded disposable shutdown.
    event('shutdown', 'terminate-disposable-guest')


# Closed host contract for the separate synthetic campaign. No execution on import.
CAMPAIGN_ACK = 'RUN-SYNTHETIC-POLICY-SESSION-12-CASES'
CASE_NAMES = ('two-entry-armed', 'full-store-rejected-after-barrier', 'notify-main-rejection',
    'alive-holder-crash-handback', 'worker-agent-exit-hup', 'hup-fdpoll0-retention',
    'inactive-anchor-gc-retention', 'same-unit-dead-handback', 'no-second-worker-start',
    'partial-removal-crash', 'partial-handback-cleanup', 'repeated-empty-cleanup')
GAP_NAMES = ('worker-early-exit-no-work-proof', 'exited-readable-without-hup', 'pid-reuse',
    'worker-memory-max-oom', 'holder-oom', 'ancestor-oomd-policy-qualification',
    'agent-descendant-and-fd-exfil-closure', 'manager-reexec', 'anchor-removal-sensitivity',
    'pre-clr-tsync-and-no-old-rings', 'real-product-policy-protocol')
STAGES = ('before-handler-restoration', 'after-handler-restoration')


def strict_json(raw):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise Failure('campaign-duplicate-member')
            result[key] = value
        return result
    try:
        value = json.loads(raw, object_pairs_hook=pairs,
                           parse_constant=lambda _: (_ for _ in ()).throw(ValueError()))
        def bounded(item, depth=0):
            if depth > 16:
                raise Failure('campaign-depth')
            if isinstance(item, dict):
                if len(item) > 128:
                    raise Failure('campaign-members')
                for key, val in item.items():
                    if len(key) > 256:
                        raise Failure('campaign-key')
                    bounded(val, depth + 1)
            elif isinstance(item, list):
                if len(item) > 128:
                    raise Failure('campaign-members')
                for val in item:
                    bounded(val, depth + 1)
            elif isinstance(item, str) and len(item) > 262144:
                raise Failure('campaign-string')
        bounded(value)
        return value
    except (ValueError, TypeError, RecursionError, UnicodeError):
        raise Failure('campaign-json') from None


def same_json(value, expected):
    """Compare bound JSON facts without Python's bool/int/float coercion."""
    if type(value) is not type(expected):
        return False
    if isinstance(expected, dict):
        return (value.keys() == expected.keys()
                and all(same_json(value[key], item) for key, item in expected.items()))
    if isinstance(expected, list):
        return len(value) == len(expected) and all(same_json(a, b) for a, b in zip(value, expected))
    return value == expected


def policy_prepare():
    import importlib.util
    spec = importlib.util.spec_from_file_location('cm14_prepare', HERE.parent / 'policy-session-fixture/prepare.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def reviewed_manifest(commit, expected):
    """Deterministic exact Git bytes; expected digest is external review authority."""
    if not re.fullmatch('[0-9a-f]{40}', commit or '') or not re.fullmatch('[0-9a-f]{64}', expected or ''):
        raise Failure('campaign-review-required')
    module = policy_prepare()
    from common import SOURCE_FILES
    files = {}
    for name in sorted(SOURCE_FILES):
        result = subprocess.run(['git', '-C', str(HERE), 'show',
            commit + ':tooling/policy-session-fixture/' + name], capture_output=True, timeout=5, check=False)
        if result.returncode or len(result.stdout) > 262144:
            raise Failure('campaign-reviewed-commit')
        files[name] = hashlib.sha256(result.stdout).hexdigest()
    raw = json.dumps({'schema': 1, 'commit': commit, 'files': files},
                     sort_keys=True, separators=(',', ':')).encode()
    if hashlib.sha256(raw).hexdigest() != expected:
        raise Failure('campaign-manifest-binding')
    return raw


class Campaign:
    def __init__(self, manifest, digest, commit):
        if not re.fullmatch('[0-9a-f]{40}', commit or '') or not re.fullmatch('[0-9a-f]{64}', digest or '') or not manifest:
            raise Failure('campaign-review-required')
        path = Path(manifest)
        if path.is_symlink() or path.stat().st_size > 16384:
            raise Failure('campaign-manifest-file')
        raw = path.read_bytes()
        if hashlib.sha256(raw).hexdigest() != digest:
            raise Failure('campaign-manifest-binding')
        if strict_json(raw).get('commit') != commit:
            raise Failure('campaign-commit-binding')
        self.manifest, self.digest, self.commit = path, digest, commit
        self.nonce = secrets.token_hex(16)
        self.records = []
        self.exit_record = None
        self.parse_error = None
        self.pending_frame = False
        self.cancelled = False
        self.ssh = False
        self.cleaned = False
        self.template = None
        # Real prepare refuses uncommitted bytes before any download or VM mutation.
        self.configure('#cloud-config\n' + json.dumps({'package_update': False,
            'package_upgrade': False, 'write_files': [], 'runcmd': []}))

    def configure(self, config):
        config, firmware = policy_prepare().prepare(config, self.nonce, self.manifest, self.digest)
        data = json.loads(config.removeprefix('#cloud-config\n'))
        self.template = json.loads(next(item['content'] for item in data['write_files']
            if item['path'] == '/run/cm14-authorization-template.json'))
        return config, firmware

    def capture(self, data, final=False):
        import base64
        if len(data) > 1024**2:
            raise Failure('campaign-serial-limit')
        self.records = []
        self.exit_record = None
        lines = data.split(b'\n')
        self.pending_frame = lines[-1].startswith((b'CM14-EVIDENCE:', b'CM14-EXIT:', b'{"cm14_evidence":'))
        if final and self.pending_frame:
            self.parse_error = 'campaign-frame-truncated'
            raise Failure(self.parse_error)
        for line in lines[:-1]:
            wrapper = None
            line = line.removesuffix(b'\r')
            if line.startswith(b'{"cm14_evidence":'):
                wrapper = strict_json(line)
                if set(wrapper) != {'cm14_evidence', 'qualification', 'primary_failure', 'cleanup_failures',
                        'passed_cases', 'required_cases', 'not_run_gaps', 'command_containment',
                        'campaign_completed', 'receipt_stage'}:
                    raise Failure('campaign-receipt-members')
                if not isinstance(wrapper['cm14_evidence'], str):
                    raise Failure('campaign-export-missing')
                line = b'CM14-EVIDENCE:' + wrapper['cm14_evidence'].encode('ascii')
            prefix = next((p for p in (b'CM14-EVIDENCE:', b'CM14-EXIT:') if line.startswith(p)), None)
            if prefix is None:
                continue
            if len(line) > 262200:
                raise Failure('campaign-frame-limit')
            try:
                raw = base64.b64decode(line[len(prefix):], validate=True)
            except ValueError:
                raise Failure('campaign-frame-encoding') from None
            if len(raw) > 196608:
                raise Failure('campaign-frame-limit')
            obj = strict_json(raw)
            if not isinstance(obj, dict) or obj.get('schema') != 1 or type(obj.get('schema')) is not int:
                raise Failure('campaign-schema')
            if prefix == b'CM14-EXIT:':
                if self.exit_record is not None or set(obj) != {'schema', 'nonce', 'source_sha256', 'exit'}:
                    raise Failure('campaign-exit-members')
                if obj['nonce'] != self.nonce or obj['source_sha256'] != self.digest or type(obj['exit']) is not int:
                    raise Failure('campaign-exit-binding')
                if obj['exit'] not in (1, 2, 3) and not -64 <= obj['exit'] < 0:
                    raise Failure('campaign-exit-enum')
                self.exit_record = obj
            else:
                if self.exit_record is not None or set(obj) != {'schema', 'stage', 'report'} or len(self.records) >= 2:
                    raise Failure('campaign-stage-members')
                if obj['stage'] != STAGES[len(self.records)]:
                    raise Failure('campaign-stage-order')
                self.validate_report(obj['report'])
                if wrapper is not None:
                    report = obj['report']
                    expected = {'qualification': 'INCOMPLETE', 'primary_failure': report['primary_failure'],
                        'cleanup_failures': report['cleanup_failures'], 'passed_cases': sum(
                            c['status'] == 'PASS' for c in report['cases'].values()),
                        'required_cases': 12, 'not_run_gaps': 11, 'command_containment': report['command_containment'],
                        'campaign_completed': report['campaign_completed'], 'receipt_stage': obj['stage']}
                    if any(not same_json(wrapper[k], v) for k, v in expected.items()):
                        raise Failure('campaign-receipt-binding')
                self.records.append(obj)

    def validate_report(self, report):
        required = {'kind', 'authorization', 'cases', 'gaps', 'primary_failure', 'cleanup_failures',
                    'qualification', 'command_containment', 'campaign_completed', 'retirement_verified'}
        optional = {'journal_snapshot', 'oom_ancestry', 'manager_version', 'kernel', 'python_sha256',
                    'libsystemd_mappings', 'libsystemd_sha256'}
        if not isinstance(report, dict) or not required <= set(report) or set(report) - required - optional:
            raise Failure('campaign-report-members')
        identity = report['authorization']
        if not isinstance(identity, dict) or set(identity) != set(self.template) | {'boot_id'}:
            raise Failure('campaign-identity-members')
        if any(not same_json(identity[k], v) for k, v in self.template.items()):
            raise Failure('campaign-identity-binding')
        if not isinstance(identity['boot_id'], str) or not re.fullmatch('[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}', identity['boot_id']):
            raise Failure('campaign-boot-binding')
        if self.records and not same_json(identity, self.records[0]['report']['authorization']):
            raise Failure('campaign-boot-substitution')
        if report['kind'] != 'PROTOCOL_FIXTURE_ONLY' or report['qualification'] != 'INCOMPLETE':
            raise Failure('campaign-qualification')
        if not same_json(report['command_containment'], {'status': 'UNKNOWN', 'scope': 'initial-process-group-only',
                'required_fallback': 'outer-disposable-VM-destroy-and-reap'}):
            raise Failure('campaign-containment')
        for field in ('campaign_completed', 'retirement_verified'):
            if type(report[field]) is not bool:
                raise Failure('campaign-boolean')
        def category(value):
            return isinstance(value, str) and re.fullmatch('[a-zA-Z0-9.-]{1,100}', value)
        if report['primary_failure'] is not None and not category(report['primary_failure']):
            raise Failure('campaign-primary')
        if not isinstance(report['cleanup_failures'], list) or any(not category(v) for v in report['cleanup_failures']):
            raise Failure('campaign-secondary')
        if report['gaps'] != dict.fromkeys(GAP_NAMES, 'NOT RUN') or not isinstance(report['cases'], dict) or set(report['cases']) != set(CASE_NAMES):
            raise Failure('campaign-case-members')
        for case in report['cases'].values():
            if not isinstance(case, dict) or case.get('status') not in ('PASS', 'NOT RUN'):
                raise Failure('campaign-case-enum')
            if case['status'] == 'PASS':
                if set(case) != {'status', 'raw'} or not isinstance(case['raw'], (dict, list)) or not case['raw']:
                    raise Failure('campaign-raw-required')
            elif set(case) != {'status'}:
                raise Failure('campaign-case-members')

    def raw_cases_pass(self, cases, epoch):
        def phase(index, name, count=None):
            raw = cases[CASE_NAMES[index]]['raw']
            return (isinstance(raw, dict) and raw.get('phase') == name
                    and (count is None or isinstance(raw.get('raw_dump'), list) and len(raw['raw_dump']) == count))
        try:
            original = cases[CASE_NAMES[0]]['raw']['raw_dump']
            if not isinstance(original, list) or len(original) != 2:
                return False
            if any(not isinstance(row, list) or len(row) != 9 or not isinstance(row[0], str)
                   or not re.fullmatch('(worker|agent)-[0-9a-f]{32}', row[0])
                   or row[0].split('-', 1)[1] != epoch or row[7] != 'anon_inode:[pidfd]'
                   or any(type(row[i]) is not int or not 0 <= row[i] <= 0xFFFFFFFF
                          for i in (1, 2, 3, 5, 6, 8))
                   or type(row[4]) is not int or not 1 < row[4] <= 0xFFFFFFFFFFFFFFFF
                   or row[8] not in (2, 2 | 0o4000) for row in original):
                return False
            if {row[0].split('-')[0] for row in original} != {'worker', 'agent'}:
                return False
            if len({tuple(row[2:5]) for row in original}) != 2:
                return False
            for index in (1, 2, 3, 4, 7):
                if not same_json(cases[CASE_NAMES[index]]['raw'].get('raw_dump'), original):
                    return False
            if not phase(0, 'ARMED', 2) or not phase(1, 'FULL_REJECTED', 2) or not phase(2, 'SENDER_REJECTED', 2):
                return False
            if cases[CASE_NAMES[1]]['raw'].get('barrier') != 'processed':
                return False
            for index, dead in ((3, False), (4, True), (7, True)):
                raw = cases[CASE_NAMES[index]]['raw']
                if (not phase(index, 'RECOVERED', 2)
                        or not same_json(raw.get('hup'), {'worker': dead, 'agent': dead})
                        or raw.get('recovery') is not True):
                    return False
            rows = cases[CASE_NAMES[5]]['raw']
            anchor = cases[CASE_NAMES[6]]['raw']
            if not same_json(rows, original) or not same_json(anchor.get('raw_dump'), rows):
                return False
            holder = anchor['holder']
            if holder.get('ActiveState') != 'inactive' or holder.get('LoadState') != 'loaded':
                return False
            worker = cases[CASE_NAMES[8]]['raw']
            if worker.get('starts') != '1' or not re.fullmatch('[0-9a-f]{32}', worker.get('invocation', '')):
                return False
            partial = cases[CASE_NAMES[9]]['raw']
            if partial['partial']['phase'] != 'PARTIAL' or len(partial['partial']['raw_dump']) != 1 or partial['recovered']['phase'] != 'RECOVERED' or len(partial['recovered']['raw_dump']) != 1:
                return False
            remaining = [row for row in original if row[0].startswith('agent-')]
            if (not same_json(partial['partial']['raw_dump'], remaining)
                    or not same_json(partial['recovered']['raw_dump'], remaining)
                    or partial['recovered'].get('recovery') is not True):
                return False
            return phase(10, 'EMPTY', 0) and phase(11, 'EMPTY', 0)
        except (KeyError, TypeError, AttributeError):
            return False

    def protocol_pass(self):
        if (self.parse_error or getattr(self, 'pending_frame', False) or getattr(self, 'cancelled', False)
                or not self.ssh or not self.cleaned or len(self.records) != 2
                or self.exit_record is None or self.exit_record['exit'] != 2):
            return False
        for item in self.records:
            r = item['report']
            journal = r.get('journal_snapshot')
            if not isinstance(journal, dict) or journal.get('phase') != 'RETIRED':
                return False
            epoch = journal.get('epoch')
            if not isinstance(epoch, str) or not re.fullmatch('[0-9a-f]{32}', epoch):
                return False
            try:
                rows = r['cases'][CASE_NAMES[0]]['raw']['raw_dump']
                if set(journal['expected']) != {row[0] for row in rows} or any(
                        not same_json(journal['expected'][row[0]], row[1:7] + [row[8]]) for row in rows):
                    return False
                if r['cases'][CASE_NAMES[8]]['raw']['invocation'] != journal['worker_id']:
                    return False
            except (KeyError, TypeError, IndexError):
                return False
            if r['primary_failure'] is not None or r['cleanup_failures'] or not r['campaign_completed'] or not r['retirement_verified']:
                return False
            if any(c['status'] != 'PASS' for c in r['cases'].values()) or not self.raw_cases_pass(r['cases'], epoch):
                return False
        return True

    def persist(self, directory, primary):
        # Only validated frames, never raw serial, keys, cloud config or arbitrary files.
        summary = {'schema': 1, 'source_commit': self.commit, 'source_sha256': self.digest,
            'nonce': self.nonce, 'qualification': 'INCOMPLETE', 'gaps': dict.fromkeys(GAP_NAMES, 'NOT RUN'),
            'synthetic_protocol': 'UNRESOLVED',
            'candidate_protocol_pass': primary is None and self.protocol_pass(),
            'required_completion_receipt': 'campaign-complete',
            'strict_ssh': self.ssh, 'outer_cleanup_confirmed': self.cleaned,
            'host_failure': failure_name(primary) if primary is not None else None,
            'parser_failure': self.parse_error, 'guest_exit': self.exit_record}
        for name, obj in (('campaign-raw.json', self.records), ('campaign-summary.json', summary)):
            fd = os.open(Path(directory) / name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
            with os.fdopen(fd, 'w') as stream:
                json.dump(obj, stream, sort_keys=True)
                stream.flush()
                os.fsync(stream.fileno())


def wait_campaign(h, vm, output, campaign):
    end = min(time.monotonic() + 240, h.deadline)
    while True:
        h.check_output_limits('campaign')
        data = os.pread(output.fileno(), 1024**2, 0)
        campaign.capture(data)
        if campaign.exit_record is not None:
            return
        if vm.poll() is not None or time.monotonic() >= end:
            raise Failure('campaign-exit-missing')
        time.sleep(0.1)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--execute-owner-reviewed-fixture', action='store_true')
    parser.add_argument('--policy-session-campaign', action='store_true')
    parser.add_argument('--campaign-preflight', action='store_true')
    parser.add_argument('--campaign-acknowledge')
    parser.add_argument('--reviewed-source-commit')
    parser.add_argument('--reviewed-manifest-sha256')
    parser.add_argument('--reviewed-manifest')
    parser.add_argument('--campaign-evidence')
    parser.add_argument('--write-reviewed-manifest', action='store_true')
    args = parser.parse_args()
    if args.write_reviewed_manifest:
        if args.execute_owner_reviewed_fixture or args.policy_session_campaign or not args.reviewed_manifest:
            raise Failure('campaign-manifest-mode')
        raw = reviewed_manifest(args.reviewed_source_commit, args.reviewed_manifest_sha256)
        with open(args.reviewed_manifest, 'xb') as stream:
            stream.write(raw)
        return
    campaign = None
    if args.policy_session_campaign:
        if not args.execute_owner_reviewed_fixture or args.campaign_acknowledge != CAMPAIGN_ACK or not args.campaign_evidence:
            raise Failure('campaign-explicit-authorization-required')
        evidence = Path(args.campaign_evidence)
        if evidence.is_symlink() or not evidence.is_dir() or evidence.stat().st_uid != os.getuid() or evidence.stat().st_mode & 0o077:
            raise Failure('campaign-evidence-directory')
        campaign = Campaign(args.reviewed_manifest, args.reviewed_manifest_sha256, args.reviewed_source_commit)
    elif any((args.campaign_acknowledge, args.reviewed_source_commit, args.reviewed_manifest_sha256,
              args.reviewed_manifest, args.campaign_evidence)):
        raise Failure('campaign-mode-required')
    if not args.execute_owner_reviewed_fixture:
        raise Failure('owner-opt-in-required')
    if args.campaign_preflight:
        if campaign is None:
            raise Failure('campaign-mode-required')
        event('campaign-source', 'reviewed-commit-and-manifest-bound-not-executed')
        return
    prerequisites()
    def expired(signum, frame):
        raise Failure('job-timeout')
    signal.signal(signal.SIGALRM, expired)
    signal.alarm(1200)
    # Needed only in this harness process, never a host-wide setting.
    if ctypes.CDLL(None, use_errno=True).prctl(36, 1, 0, 0, 0) != 0:
        raise Failure('preflight-subreaper')
    os.umask(0o077)
    root = Path(tempfile.mkdtemp(prefix='cm-debian-fixture-', dir='/tmp'))
    h = Harness(root)
    primary = None
    try:
        if shutil.disk_usage(root).free < 10 * 1024**3:
            raise Failure('preflight-disk')
        if campaign is None:
            exercise(h)
        else:
            exercise(h, campaign)
        h.check_output_limits('shutdown')
    except BaseException as error:
        primary = error
        record_failure('primary', error)
    finally:
        def cancelled(signum, frame):
            # No I/O or exception in a signal handler: mandatory reaping continues.
            # Preserve any earlier cause; cancellation permanently forbids success.
            nonlocal primary
            if primary is None:
                primary = Failure('interrupted')
            if campaign is not None:
                campaign.cancelled = True
        signal.signal(signal.SIGTERM, cancelled)
        signal.signal(signal.SIGINT, cancelled)
        signal.alarm(0)
        try:
            h.cleanup()
        except BaseException as error:
            record_failure('cleanup', error)
            event('cleanup', 'workspace-retained-for-ephemeral-runner-destruction')
            if primary is None:
                primary = error
        else:
            if campaign is not None:
                campaign.cleaned = True
            # Delete private disks only after successful reaping.
            try:
                shutil.rmtree(root)
                event('cleanup', 'owned-workspace-deleted')
            except BaseException as error:
                record_failure('workspace-deletion', error)
                event('cleanup', 'workspace-retained-for-ephemeral-runner-destruction')
                if primary is None:
                    primary = error
    if campaign is not None:
        if primary is None and not campaign.protocol_pass():
            primary = Failure('campaign-incomplete')
        try:
            campaign.persist(args.campaign_evidence, primary)
        except BaseException as error:
            record_failure('artifact', error)
            if primary is None:
                primary = error
    if primary is not None:
        raise primary
    def terminal_cancelled(signum, frame):
        # Cleanup and artifact writes are complete. Cancellation can now abort
        # even the final write/return without bypassing mandatory teardown.
        cancelled(signum, frame)
        raise primary
    try:
        signal.signal(signal.SIGTERM, terminal_cancelled)
        signal.signal(signal.SIGINT, terminal_cancelled)
    except BaseException:
        if primary is not None:
            raise primary
        raise
    if primary is not None:
        raise primary
    if campaign is not None:
        # Only after artifact writes/flushes succeed. Summary alone never grants PASS.
        # A record alone cannot be an atomic success verdict: a cancellation can
        # arrive during its write. Require a successful terminal host/job exit too.
        print(json.dumps({'phase': 'campaign-complete', 'synthetic_protocol': 'CANDIDATE',
            'required_host_exit': 0,
            'scope': 'synthetic-12-case-protocol-only', 'qualification': 'INCOMPLETE',
            'nonce': campaign.nonce, 'source_commit': campaign.commit,
            'source_sha256': campaign.digest, 'outer_cleanup_confirmed': True}), flush=True)
        if primary is not None:
            raise primary
        return
    event('fixture', 'smoke-passed-not-product-acceptance')
    if primary is not None:
        raise primary


if __name__ == '__main__':
    def interrupted(signum, frame):
        raise Failure('interrupted')
    signal.signal(signal.SIGTERM, interrupted)
    signal.signal(signal.SIGINT, interrupted)
    try:
        main()
    except Failure as exc:
        event(failure_name(exc), 'failed')
        raise SystemExit(1)
    except Exception:
        event('internal', 'failed-details-suppressed')
        raise SystemExit(1)
