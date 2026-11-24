"""Fixed cloud-final entry; refuse before root mutations outside positive guest."""
import base64
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import subprocess
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, '/opt/cm14-fixture')
from common import Failure, ROOT, guest_source, guard, require, trusted

def progress(serial, identity, phase):
    # Only fixed source/nonce-bound labels; never exception text or environment.
    require(phase in ('provision-entered', 'template-bound', 'authorization-written',
                      'guard-passed', 'guest-returned', 'provision-failed', 'guest-timeout'),
            'progress-phase')
    record = {'schema': 1, 'nonce': identity['nonce'],
              'source_sha256': identity['source_sha256'], 'phase': phase}
    raw = b'\nCM14-BOOT:' + base64.b64encode(json.dumps(record).encode()) + b'\n'
    require(serial.write(raw) == len(raw), 'serial-short-write')


def run(serial, binding):
    progress(serial, binding, 'provision-entered')
    fw = Path('/sys/firmware/qemu_fw_cfg/by_name/opt/cm14')
    raw = trusted('/run/cm14-authorization-template.json').read_bytes()
    require(len(raw) < 8192 and hashlib.sha256(raw).hexdigest() ==
            (fw / 'authorization-sha256/raw').read_text().strip(), 'template-hash')
    identity = json.loads(raw)
    require(identity['nonce'] == binding['nonce'] and identity['source_sha256'] == binding['source_sha256'],
            'template-binding')
    progress(serial, binding, 'template-bound')
    guest_source(identity['source_sha256'])
    identity['boot_id'] = Path('/proc/sys/kernel/random/boot_id').read_text().strip()
    fd = os.open('/run/cm14-authorization.json', os.O_CREAT | os.O_EXCL | os.O_WRONLY | os.O_NOFOLLOW, 0o600)
    with os.fdopen(fd, 'w') as stream:
        json.dump(identity, stream)
    progress(serial, binding, 'authorization-written')
    guard()
    progress(serial, binding, 'guard-passed')
    # Retain both real guest receipts unchanged. No filesystem report adoption.
    result = subprocess.run(['/usr/bin/python3', '-I', '/opt/cm14-fixture/guest.py'],
                            stdin=subprocess.DEVNULL, stdout=serial, stderr=subprocess.DEVNULL,
                            timeout=160, close_fds=True,
                            env={'PATH': '/usr/bin:/usr/sbin', 'LC_ALL': 'C'})
    progress(serial, binding, 'guest-returned')
    exit_record = {'schema': 1, 'nonce': identity['nonce'],
                   'source_sha256': identity['source_sha256'], 'exit': result.returncode}
    raw = b'\nCM14-EXIT:' + base64.b64encode(json.dumps(exit_record).encode()) + b'\n'
    require(serial.write(raw) == len(raw), 'serial-short-write')
    return result.returncode


def main():
    require(len(sys.argv) == 1 and platform.system() == 'Linux' and os.geteuid() == 0, 'guest-root')
    require(Path('/sys/class/dmi/id/sys_vendor').read_text().strip() == 'QEMU', 'qemu-required')
    fw = Path('/sys/firmware/qemu_fw_cfg/by_name/opt/cm14')
    require((fw / 'accelerator/raw').read_text().strip() == 'tcg', 'tcg-required')
    binding = {name: (fw / field / 'raw').read_text().strip()
               for name, field in (('nonce', 'nonce'), ('source_sha256', 'source-sha256'))}
    require(re.fullmatch('[0-9a-f]{32}', binding['nonce']) is not None
            and re.fullmatch('[0-9a-f]{64}', binding['source_sha256']) is not None, 'firmware-binding')
    with open('/dev/ttyS0', 'wb', buffering=0) as serial:
        try:
            return run(serial, binding)
        except Exception as error:
            progress(serial, binding, 'guest-timeout' if isinstance(error, subprocess.TimeoutExpired)
                     else 'provision-failed')
            return 3


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception:
        print('UNAVAILABLE:provision-prerequisite', flush=True)
        sys.exit(3)
