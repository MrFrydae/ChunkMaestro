"""Fixed cloud-final entry; refuse before root mutations outside positive guest."""
import base64
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, '/opt/cm14-fixture')
from common import Failure, ROOT, guest_source, guard, require, trusted

try:
    require(len(sys.argv) == 1 and platform.system() == 'Linux' and os.geteuid() == 0, 'guest-root')
    require(Path('/sys/class/dmi/id/sys_vendor').read_text().strip() == 'QEMU', 'qemu-required')
    fw = Path('/sys/firmware/qemu_fw_cfg/by_name/opt/cm14')
    require((fw / 'accelerator/raw').read_text().strip() == 'tcg', 'tcg-required')
    raw = trusted('/run/cm14-authorization-template.json').read_bytes()
    require(len(raw) < 8192 and hashlib.sha256(raw).hexdigest() ==
            (fw / 'authorization-sha256/raw').read_text().strip(), 'template-hash')
    identity = json.loads(raw)
    require(identity['nonce'] == (fw / 'nonce/raw').read_text().strip()
            and identity['source_sha256'] ==
            (fw / 'source-sha256/raw').read_text().strip(), 'template-binding')
    guest_source(identity['source_sha256'])
    identity['boot_id'] = Path('/proc/sys/kernel/random/boot_id').read_text().strip()
    fd = os.open('/run/cm14-authorization.json', os.O_CREAT | os.O_EXCL | os.O_WRONLY | os.O_NOFOLLOW, 0o600)
    with os.fdopen(fd, 'w') as stream:
        json.dump(identity, stream)
    guard()
    # Fixed local source only. Outer harness retains bounded serial output and destroys VM.
    # Guest receipts contain only this invocation's report, including guard/collision
    # failures. Never open campaign.json: it may belong to an unknown collision.
    with open('/dev/ttyS0', 'wb', buffering=0) as serial:
        result = subprocess.run(['/usr/bin/python3', '-I', '/opt/cm14-fixture/guest.py'],
                                stdin=subprocess.DEVNULL, stdout=serial, stderr=subprocess.DEVNULL,
                                timeout=160, close_fds=True,
                                env={'PATH': '/usr/bin:/usr/sbin', 'LC_ALL': 'C'})
        exit_record = {'schema': 1, 'nonce': identity['nonce'],
                       'source_sha256': identity['source_sha256'], 'exit': result.returncode}
        serial.write(b'\nCM14-EXIT:' + base64.b64encode(json.dumps(exit_record).encode()) + b'\n')
    sys.exit(result.returncode)
except Exception as error:
    print('UNAVAILABLE:' + (str(error) if isinstance(error, Failure) else 'provision-prerequisite'), flush=True)
    sys.exit(3)
