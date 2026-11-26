"""Collect bounded installed Linux identities; never qualifies an H1 runtime."""
import argparse
import base64
from collections import deque
from contextlib import ExitStack, contextmanager
import hashlib
import json
import os
from pathlib import Path
import platform
import selectors
import signal
import stat
import subprocess
import sys
import time

ROOTS = (Path('/usr'), Path('/lib'), Path('/lib64'), Path('/etc'), Path('/var/lib/dpkg'))
TOOLS = {'readelf': '/usr/bin/readelf', 'cc': '/usr/bin/cc',
         'ld': '/usr/bin/ld', 'dpkg-query': '/usr/bin/dpkg-query'}
SDK_ROOTS = (Path('/usr/share/dotnet'), Path('/usr/lib/dotnet'))
SDK_FILES = ('dotnet', 'sdk/10.0.404/dotnet.runtimeconfig.json',
             'sdk/10.0.404/Microsoft.NETCoreSdk.BundledVersions.props',
             'sdk/10.0.404/Roslyn/bincore/csc.dll',
             'sdk/10.0.404/Roslyn/bincore/csc.deps.json',
             'packs/Microsoft.NETCore.App.Ref/10.0.15/data/FrameworkList.xml')
UNKNOWN = [
    'No H1 host/application/CLR was executed; loaded-module graph absent.',
    'No servicing absence, fallback exclusion, runtime lifetime or sandbox proof.',
    'Installed package metadata is not signed OS image or binary provenance.',
    'SDK/archive/source reproducibility and native compiler/sysroot closure unqualified.',
    'Dynamic ICU/OpenSSL/NSS/configuration and future CLR mappings are unknown.',
    'No Agent, guard, production caller or installed qualifier authority.',
]


class Refusal(Exception):
    pass


class Interrupted(Exception):
    pass


class Budget:
    def __init__(self, seconds=90, count=128, total=384 * 1024 * 1024):
        self.until = time.monotonic() + seconds
        self.count = count
        self.total = total

    def check(self):
        if time.monotonic() >= self.until:
            raise Refusal('collection deadline')

    def charge(self, size):
        self.check()
        if self.count <= 0 or size > self.total:
            raise Refusal('aggregate read budget')
        self.count -= 1
        self.total -= size


def bounded_read(path, budget, limit=128 * 1024 * 1024):
    """No symlink leaf, special files, unbounded read or silent truncation."""
    budget.check()
    if budget.count <= 0:
        raise Refusal('aggregate read budget')
    limit = min(limit, budget.total)
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    try:
        return bounded_read_fd(fd, budget, limit)
    finally:
        os.close(fd)


def bounded_read_fd(fd, budget, limit=128 * 1024 * 1024):
    """Read a borrowed open descriptor; the caller owns its closure."""
    budget.check()
    if budget.count <= 0:
        raise Refusal('aggregate read budget')
    limit = min(limit, budget.total)
    before = os.fstat(fd)
    if not stat.S_ISREG(before.st_mode) or before.st_size > limit:
        raise Refusal('nonregular or oversized input')
    data = bytearray()
    while True:
        budget.check()
        chunk = os.read(fd, min(65536, limit + 1 - len(data)))
        if not chunk:
            break
        data.extend(chunk)
        if len(data) > limit:
            raise Refusal('input byte limit')
    after = os.fstat(fd)
    if (before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns,
            before.st_ctime_ns) != (after.st_dev, after.st_ino, after.st_size,
                                   after.st_mtime_ns, after.st_ctime_ns):
        raise Refusal('input changed during read')
    budget.charge(len(data))
    return bytes(data)


def trusted(path, roots=ROOTS):
    """Resolve known installed links; reject escapes and mutable ancestors."""
    original = Path(path)
    if not original.is_absolute() or '..' in original.parts:
        raise Refusal('absolute installed path required')
    resolved = original.resolve(strict=True)
    if not any(resolved.is_relative_to(root) for root in roots):
        raise Refusal('installed root escape')
    for base in (original, *original.parents, resolved, *resolved.parents):
        info = base.lstat()
        if info.st_uid != 0 or (not stat.S_ISLNK(info.st_mode) and info.st_mode & 0o022):
            raise Refusal('untrusted installed ownership/mode: ' + str(base) +
                          ' uid=' + str(info.st_uid) + ' mode=' + oct(stat.S_IMODE(info.st_mode)))
    return resolved


