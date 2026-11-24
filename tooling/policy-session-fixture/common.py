"""Closed disposable-guest fixture boundary. No caller-defined commands."""
import ctypes
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import resource
import signal
import stat
import subprocess
import tempfile
import time

ROOT = Path('/opt/cm14-fixture')
STATE = Path('/run/cm14-fixture')
UNITS = ('cm14-holder.service', 'cm14-worker.service', 'cm14-agent.service',
         'cm14-reference.target')
IMAGE = 'd25999de0770d4cde1be72c299ba3ad94eac7ea8fd4d18f8cc86c69df69337e2e8a91d64465a9bfd8f2a78c566fcbcb0842eacc91c09efdef72d719d9f13b535'
LIMIT = 65536
DEADLINE = time.monotonic() + 150


class Failure(Exception):
    def __init__(self, category, secondary=()):
        super().__init__(category)
        self.secondary = list(secondary)[:16]


def category(error):
    if isinstance(error, Failure):
        return str(error)
    return 'cancelled' if isinstance(error, (KeyboardInterrupt, SystemExit)) else 'runtime-unavailable'


def publish_failure(name, value, expected_state=None):
    """Best effort only; the caller retains the first cause, even on cancellation."""
    try:
        write(name, value, expected_state=expected_state)
        return []
    except BaseException as error:
        return ['publication-' + category(error)]


