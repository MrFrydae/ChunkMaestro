"""Real Darwin filesystem invariants against one isolated compiled C helper."""
import ctypes
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile


def exercise(library):
    native = ctypes.CDLL(str(library), use_errno=True)
    native.cm_scope.argtypes = [ctypes.c_char_p, ctypes.c_int]
    native.cm_open.argtypes = [ctypes.c_char_p]
    native.cm_matches.argtypes = [ctypes.c_int, ctypes.c_char_p]
    results = []

    def check(name, actual, expected):
        results.append({'name': name, 'passed': actual == expected,
                        'actual': actual, 'expected': expected})

    with tempfile.TemporaryDirectory(prefix='cm-native-') as directory:
        root = Path(directory)
        os.chmod(root, 0o700)
        file = root / 'database'
        file.write_bytes(b'authority fixture')
        os.chmod(file, 0o600)
        scope = lambda path, ancestor=0: native.cm_scope(os.fsencode(path), ancestor)
        matches = lambda fd, path: native.cm_matches(fd, os.fsencode(path))
        check('private-directory', scope(root), 1)
        check('private-file', scope(file), 1)
        check('private-ancestor', scope(root, 1), 1)
        check('file-is-not-ancestor', scope(file, 1), 0)
        check('missing-leaf', scope(root / 'absent'), 0)
        check('missing-ancestor', scope(root / 'absent', 1), 0)
        for mode in (0o620, 0o602, 0o640, 0o604, 0o610, 0o601):
            os.chmod(file, mode)
            check('leaf-mode-%o' % mode, scope(file), 0)
        os.chmod(file, 0o600)
        for mode in (0o720, 0o702, 0o1720):
            os.chmod(root, mode)
            check('writable-ancestor-%o' % mode, scope(root, 1), 0)
        os.chmod(root, 0o700)
        link = root / 'symlink'
        link.symlink_to(file)
        check('symlink-leaf', scope(link), 0)
        check('symlink-ancestor', scope(link, 1), 0)
        check('nofollow-open', native.cm_open(os.fsencode(link)), -1)
        check('missing-open', native.cm_open(os.fsencode(root / 'absent')), -1)
        hard = root / 'hardlink'
        os.link(file, hard)
        check('hardlink-original', scope(file), 0)
        check('hardlink-alias', scope(hard), 0)
        hard.unlink()
        fifo = root / 'fifo'
        os.mkfifo(fifo, 0o600)
        check('fifo-leaf', scope(fifo), 0)
        check('fifo-ancestor', scope(fifo, 1), 0)
        descriptor = native.cm_open(os.fsencode(file))
        if descriptor < 0:
            raise RuntimeError('positive open failed')
        try:
            check('descriptor-cloexec', os.get_inheritable(descriptor), False)
            check('same-identity', matches(descriptor, file), 1)
            read_fd, write_fd = os.pipe()
            try:
                check('different-device-identity', matches(read_fd, file), 0)
            finally:
                os.close(read_fd)
                os.close(write_fd)
            check('invalid-descriptor', matches(-1, file), 0)
            check('missing-identity', matches(descriptor, root / 'absent'), 0)
            check('symlink-identity', matches(descriptor, link), 0)
            os.link(file, hard)
            check('hardlink-same-identity', matches(descriptor, hard), 1)
            hard.unlink()
            moved = root / 'moved'
            file.rename(moved)
            file.write_bytes(b'replacement')
            os.chmod(file, 0o600)
            check('replacement-identity', matches(descriptor, file), 0)
            check('renamed-held-identity', matches(descriptor, moved), 1)
        finally:
            os.close(descriptor)
        check('closed-descriptor', matches(descriptor, file), 0)
        # Native ACLs on disposable objects, using the actual invoking principal.
        user = subprocess.check_output(['id', '-un'], text=True).strip()
        for path, ancestor in ((file, 0), (root, 1)):
            for tag, expected in (('allow', 0), ('deny', ancestor)):
                subprocess.run(['chmod', '+a', f'user:{user} {tag} write', str(path)], check=True)
                try:
                    check(f'acl-{tag}-ancestor-{ancestor}', scope(path, ancestor), expected)
                finally:
                    subprocess.run(['chmod', '-N', str(path)], check=True)
        check('acl-removed-file', scope(file), 1)
        check('acl-removed-ancestor', scope(root, 1), 1)
    return results


if __name__ == '__main__':
    cases = exercise(Path(sys.argv[1]))
    print(json.dumps({'category': 'real-filesystem', 'cases': cases}, indent=2))
    sys.exit(0 if cases and all(c['passed'] for c in cases) else 1)