def identity(path, budget):
    resolved = trusted(path)
    data = bounded_read(resolved, budget)
    if data.startswith(b'\x7fELF'):
        expected = {'x86_64': 62, 'aarch64': 183}.get(platform.machine())
        if len(data) < 20 or data[4:6] != b'\x02\x01' or int.from_bytes(data[18:20], 'little') != expected:
            raise Refusal('installed ELF architecture mismatch')
    return {'path': str(path), 'resolved': str(resolved), 'bytes': len(data),
            'sha256': hashlib.sha256(data).hexdigest(),
            'origin': 'installed root-owned bytes; package association collected separately'}


@contextmanager
def sdk_handles():
    """Close every acquired handle once without replacing the first refusal."""
    cleanup = []
    first = None
    try:
        with ExitStack() as handles:
            def close(descriptor):
                try:
                    os.close(descriptor)
                except OSError as error:
                    cleanup.append(str(error))
            def opened(name, flags, parent=None):
                descriptor = os.open(name, flags, dir_fd=parent)
                handles.callback(close, descriptor)
                return descriptor
            try:
                yield opened
            except BaseException as error:
                first = error
                raise
    except BaseException as error:
        if first is not None and error is not first:
            cleanup.append('SDK cleanup interrupted: ' + type(error).__name__ + ': ' + str(error))
            first.sdk_cleanup_uncertainty = '; '.join(cleanup)
            raise first from error
        if cleanup or first is None:
            error.sdk_cleanup_uncertainty = '; '.join(cleanup) or ('SDK cleanup interrupted: ' + type(error).__name__)
        raise
    if cleanup:
        error = Refusal('SDK descriptor cleanup')
        error.sdk_cleanup_uncertainty = '; '.join(cleanup)
        raise error


