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
import stat
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


def child_env(home):
    return {'PATH': '/usr/bin:/bin', 'HOME': str(home), 'LANG': 'C', 'LC_ALL': 'C'}


def limits():
    os.umask(0o077)
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
    resource.setrlimit(resource.RLIMIT_FSIZE, (4 * 1024**3, 4 * 1024**3))
    resource.setrlimit(resource.RLIMIT_CPU, (900, 900))


def prerequisites():
    if platform.system() != 'Linux' or platform.machine() != 'x86_64':
        raise Failure('preflight-platform')
    if os.geteuid() == 0:
        raise Failure('preflight-root')
    if any(shutil.which(t, path='/usr/bin:/bin') is None for t in TOOLS):
        raise Failure('preflight-tools')
    try:
        if not stat.S_ISCHR(os.stat('/dev/kvm').st_mode):
            raise OSError()
        fd = os.open('/dev/kvm', os.O_RDWR | os.O_CLOEXEC)
        os.close(fd)
    except OSError:
        raise Failure('preflight-kvm') from None


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
        self.deadline = time.monotonic() + 1200

    def start(self, args):
        out = tempfile.TemporaryFile(dir=self.root)
        p = subprocess.Popen(args, cwd=self.root, env=child_env(self.root),
                             stdin=subprocess.DEVNULL, stdout=out, stderr=out,
                             start_new_session=True, preexec_fn=limits)
        self.children.append((p, out))
        return p, out

    def wait(self, p, out, phase, seconds):
        end = min(time.monotonic() + seconds, self.deadline)
        beat = 0
        while p.poll() is None:
            now = time.monotonic()
            if now >= end or os.fstat(out.fileno()).st_size > 1024**2:
                raise Failure(phase + '-timeout-or-output-limit')
            if now >= beat:
                event(phase, 'waiting')
                beat = now + 10
            time.sleep(0.1)
        if p.returncode:
            raise Failure(phase + '-exit')
        if os.fstat(out.fileno()).st_size > 1024**2:
            raise Failure(phase + '-output-limit')
        out.seek(0)
        return out.read(1024**2)

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
        for p, out in self.children:
            p.wait(timeout=5)
            out.close()
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
        event('cleanup', 'reaped')

    def download(self, name, cap, seconds):
        self.run(['curl', '--fail', '--silent', '--show-error', '--proto', '=https',
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


def cloud_config(root):
    # JSON is valid YAML; no manual interpolation of secret key material.
    return '#cloud-config\n' + json.dumps({
        'users': [{'name': 'fixture', 'lock_passwd': True, 'shell': '/bin/sh',
                   'sudo': False, 'ssh_authorized_keys': [(root / 'client.pub').read_text().strip()]}],
        'disable_root': True, 'ssh_pwauth': False, 'ssh_deletekeys': True,
        'ssh_genkeytypes': [], 'ssh': {'emit_keys_to_console': False},
        'ssh_keys': {'ed25519_private': (root / 'host').read_text(),
                     'ed25519_public': (root / 'host.pub').read_text().strip()},
        'write_files': [{'path': '/etc/ssh/sshd_config.d/00-fixture.conf',
                         'content': 'PasswordAuthentication no\nKbdInteractiveAuthentication no\nPermitRootLogin no\nAllowUsers fixture\nAllowTcpForwarding no\nX11Forwarding no\nAllowAgentForwarding no\n'}],
        'package_update': False, 'package_upgrade': False})


def exercise(h):
    r = h.root
    h.download('SHA512SUMS', 65536, 30)
    meta = PIN['filename'].replace('.qcow2', '.json')
    h.download(meta, 131072, 30)
    validate_provenance((r / 'SHA512SUMS').read_bytes(), (r / meta).read_bytes())
    h.download(PIN['filename'], 1024**3, 300)
    base = r / PIN['filename']
    with base.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha512').hexdigest()
    if digest != PIN['sha512']:
        raise Failure('image-hash')
    base.chmod(0o400)
    info = json.loads(h.run(['qemu-img', 'info', '--output=json', str(base)], 'disk-info'))
    if info['format'] != 'qcow2' or info['virtual-size'] > 4 * 1024**3 or info.get('backing-filename'):
        raise Failure('disk-format-size-backing')
    h.run(['qemu-img', 'create', '-f', 'qcow2', '-F', 'qcow2', '-b', str(base), 'overlay', '4G'], 'overlay')
    for key in ('client', 'host'):
        h.run(['ssh-keygen', '-q', '-t', 'ed25519', '-N', '', '-C', 'synthetic-fixture', '-f', key], 'keys')
    (r / 'user-data').write_text(cloud_config(r))
    (r / 'meta-data').write_text('instance-id: synthetic-fixture\nlocal-hostname: synthetic-fixture\n')
    h.run(['cloud-localds', 'seed.img', 'user-data', 'meta-data'], 'seed')
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0))
        port = s.getsockname()[1]
    # QEMU fails closed if another process wins the release/bind race.
    (r / 'known_hosts').write_text(f'[127.0.0.1]:{port} ' + (r / 'host.pub').read_text())
    vm, output = h.start(['qemu-system-x86_64', '-machine', 'q35,accel=kvm',
        '-cpu', 'host', '-smp', '2', '-m', '2048', '-display', 'none',
        '-serial', 'none', '-monitor', 'none', '-no-reboot',
        '-drive', 'file=overlay,format=qcow2,if=virtio',
        '-drive', 'file=seed.img,format=raw,if=virtio,readonly=on',
        '-netdev', f'user,id=n,restrict=on,hostfwd=tcp:127.0.0.1:{port}-:22',
        '-device', 'virtio-net-pci,netdev=n'])
    event('boot', 'start')
    end = min(time.monotonic() + 240, h.deadline)
    # Readiness polling is bounded, not a VM/SSH operation retry. SSH runs once.
    while True:
        if vm.poll() is not None:
            raise Failure('boot-qemu-exit')
        if time.monotonic() >= end:
            raise Failure('boot-timeout')
        try:
            with socket.create_connection(('127.0.0.1', port), timeout=1) as s:
                s.settimeout(1)
                if s.recv(128).startswith(b'SSH-2.0-'):
                    break
        except OSError:
            pass
        event('boot', 'waiting')
        time.sleep(5)
    result = h.run(ssh_args(r, port) + [SMOKE], 'probe', 30)
    if result != EXPECTED:
        raise Failure('probe-identity')
    event('probe', 'debian13-systemd-pid1-manager-query-ok')
    # No sudo in guest: signal QEMU via ACPI QMP would add control surface.
    # Termination and mandatory reaping are the bounded disposable shutdown.
    event('shutdown', 'terminate-disposable-guest')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--execute-owner-reviewed-fixture', action='store_true')
    args = parser.parse_args()
    if not args.execute_owner_reviewed_fixture:
        raise Failure('owner-opt-in-required')
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
    try:
        if shutil.disk_usage(root).free < 10 * 1024**3:
            raise Failure('preflight-disk')
        exercise(h)
    finally:
        signal.alarm(0)
        signal.signal(signal.SIGTERM, signal.SIG_IGN)
        signal.signal(signal.SIGINT, signal.SIG_IGN)
        h.cleanup()
        shutil.rmtree(root)  # If reaping fails, retain private disks for runner teardown.
        event('cleanup', 'owned-workspace-deleted')
    event('fixture', 'smoke-passed-not-product-acceptance')


if __name__ == '__main__':
    def interrupted(signum, frame):
        raise Failure('interrupted')
    signal.signal(signal.SIGTERM, interrupted)
    signal.signal(signal.SIGINT, interrupted)
    try:
        main()
    except Failure as exc:
        event(str(exc), 'failed')
        raise SystemExit(1)
    except Exception:
        event('internal', 'failed-details-suppressed')
        raise SystemExit(1)
