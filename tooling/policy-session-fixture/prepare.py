"""Offline payload construction for the parent-owned Debian harness. No execution."""
import base64
import hashlib
import json
from pathlib import Path
import re
import sys
import subprocess

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent))
from common import IMAGE, bound_payload, require


def prepare(cloud_config, nonce, manifest_path=None, manifest_sha256=None):
    require(re.fullmatch('[0-9a-f]{32}', nonce) is not None, 'prepare-nonce')
    root = Path(__file__).resolve().parent
    require(manifest_path is not None and manifest_sha256 is not None, 'trusted-source-binding-required')
    path = Path(manifest_path).resolve()
    require(not path.is_relative_to(root), 'external-manifest-required')
    manifest_raw = path.read_bytes()
    manifest, payload = bound_payload(root, manifest_raw, manifest_sha256)
    # A caller's digest alone is not review authority: every byte must exist at that Git commit.
    for name, data in payload.items():
        committed = subprocess.run(['git', '-C', str(root), 'show',
            manifest['commit'] + ':tooling/policy-session-fixture/' + name],
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=5, check=False)
        require(committed.returncode == 0 and committed.stdout == data, 'reviewed-commit-bytes')
    source = manifest_sha256
    inventory = json.loads((root / 'baseline.json').read_text())['inventory']
    versions = {key: value for key, value in inventory.items()
                if key in ('systemd', 'libsystemd0', 'python3', 'python3.13') or key.startswith('linux-image-6.12.')}
    template = {'schema': 1, 'nonce': nonce, 'source_sha256': source, 'image_sha512': IMAGE,
                'packages': versions, 'kernel_release': next(key.removeprefix('linux-image-')
                    for key in versions if key.startswith('linux-image-'))}
    raw = json.dumps(template, sort_keys=True, separators=(',', ':'))
    config = json.loads(cloud_config.removeprefix('#cloud-config\n'))
    require(config.get('package_update') is False and config.get('package_upgrade') is False,
            'no-package-mutation')
    for name, data in payload.items():
        config['write_files'].append({'path': '/opt/cm14-fixture/' + name,
            'owner': 'root:root', 'permissions': '0644', 'encoding': 'b64',
            'content': base64.b64encode(data).decode()})
    config['write_files'].append({'path': '/run/cm14-source-manifest.json',
        'owner': 'root:root', 'permissions': '0600', 'encoding': 'b64',
        'content': base64.b64encode(manifest_raw).decode()})
    config['write_files'].append({'path': '/run/cm14-authorization-template.json',
                                  'owner': 'root:root', 'permissions': '0600', 'content': raw})
    config['runcmd'].insert(0, ['/usr/bin/python3', '-I', '/opt/cm14-fixture/provision.py'])
    fw = {'nonce': nonce, 'source-sha256': source, 'accelerator': 'tcg',
          'authorization-sha256': hashlib.sha256(raw.encode()).hexdigest()}
    args = []
    for key, value in fw.items():
        args.extend(['-fw_cfg', 'name=opt/cm14/' + key + ',string=' + value])
    return '#cloud-config\n' + json.dumps(config), args