class OwnedState:
    """Exclusive acquisition, pinned directory FD; never adopt occupied state."""
    def __init__(self, path):
        self.path = Path(path)
        self.fd = None

    def acquire(self):
        parent = os.open(self.path.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            os.mkdir(self.path.name, 0o700, dir_fd=parent)
            self.fd = os.open(self.path.name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW,
                              dir_fd=parent)
            st = os.fstat(self.fd)
            require(st.st_uid == os.geteuid() and st.st_mode & 0o777 == 0o700, 'state-owner')
            self.identity = (st.st_dev, st.st_ino)
        finally:
            os.close(parent)

    def publish(self, report):
        require(self.fd is not None, 'state-not-owned')
        st = self.path.lstat()
        require(stat.S_ISDIR(st.st_mode) and (st.st_dev, st.st_ino) == self.identity,
                'state-replaced')
        atomic_write(self.fd, 'campaign.json', json.dumps(report, sort_keys=True).encode(), 262144)

    def close(self):
        fd, self.fd = self.fd, None
        if fd is not None:
            os.close(fd)



def require(ok, category):
    if not ok:
        raise Failure(category)


def trusted(path, directory=False):
    path = Path(path)
    for item in (path, *path.parents):
        st = item.lstat()
        require(not item.is_symlink() and st.st_uid == 0 and not st.st_mode & 0o022,
                'protected-ancestry')
    require(path.is_dir() if directory else path.is_file(), 'protected-type')
    return path


def digest_tree(root):
    entries = []
    for path in sorted(root.rglob('*')):
        require(not path.is_symlink(), 'source-symlink')
        if path.is_file():
            require('__pycache__' not in path.parts, 'source-cache')
            entries.append([str(path.relative_to(root)), hashlib.sha256(path.read_bytes()).hexdigest()])
    return hashlib.sha256(json.dumps(entries, separators=(',', ':')).encode()).hexdigest()


# Reviewed membership is code-defined; authority is an external exact-commit manifest.
SOURCE_FILES = frozenset(('baseline.json', 'common.py', 'entry.py', 'guest.py', 'prepare.py',
    'process.py', 'provision.py', 'test_contracts.py', 'test_failures.py',
    'parent-integration.diff', 'units/cm14-agent.service', 'units/cm14-holder.service',
    'units/cm14-reference.target', 'units/cm14-worker.service'))


def bound_payload(root, raw, expected):
    require(hashlib.sha256(raw).hexdigest() == expected, 'manifest-binding')
    manifest = json.loads(raw)
    require(set(manifest) == {'schema', 'commit', 'files'} and manifest['schema'] == 1
            and re.fullmatch('[0-9a-f]{40}', manifest['commit']) is not None, 'manifest-fields')
    require(set(manifest['files']) == SOURCE_FILES, 'manifest-membership')
    actual = set()
    for path in root.rglob('*'):
        st = path.lstat()
        require(stat.S_ISREG(st.st_mode) or stat.S_ISDIR(st.st_mode), 'source-special')
        if stat.S_ISREG(st.st_mode):
            actual.add(path.relative_to(root).as_posix())
        else:
            require(path.relative_to(root).as_posix() == 'units', 'source-directory')
    require(actual == SOURCE_FILES, 'source-membership')
    payload = {}
    for name in sorted(SOURCE_FILES):
        fd = os.open(root / name, os.O_RDONLY | os.O_NOFOLLOW)
        with os.fdopen(fd, 'rb') as stream:
            require(stat.S_ISREG(os.fstat(stream.fileno()).st_mode), 'source-type')
            data = stream.read(262145)
        require(len(data) <= 262144 and hashlib.sha256(data).hexdigest() == manifest['files'][name],
                'source-bytes')
        payload[name] = data
    return manifest, payload


def guest_source(source):
    trusted(ROOT, True)
    raw = trusted('/run/cm14-source-manifest.json').read_bytes()
    require(len(raw) <= 16384, 'manifest-size')
    manifest, _ = bound_payload(ROOT, raw, source)
    return manifest


def validate_identity(identity, nonce, source, boot, packages):
    require(set(identity) == {'schema', 'nonce', 'source_sha256', 'boot_id', 'image_sha512',
                              'packages', 'kernel_release'}, 'identity-fields')
    require(identity['schema'] == 1 and re.fullmatch('[0-9a-f]{32}', nonce) is not None,
            'guest-nonce')
    require(identity['nonce'] == nonce and identity['source_sha256'] == source
            and identity['boot_id'] == boot and identity['image_sha512'] == IMAGE, 'guest-binding')
    require(set(identity['packages']) == {'systemd', 'libsystemd0', 'python3', 'python3.13', packages['kernel_package']},
            'package-membership')
    require(all(isinstance(v, str) and re.fullmatch(r'[0-9][A-Za-z0-9.+:~_-]{1,100}', v)
                for v in identity['packages'].values()), 'package-pins')
    require(identity['packages'] == packages['versions']
            and identity['kernel_release'] == packages['release'], 'package-baseline')


def guard():
    require(platform.system() == 'Linux' and os.geteuid() == 0, 'disposable-root-required')
    require(Path('/proc/1/comm').read_text().strip() == 'systemd', 'systemd-pid1-required')
    require(Path('/sys/fs/cgroup/cgroup.controllers').is_file(), 'unified-cgroup-required')
    require(Path('/sys/class/dmi/id/sys_vendor').read_text().strip() == 'QEMU', 'qemu-required')
    fw = Path('/sys/firmware/qemu_fw_cfg/by_name/opt/cm14')
    require((fw / 'accelerator/raw').read_text().strip() == 'tcg', 'tcg-required')
    nonce = (fw / 'nonce/raw').read_text().strip()
    source = (fw / 'source-sha256/raw').read_text().strip()
    raw = trusted('/run/cm14-authorization.json').read_bytes()
    require(len(raw) < 8192, 'authorization-size')
    identity = json.loads(raw)
    template = {key: value for key, value in identity.items() if key != 'boot_id'}
    require(hashlib.sha256(json.dumps(template, sort_keys=True, separators=(',', ':')).encode()).hexdigest() ==
            (fw / 'authorization-sha256/raw').read_text().strip(), 'authorization-hash')
    baseline = json.loads(trusted(ROOT / 'baseline.json').read_text())['inventory']
    fixed = {name: baseline[name] for name in baseline if name in ('systemd', 'libsystemd0', 'python3', 'python3.13')
             or name.startswith('linux-image-6.12.')}
    require(identity.get('packages') == fixed, 'fixed-image-packages')
    require(identity.get('kernel_release') == next(name.removeprefix('linux-image-') for name in fixed
                                                 if name.startswith('linux-image-')), 'fixed-kernel-release')
    versions = {}
    for name in identity.get('packages', {}):
        require(re.fullmatch(r'(systemd|libsystemd0|python3|python3\.13|linux-image-6\.12\.[0-9]+[^/ ]*)', name)
                is not None, 'package-name')
        versions[name] = run_raw(('/usr/bin/dpkg-query', '-W', '-f=${Version}', name)).strip()
    kernel = [n for n in versions if n.startswith('linux-image-')]
    require(len(kernel) == 1, 'kernel-package')
    validate_identity(identity, nonce, source, Path('/proc/sys/kernel/random/boot_id').read_text().strip(),
                      {'versions': versions, 'release': platform.release(), 'kernel_package': kernel[0]})
    trusted(ROOT, True)
    guest_source(source)
    return identity


def child_limits():
    resource.setrlimit(resource.RLIMIT_NOFILE, (64, 64))
    resource.setrlimit(resource.RLIMIT_FSIZE, (LIMIT, LIMIT))
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))