def sdk_observation(sdk_root, relative, budget):
    """Passive fixed SDK bytes only; ownership is observed, never trusted."""
    root = Path(sdk_root)
    if root not in SDK_ROOTS or relative not in SDK_FILES:
        raise Refusal('SDK observation outside fixed allowlist')
    budget.check()
    if budget.count <= 0:
        raise Refusal('aggregate read budget')
    directory_flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_NONBLOCK
    with sdk_handles() as opened:
        # Bind the selected root from / without following a substituted ancestor.
        canonical_root = root
        seen_roots = set()
        while True:
            budget.check()
            if canonical_root in seen_roots:
                raise Refusal('SDK root symlink limit')
            seen_roots.add(canonical_root)
            anchor = opened('/', directory_flags)
            for component in canonical_root.parts[1:-1]:
                budget.check()
                anchor = opened(component, directory_flags, anchor)
            info = os.stat(canonical_root.name, dir_fd=anchor, follow_symlinks=False)
            if stat.S_ISLNK(info.st_mode):
                target = os.readlink(canonical_root.name, dir_fd=anchor)
                if len(os.fsencode(target)) > 4096:
                    raise Refusal('SDK root symlink size')
                target_path = Path(target)
                if not target_path.is_absolute():
                    target_path = canonical_root.parent / target_path
                canonical_root = Path(os.path.normpath(target_path))
                if canonical_root not in SDK_ROOTS:
                    raise Refusal('SDK root escape')
                continue
            anchor = opened(canonical_root.name, directory_flags, anchor)
            bound = os.fstat(anchor)
            if (info.st_dev, info.st_ino, info.st_mode, info.st_uid) != (
                    bound.st_dev, bound.st_ino, bound.st_mode, bound.st_uid):
                raise Refusal('SDK root changed during open')
            break
        root_info = os.fstat(anchor)
        directories = [anchor]
        names = []
        pending = deque(Path(relative).parts)
        observed = []
        requested_leaf = None
        links = 0
        steps = 0
        while pending:
            budget.check()
            steps += 1
            if steps > 128 or len(pending) > 128:
                raise Refusal('SDK traversal limit')
            component = pending.popleft()
            if component in ('', '.'):
                continue
            if component == '..':
                if not names:
                    raise Refusal('SDK member escape')
                names.pop()
                directories.pop()
                continue
            info = os.stat(component, dir_fd=directories[-1], follow_symlinks=False)
            record = {'path': str(canonical_root.joinpath(*names, component)),
                      'uid': info.st_uid, 'mode': oct(stat.S_IMODE(info.st_mode)),
                      'symlink': stat.S_ISLNK(info.st_mode)}
            observed.append(record)
            if not pending and requested_leaf is None:
                requested_leaf = record
            if stat.S_ISLNK(info.st_mode):
                links += 1
                if links > 40:
                    raise Refusal('SDK symlink limit')
                target = os.readlink(component, dir_fd=directories[-1])
                if len(os.fsencode(target)) > 4096:
                    raise Refusal('SDK symlink size')
                target_path = Path(target)
                if target_path.is_absolute():
                    if not target_path.is_relative_to(canonical_root):
                        raise Refusal('SDK member escape')
                    target_path = target_path.relative_to(canonical_root)
                    names.clear()
                    directories = [anchor]
                pending.extendleft(reversed(target_path.parts))
                continue
            flags = directory_flags if pending else os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK
            descriptor = opened(component, flags, directories[-1])
            bound = os.fstat(descriptor)
            if (info.st_dev, info.st_ino, info.st_mode, info.st_uid) != (
                    bound.st_dev, bound.st_ino, bound.st_mode, bound.st_uid):
                raise Refusal('SDK member changed during open')
            if pending:
                directories.append(descriptor)
                names.append(component)
                continue
            data = bounded_read_fd(descriptor, budget)
            observations = [requested_leaf] + [item for item in observed if item is not requested_leaf]
            return {'path': str(root / relative), 'resolved': str(canonical_root.joinpath(*names, component)),
                    'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest(),
                    'trust': 'UNQUALIFIED_SDK_OBSERVATION', 'execution_authorized': False,
                    'ownership_observed_before_read': observations,
                    'bound_root': {'device': root_info.st_dev, 'inode': root_info.st_ino},
                    'bound_file': {'device': bound.st_dev, 'inode': bound.st_ino},
                    'resolution_semantics': 'Names resolved against held directory handles; later renames do not redirect reads.',
                    'origin': 'Passive bytes only; mutable ownership and setup origin do not qualify SDK/compiler/runtime.'}
        raise Refusal('SDK member is not a file')


def command(argv, budget, output_limit=2 * 1024 * 1024):
    """Fixed caller argv; no shell, inherited environment or library execution."""
    budget.check()
    process = subprocess.Popen(argv, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                               env={'PATH': '/usr/bin:/bin', 'LC_ALL': 'C'},
                               cwd='/', start_new_session=True)
    data = bytearray()
    selector = selectors.DefaultSelector()
    selector.register(process.stdout, selectors.EVENT_READ)
    first = None
    cleanup = None
    result = None
    try:
        until = min(budget.until, time.monotonic() + 10)
        while selector.get_map():
            if time.monotonic() >= until:
                raise Refusal('command deadline')
            for key, _ in selector.select(min(0.1, max(0, until - time.monotonic()))):
                chunk = os.read(key.fileobj.fileno(), 65536)
                if not chunk:
                    selector.unregister(key.fileobj)
                else:
                    data.extend(chunk)
                    if len(data) > output_limit:
                        raise Refusal('command output limit')
        result = process.wait(timeout=max(0.001, until - time.monotonic()))
        if result != 0:
            raise Refusal('command exit ' + str(result))
        budget.charge(len(data))
    except BaseException as error:
        first = error
    finally:
        selector.close()
        process.stdout.close()
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        except OSError as error:
            cleanup = str(error)
        try:
            process.wait(timeout=2)
        except subprocess.TimeoutExpired:
            cleanup = 'child termination unconfirmed'
    raw_output = bytes(data[:output_limit])
    try:
        output = raw_output.decode('utf-8', 'strict')
        decoding_failure = None
    except UnicodeDecodeError as error:
        output = raw_output.decode('utf-8', 'replace')
        decoding_failure = str(error)
        if first is None:
            first = Refusal('command output is not UTF-8')
    evidence = {'argv': argv, 'exit': process.returncode, 'output': output,
                'output_bytes_base64': base64.b64encode(raw_output).decode('ascii'),
                'output_truncated': len(data) > output_limit,
                'decoding_failure': decoding_failure,
                'first_failure': str(first) if first is not None else None,
                'cleanup_uncertainty': cleanup}
    if first is not None or cleanup is not None:
        error = Refusal((str(first) or type(first).__name__) if first is not None else 'command cleanup')
        error.command_evidence = evidence
        raise error from first
    return evidence


