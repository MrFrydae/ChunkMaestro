"""Run pinned Gitleaks against tracked history and tracked working files, redacted."""
import json
from pathlib import Path
import shutil
import subprocess
import tempfile

VERSION = '8.30.1'
ROOT = Path(__file__).resolve().parents[2]


def main():
    scanner = shutil.which('gitleaks')
    if not scanner:
        raise RuntimeError(f'Install Gitleaks {VERSION}; no scan was performed.')
    version = subprocess.check_output([scanner, 'version'], text=True).strip()
    if version != VERSION:
        raise RuntimeError(f'Expected Gitleaks {VERSION}, got {version}.')
    output = ROOT / '.artifacts/security'
    output.mkdir(parents=True, exist_ok=True)
    # Fixed empty config prevents local allowlists/env overrides from silently
    # disabling default detectors. Ignore inline allow-comments and ignore files.
    with tempfile.TemporaryDirectory(prefix='chunkmaestro-secret-scan-') as temporary:
        workspace = Path(temporary)
        config = workspace / 'default.toml'
        config.write_text('[extend]\nuseDefault = true\n', encoding='utf-8')
        ignore = workspace / 'empty.ignore'
        ignore.touch()
        common = ['--redact=100', '--no-banner', '--no-color', '--config', str(config),
                  '--gitleaks-ignore-path', str(ignore), '--ignore-gitleaks-allow',
                  '--report-format', 'json', '--timeout', '120']
        history = subprocess.run([scanner, 'git', str(ROOT), '--log-opts=--all',
                                  *common, '--report-path', str(output / 'history.json')])
        # Copy tracked files only: generated artifacts, user-local secrets and
        # ignored build/cache trees aren't scanned or uploaded accidentally.
        tracked = workspace / 'tracked'
        tracked.mkdir()
        names = subprocess.check_output(['git', 'ls-files', '-z'], cwd=ROOT).split(b'\0')
        for name in names:
            if not name:
                continue
            relative = Path(name.decode('utf-8'))
            source = ROOT / relative
            if source.is_symlink():
                raise RuntimeError('Tracked symlinks require review before scanning.')
            if source.is_file():
                destination = tracked / relative
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(source, destination)
        current = subprocess.run([scanner, 'dir', str(tracked), *common,
                                  '--report-path', str(output / 'tracked-files.json')])
    status = {'scanner': f'gitleaks {VERSION}', 'history_exit': history.returncode,
              'tracked_files_exit': current.returncode, 'redacted': True}
    (output / 'summary.json').write_text(json.dumps(status, indent=2) + '\n', encoding='utf-8')
    if history.returncode or current.returncode:
        raise RuntimeError('Secret scan failed; review redacted local reports. No success claimed.')
    print('PASS: tracked history and working files scanned with redacted output.')


if __name__ == '__main__':
    main()
