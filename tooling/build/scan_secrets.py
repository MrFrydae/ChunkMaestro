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
    output = ROOT / '.artifacts/security'
    output.mkdir(parents=True, exist_ok=True)
    for name in ('history.json', 'tracked-files.json'):
        (output / name).write_text('[]\n', encoding='utf-8')
    summary = output / 'summary.json'
    summary.write_text(json.dumps({'scanner': f'gitleaks {VERSION}', 'status': 'not-run'}) + '\n', encoding='utf-8')
    names = subprocess.check_output(['git', 'ls-files', '-z'], cwd=ROOT).split(b'\0')
    ignore = ROOT / '.gitleaksignore'
    if ignore.exists() or ignore.is_symlink() or any(
            Path(name.decode('utf-8')).name.casefold() == '.gitleaksignore' for name in names if name):
        summary.write_text(json.dumps({'scanner': f'gitleaks {VERSION}', 'status': 'blocked-ignore-file'}) + '\n', encoding='utf-8')
        raise RuntimeError('Repository Gitleaks ignore files are prohibited; no scan ran. Remove the ignore file and rerun.')
    version = subprocess.run([scanner, 'version'], capture_output=True, text=True)
    if version.returncode or version.stdout.strip() != VERSION:
        raise RuntimeError('Pinned Gitleaks version required; scanner diagnostics are withheld.')
    # Use fixed defaults. Reject implicit repository ignore files above because
    # Gitleaks also consumes them regardless of the explicit empty-ignore path.
    # Raw scanner diagnostics are captured and withheld, including on errors.
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
                                  *common, '--report-path', str(output / 'history.json')],
                                 capture_output=True, text=True)
        # Copy tracked files only: generated artifacts, user-local secrets and
        # ignored build/cache trees aren't scanned or uploaded accidentally.
        tracked = workspace / 'tracked'
        tracked.mkdir()
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
                                  '--report-path', str(output / 'tracked-files.json')],
                                 capture_output=True, text=True)
    status = {'scanner': f'gitleaks {VERSION}', 'history_exit': history.returncode,
              'tracked_files_exit': current.returncode, 'redacted': True}
    (output / 'summary.json').write_text(json.dumps(status, indent=2) + '\n', encoding='utf-8')
    if history.returncode or current.returncode:
        raise RuntimeError('Secret scan failed; review redacted local reports. No success claimed.')
    print('PASS: tracked history and working files scanned with redacted output.')


if __name__ == '__main__':
    main()