def preflight(architecture):
    if platform.system() != 'Linux':
        return 77, 'NOTRUN: Linux only'
    if os.geteuid() == 0:
        return 1, 'REFUSED: unprivileged collector required'
    if platform.machine() != architecture:
        return 1, 'REFUSED: native architecture mismatch'
    return 0, None


def collect(report, architecture, sdk_root):
    budget = Budget()
    report['kernel'] = {'system': platform.system(), 'release': platform.release(),
                        'machine': platform.machine(), 'version': platform.version()}
    os_release = bounded_read(trusted('/etc/os-release'), budget, 65536).decode('utf-8', 'strict')
    report['os'] = {}
    for line in os_release.splitlines():
        key, separator, value = line.partition('=')
        if separator and key in ('ID', 'VERSION_ID', 'PRETTY_NAME'):
            if key in report['os']:
                raise Refusal('ambiguous OS identity')
            report['os'][key] = value
    if not {'ID', 'VERSION_ID'} <= report['os'].keys():
        raise Refusal('missing OS identity')
    for path in ('/etc/os-release', '/etc/ld.so.cache', '/etc/ld.so.conf',
                 '/etc/nsswitch.conf', '/var/lib/dpkg/status'):
        report['files'].append(identity(path, budget))
    # A bounded directory, not recursive search. Config fragments are static identities only.
    fragments = trusted('/etc/ld.so.conf.d')
    names = sorted(fragments.iterdir())
    if len(names) > 32:
        raise Refusal('loader configuration count')
    for path in names:
        if path.suffix != '.conf':
            raise Refusal('unexpected loader configuration member')
        report['files'].append(identity(path, budget))
    for name, path in TOOLS.items():
        report['files'].append(identity(path, budget))
        report['commands'].append(command([str(trusted(path)), '--version'], budget))
    inventory = command([str(trusted(TOOLS['dpkg-query'])), '-W',
                         '-f=${binary:Package}\t${Version}\t${Architecture}\n'], budget)
    report['commands'].append(inventory)
    if len(inventory['output'].splitlines()) > 4096:
        raise Refusal('package count')
    triplet = {'x86_64': 'x86_64-linux-gnu', 'aarch64': 'aarch64-linux-gnu'}[architecture]
    libraries = ['libc.so.6', 'libm.so.6', 'libdl.so.2', 'libpthread.so.0',
                 'librt.so.1', 'libstdc++.so.6', 'libgcc_s.so.1']
    loader = '/lib64/ld-linux-x86-64.so.2' if architecture == 'x86_64' else '/lib/ld-linux-aarch64.so.1'
    for path in [loader] + ['/usr/lib/' + triplet + '/' + name for name in libraries]:
        report['files'].append(identity(path, budget))
        report['commands'].append(command([str(trusted(TOOLS['readelf'])), '-h', '-d', '-V',
                                            str(trusted(path))], budget))
    if sdk_root not in ('/usr/share/dotnet', '/usr/lib/dotnet'):
        raise Refusal('SDK root outside fixed allowlist')
    package_paths = [str(trusted(path)) for path in TOOLS.values()]
    package_paths += [str(trusted(path)) for path in [loader] + ['/usr/lib/' + triplet + '/' + name for name in libraries]]
    for relative in SDK_FILES:
        report['sdk_observations'].append(sdk_observation(sdk_root, relative, budget))
    report['commands'].append(command([str(trusted(TOOLS['dpkg-query'])), '-S', *package_paths], budget))
    report['origin_limits'] = 'dpkg associations cover compiler/linker/readelf/dpkg and selected loader/libraries only; configuration/cache and SDK authentication unresolved'
    report['status'] = 'COLLECTED_UNQUALIFIED_PROFILE'