CONTAINMENT = {'status': 'UNKNOWN', 'scope': 'initial-process-group-only',
               'required_fallback': 'outer-disposable-VM-destroy-and-reap'}


def run_raw(argv, accepted_codes=(0,)):
    allowed = {command(action, unit) for action in ('start', 'stop', 'reset-failed', 'show', 'kill')
               for unit in UNITS}
    allowed.update({('/usr/bin/systemctl', 'daemon-reload'), ('/usr/bin/systemctl', '--version'),
        ('/usr/bin/busctl', '--system', '--json=short', 'call', 'org.freedesktop.systemd1',
         '/org/freedesktop/systemd1/unit/cm14_2dholder_2eservice',
         'org.freedesktop.systemd1.Service', 'DumpFileDescriptorStore')})
    packages = ('systemd', 'libsystemd0', 'python3', 'python3.13',
                'linux-image-6.12.115+deb13.1-cloud-amd64')
    allowed.update(('/usr/bin/dpkg-query', '-W', '-f=${Version}', name) for name in packages)
    require(tuple(argv) in allowed, 'fixed-command-required')
    require(accepted_codes == ((0, 1, 4) if len(argv) > 1 and argv[1] == 'show' else (0,)),
            'fixed-exit-codes')
    return _run_process(argv, accepted_codes)


