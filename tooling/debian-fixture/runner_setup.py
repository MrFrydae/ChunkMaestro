#!/usr/bin/env python3
"""Fixed ephemeral Ubuntu runner setup; no VM or KVM access changes."""
import argparse
import hashlib
import os
from pathlib import Path
import re
import signal
import tempfile

import fixture as f

PACKAGES = ('curl', 'qemu-system-x86', 'qemu-utils', 'cloud-image-utils', 'openssh-client')
SOURCES = Path('/etc/apt/sources.list.d/ubuntu.sources')
KEYRING = '/usr/share/keyrings/ubuntu-archive-keyring.gpg'


def validate_sources(data):
    # Accept only the existing standard noble deb822 source, fail on unknown fields.
    text = data.decode('ascii')
    blocks = re.split(r'\n\s*\n', text.strip())
    if not blocks:
        raise f.Failure('setup-source-policy')
    for block in blocks:
        fields = {}
        for line in block.splitlines():
            if not line.strip() or line.lstrip().startswith('#'):
                continue
            key, sep, value = line.partition(':')
            if not sep or key in fields:
                raise f.Failure('setup-source-policy')
            fields[key] = value.strip()
        if not fields:
            continue
        if (set(fields) != {'Types', 'URIs', 'Suites', 'Components', 'Signed-By'}
                or fields['Types'] != 'deb'
                or fields['Signed-By'] != KEYRING
                or not fields['URIs'].split()
                or any(uri not in ('http://archive.ubuntu.com/ubuntu/', 'http://security.ubuntu.com/ubuntu/',
                                   'https://archive.ubuntu.com/ubuntu/', 'https://security.ubuntu.com/ubuntu/',
                                   'http://azure.archive.ubuntu.com/ubuntu/', 'https://azure.archive.ubuntu.com/ubuntu/')
                       for uri in fields['URIs'].split())
                or not fields['Suites'].split()
                or not set(fields['Suites'].split()) <= {'noble', 'noble-updates', 'noble-security', 'noble-backports'}
                or not {'main', 'universe'} <= set(fields['Components'].split())
                or not set(fields['Components'].split()) <= {'main', 'universe', 'restricted', 'multiverse'}):
            raise f.Failure('setup-source-policy')
    if not any(line.startswith('Types:') for line in text.splitlines()):
        raise f.Failure('setup-source-policy')


def runner_guard():
    if (f.platform.system() != 'Linux' or f.platform.machine() != 'x86_64'
            or os.geteuid() == 0
            or os.environ.get('GITHUB_ACTIONS') != 'true'
            or os.environ.get('RUNNER_ENVIRONMENT') != 'github-hosted'
            or os.environ.get('RUNNER_OS') != 'Linux'):
        raise f.Failure('setup-runner-refused')
    release = dict(line.split('=', 1) for line in Path('/etc/os-release').read_text().splitlines() if '=' in line)
    if release.get('ID') != 'ubuntu' or release.get('VERSION_ID', '').strip('"') != '24.04':
        raise f.Failure('setup-runner-refused')


def apt_commands(root):
    # Private lists prevent resolution from cached third-party indexes. No repository edits.
    opts = ['-o', 'Dir::Etc::sourcelist=' + str(root / 'ubuntu.sources'),
            '-o', 'Dir::Etc::sourceparts=-', '-o', 'Dir::State::lists=' + str(root / 'lists'),
            '-o', 'Acquire::Retries=0', '-o', 'Acquire::http::Timeout=15',
            '-o', 'Acquire::https::Timeout=15', '-o', 'DPkg::Lock::Timeout=30',
            '-o', 'Acquire::AllowInsecureRepositories=false',
            '-o', 'Acquire::AllowDowngradeToInsecureRepositories=false',
            '-o', 'APT::Get::AllowUnauthenticated=false']
    base = ['/usr/bin/sudo', '-n', '--preserve-env=DEBIAN_FRONTEND,NEEDRESTART_MODE',
            '/usr/bin/timeout', '--signal=TERM', '--kill-after=5']
    update = base + ['230', '/usr/bin/apt-get'] + opts
    install = base + ['290', '/usr/bin/apt-get'] + opts
    return ((update + ['-o', 'APT::Update::Error-Mode=any', 'update'], 'apt-update', 240),
            (install + ['install', '--yes', '--no-install-recommends', '--no-remove', '--no-upgrade'] + list(PACKAGES),
             'apt-install', 300))


class SetupHarness(f.Harness):
    def environment(self):
        env = f.child_env(self.root)
        env.update(DEBIAN_FRONTEND='noninteractive', NEEDRESTART_MODE='l')
        return env


def versions(h):
    data = h.run(['/usr/bin/dpkg-query', '-W', '-f=${Package}\t${Version}\n'], 'package-inventory')
    # Bind all installed transitive versions without exposing arbitrary package strings.
    f.event('installed-package-manifest-sha256', hashlib.sha256(data).hexdigest())
    found = dict(line.split('\t', 1) for line in data.decode('ascii').splitlines())
    for name in PACKAGES:
        value = found.get(name, 'missing')
        if not re.fullmatch(r'[A-Za-z0-9.+:~_-]{1,100}', value):
            raise f.Failure('package-version-invalid')
        f.event('package-' + name, value)


def setup():
    runner_guard()
    # Never rely on package installation to create or broaden KVM access.
    if f.inventory()['kvm'] != 'ready':
        raise f.Failure('preflight-kvm')
    data = SOURCES.read_bytes()
    validate_sources(data)
    f.event('configured-ubuntu-source-sha256', hashlib.sha256(data).hexdigest())
    f.event('ubuntu-keyring-sha256', hashlib.sha256(Path(KEYRING).read_bytes()).hexdigest())
    root = Path(tempfile.mkdtemp(prefix='cm-runner-setup-', dir='/tmp'))
    h = SetupHarness(root)
    try:
        (root / 'ubuntu.sources').write_bytes(data)
        (root / 'lists').mkdir()
        for args, phase, seconds in apt_commands(root):
            h.run(args, phase, seconds)
    finally:
        try:
            versions(h)
        finally:
            h.cleanup()
            # APT's private indexes may be root-owned. Never add privileged cleanup.
            f.event('setup-workspace', 'retained-for-ephemeral-runner-destruction')


class FixedParser(argparse.ArgumentParser):
    def error(self, message):
        raise f.Failure('setup-arguments')


def main():
    parser = FixedParser()
    parser.add_argument('phase', choices=('inventory', 'setup', 'check'))
    args = parser.parse_args()
    if args.phase == 'inventory':
        f.inventory()
    elif args.phase == 'check':
        f.prerequisites()
    else:
        try:
            setup()
        finally:
            # Even failed source validation or APT retains every named status.
            f.inventory()


if __name__ == '__main__':
    def interrupted(signum, frame):
        raise f.Failure('setup-interrupted')
    signal.signal(signal.SIGTERM, interrupted)
    signal.signal(signal.SIGINT, interrupted)
    try:
        main()
    except f.Failure as exc:
        f.event(str(exc), 'failed')
        raise SystemExit(1)
    except Exception:
        f.event('setup-internal', 'failed-details-suppressed')
        raise SystemExit(1)