def output_namespace(path):
    path = Path(os.path.abspath(path))
    for ancestor in path.parents:
        if ancestor.is_symlink() or not ancestor.is_dir():
            raise Refusal('output parent must exist without symlinks')
        info = ancestor.stat()
        if info.st_uid not in (0, os.geteuid()) or (info.st_mode & 0o022 and not info.st_mode & stat.S_ISVTX):
            raise Refusal('untrusted output parent')
    path.mkdir(mode=0o700, exist_ok=False)
    return path


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True)
    parser.add_argument('--architecture', required=True, choices=('x86_64', 'aarch64'))
    parser.add_argument('--sdk-root', required=True, choices=('/usr/share/dotnet', '/usr/lib/dotnet'))
    args = parser.parse_args(argv)
    report = {'schema': 1, 'status': 'REFUSED', 'runtime_pin': None, 'runtime_readiness': 'REFUSED',
              'first_failure': None, 'publication_uncertainty': None, 'cleanup_uncertainty': None,
              'unknowns': UNKNOWN, 'files': [], 'sdk_observations': [], 'commands': [], 'collector_maps_used': False}
    def interrupted(number, frame):
        raise Interrupted('signal ' + str(number))
    previous = signal.signal(signal.SIGTERM, interrupted)
    destination = None
    code = 1
    try:
        code, failure = preflight(args.architecture)
        report['first_failure'] = failure
        if code == 0:
            if args.sdk_root not in ('/usr/share/dotnet', '/usr/lib/dotnet'):
                raise Refusal('SDK root')
            # Reserve evidence before reading installed inputs; never overwrite.
            destination = output_namespace(args.output)
            collect(report, args.architecture, args.sdk_root)
        else:
            report['status'] = 'NOTRUN' if code == 77 else 'REFUSED'
    except (OSError, ValueError, Refusal, Interrupted, KeyboardInterrupt) as error:
        code = 130 if isinstance(error, (Interrupted, KeyboardInterrupt)) else 1
        report['status'] = 'REFUSED'
        if report['first_failure'] is None:
            report['first_failure'] = type(error).__name__ + ': ' + str(error)
        if hasattr(error, 'command_evidence'):
            report['commands'].append(error.command_evidence)
            report['cleanup_uncertainty'] = error.command_evidence['cleanup_uncertainty']
        if hasattr(error, 'sdk_cleanup_uncertainty'):
            report['cleanup_uncertainty'] = error.sdk_cleanup_uncertainty
    finally:
        try:
            if destination is None:
                destination = output_namespace(args.output)
            with (destination / 'profile.json').open('x', encoding='utf-8') as stream:
                json.dump(report, stream, indent=2)
                stream.write('\n')
                stream.flush()
                os.fsync(stream.fileno())
        except (OSError, Refusal) as error:
            report['publication_uncertainty'] = str(error)
            report['status'] = 'REFUSED'
            if code == 0:
                code = 1
        signal.signal(signal.SIGTERM, previous)
    print(json.dumps({'status': report['status'], 'exit': code,
                      'first_failure': report['first_failure'],
                      'publication_uncertainty': report['publication_uncertainty']}))
    return code


if __name__ == '__main__':
    sys.exit(main())