def _run_process(argv, accepted_codes=(0,)):
    """Internal mechanism tested offline; NOT a generic descendant containment domain."""
    require(time.monotonic() < DEADLINE, 'total-budget')
    # File output prevents PIPE deadlock/unbounded allocation; kill group even after leader exit.
    with tempfile.TemporaryFile() as output:
        p = subprocess.Popen(argv, stdin=subprocess.DEVNULL, stdout=output, stderr=output,
                             env={'PATH': '/usr/bin:/usr/sbin', 'LC_ALL': 'C',
                                  'SYSTEMD_PAGER': 'cat'}, start_new_session=True,
                             preexec_fn=child_limits, close_fds=True)
        end = min(DEADLINE, time.monotonic() + 5)
        primary = None
        secondary = []
        data = b''
        try:
            while p.poll() is None:
                require(time.monotonic() < end, 'command-timeout')
                require(os.fstat(output.fileno()).st_size < LIMIT, 'command-output')
                time.sleep(0.02)
            require(p.returncode in accepted_codes, 'command-exit')
            output.seek(0)
            data = output.read(LIMIT + 1)
            require(len(data) < LIMIT, 'command-output')
            data = data.decode('utf-8', errors='strict')
        except BaseException as error:
            primary = error
        finally:
            try:
                os.killpg(p.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            except BaseException:
                secondary.append('process-group-kill-unknown')
            try:
                p.wait(timeout=1)
            except BaseException:
                secondary.append('leader-reap-unknown')
        if primary is not None or secondary:
            raise Failure(category(primary) if primary is not None else 'command-cleanup', secondary)
        return data


def command(action, unit):
    require(unit in UNITS, 'unit-membership')
    require(action in ('start', 'stop', 'reset-failed', 'show', 'kill'), 'action-membership')
    if action == 'kill':
        return ('/usr/bin/systemctl', 'kill', '--kill-whom=all', '--signal=KILL', unit)
    return ('/usr/bin/systemctl', action, unit)


def ctl(action, unit):
    return run_raw(command(action, unit), (0, 1, 4) if action == 'show' else (0,))


def properties(unit):
    return dict(line.split('=', 1) for line in ctl('show', unit).splitlines() if '=' in line)


def dump():
    path = '/org/freedesktop/systemd1/unit/cm14_2dholder_2eservice'
    raw = run_raw(('/usr/bin/busctl', '--system', '--json=short', 'call',
                   'org.freedesktop.systemd1', path, 'org.freedesktop.systemd1.Service',
                   'DumpFileDescriptorStore'))
    data = json.loads(raw)
    require(data['type'] == 'a(suuutuusu)', 'dump-signature')
    rows = data['data'][0]
    require(isinstance(rows, list) and len(rows) <= 2, 'dump-shape')
    require(int(properties(UNITS[0])['NFileDescriptorStore']) == len(rows), 'dump-count')
    return rows


def fd_identity(fd):
    st = os.fstat(fd)
    require(os.readlink('/proc/self/fd/' + str(fd)) == 'anon_inode:[pidfd]', 'pidfd-type')
    # pidfs has distinct original identities; do not accept legacy anon_inode constant inode.
    require(st.st_ino > 1, 'pidfs-inode')
    import fcntl
    return [st.st_mode, os.major(st.st_dev), os.minor(st.st_dev), st.st_ino,
            os.major(st.st_rdev), os.minor(st.st_rdev), fcntl.fcntl(fd, fcntl.F_GETFL) & ~0o100000]


def confirm(rows, expected):
    require(len(rows) == len(expected) and len({r[0] for r in rows}) == len(rows), 'store-membership')
    for row in rows:
        require(len(row) == 9 and row[0] in expected, 'store-name')
        require(row[1:7] + [row[8]] == expected[row[0]], 'store-identity')


class Notify:
    def __init__(self):
        self.lib = ctypes.CDLL('libsystemd.so.0', use_errno=True)
        self.lib.sd_pid_notify_with_fds.argtypes = [ctypes.c_int, ctypes.c_int, ctypes.c_char_p,
                                                   ctypes.POINTER(ctypes.c_int), ctypes.c_uint]
        self.lib.sd_notify_barrier.argtypes = [ctypes.c_int, ctypes.c_uint64]
        self.lib.sd_listen_fds_with_names.argtypes = [ctypes.c_int,
                                                     ctypes.POINTER(ctypes.POINTER(ctypes.c_char_p))]

    def send(self, message, fd=None):
        array = (ctypes.c_int * 1)(fd) if fd is not None else None
        result = self.lib.sd_pid_notify_with_fds(0, 0, message.encode(), array, int(fd is not None))
        require(result > 0, 'notify-send')
        return result

    def barrier(self):
        require(self.lib.sd_notify_barrier(0, 2000000) > 0, 'notify-barrier')

    def inherited(self):
        names = ctypes.POINTER(ctypes.c_char_p)()
        count = self.lib.sd_listen_fds_with_names(1, ctypes.byref(names))
        require(0 <= count <= 2, 'handback-count')
        result = {names[i].decode(): 3 + i for i in range(count)}
        require(len(result) == count, 'handback-duplicate')
        # libsystemd allocates the names vector and each string. Process exits within lease;
        # at most two names per holder; no unbounded repeated allocation.
        return result


def atomic_write(directory, name, data, limit=16384):
    require(len(data) <= limit, 'state-size')
    temp = name + '.new'
    fd = os.open(temp, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600,
                 dir_fd=directory)
    primary = None
    secondary = []
    try:
        offset = 0
        while offset < len(data):
            count = os.write(fd, data[offset:])
            require(isinstance(count, int) and 0 < count <= len(data) - offset, 'write-progress')
            offset += count
        os.fsync(fd)
    except BaseException as error:
        primary = error
    # close once: an error may mean the descriptor has already been released.
    try:
        os.close(fd)
    except BaseException as error:
        if primary is None:
            primary = error
        else:
            secondary.append('temp-close-unknown')
    if primary is not None:
        raise Failure(category(primary), secondary)
    try:
        os.replace(temp, name, src_dir_fd=directory, dst_dir_fd=directory)
    except BaseException as error:
        raise Failure('publish-rename-unknown', [category(error)])
    try:
        os.fsync(directory)
    except BaseException as error:
        # New record may be visible: preserve it and journal.new, never roll back blindly.
        raise Failure('publish-durability-unknown', [category(error)])


def write(name, value, expected_state=None):
    require(name in ('journal', 'result', 'control'), 'state-name')
    directory = os.open(STATE, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    primary = None
    try:
        if expected_state is not None:
            st = os.fstat(directory)
            require((st.st_dev, st.st_ino) == expected_state, 'state-replaced')
        atomic_write(directory, name, json.dumps(value, sort_keys=True).encode())
    except BaseException as error:
        primary = error
    try:
        os.close(directory)
    except BaseException:
        if primary is None:
            primary = Failure('directory-close-unknown')
        elif isinstance(primary, Failure):
            primary.secondary.append('directory-close-unknown')
    if primary is not None:
        raise primary


def read(name):
    data = trusted(STATE / name).read_bytes()
    require(len(data) <= 16384, 'state-size')
    return json.loads(data)
