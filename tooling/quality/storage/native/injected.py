"""Explicit simulated OS failures/metadata; not real OS runtime coverage."""
import ctypes
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

native = ctypes.CDLL(sys.argv[1])
native.cm_scope.argtypes = [ctypes.c_char_p, ctypes.c_int]
native.cm_test_fault.argtypes = [ctypes.c_int]
cases = []
with tempfile.TemporaryDirectory(prefix='cm-native-injected-') as directory:
    path = Path(directory)
    user = subprocess.check_output(['id', '-un'], text=True).strip()
    for name, fault, mode, ancestor, expected, acl in (
        ('foreign-owner-leaf', 1, 0o700, 0, 0, False),
        ('foreign-owner-ancestor', 1, 0o700, 1, 0, False),
        ('root-owner-ancestor', 2, 0o700, 1, 1, False),
        ('root-sticky-writable', 2, 0o1777, 1, 1, False),
        ('root-nonsticky-writable', 3, 0o777, 1, 0, False),
        ('acl-disappeared', 4, 0o700, 0, 1, False),
        ('acl-access-denied', 5, 0o700, 0, 0, False),
        ('acl-tag-io-error', 6, 0o700, 1, 0, True),
        ('acl-enumeration-io-error', 7, 0o700, 1, 0, True),
        ('acl-free-io-error', 8, 0o700, 1, 0, True),
    ):
        os.chmod(path, mode)
        if acl:
            subprocess.run(['chmod', '+a', f'user:{user} deny write', str(path)], check=True)
        try:
            native.cm_test_fault(fault)
            actual = native.cm_scope(os.fsencode(path), ancestor)
            cases.append({'name': name, 'fault': fault, 'expected': expected,
                          'actual': actual, 'passed': actual == expected})
        finally:
            native.cm_test_fault(0)
            if acl:
                subprocess.run(['chmod', '-N', str(path)], check=True)
print(json.dumps({'category': 'injected-OS-failure-or-metadata', 'cases': cases}, indent=2))
sys.exit(0 if all(c['passed'] for c in cases) else 1)
