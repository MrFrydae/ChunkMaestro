"""Real scanner regressions using only a nonfunctional credential-shaped fixture."""
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

import scan_secrets


def commit(root, message):
    subprocess.run(['git', 'add', '-A'], cwd=root, check=True)
    subprocess.run(['git', '-c', 'user.name=QA fixture', '-c', 'user.email=fixture@example.invalid',
                    '-c', 'commit.gpgsign=false', 'commit', '--quiet', '-m', message], cwd=root, check=True)


def run_scan(root, token):
    script = root / 'tooling/build/scan_secrets.py'
    result = subprocess.run([sys.executable, str(script)], cwd=root, capture_output=True, text=True)
    if token in result.stdout or token in result.stderr:
        raise AssertionError('Scanner diagnostics exposed the synthetic value.')
    for report in (root / '.artifacts/security').glob('*.json'):
        if token in report.read_text(encoding='utf-8'):
            raise AssertionError('Scanner artifact exposed the synthetic value.')
    return result


def main():
    with tempfile.TemporaryDirectory(prefix='chunkmaestro-scan-fixture-') as temporary:
        root = Path(temporary)
        subprocess.run(['git', 'init', '--quiet', str(root)], check=True)
        script = root / 'tooling/build/scan_secrets.py'
        script.parent.mkdir(parents=True)
        shutil.copyfile(Path(scan_secrets.__file__), script)
        # Construct a nonfunctional high-entropy token-shaped fixture at runtime.
        token = 'ghp_' + 'X7m2P9q4R8s1T6v3W0y5Z9a2B7c4D8e1F6g3'
        (root / 'fixture.txt').write_bytes(('token=' + token + ' # gitleaks:allow\n').encode('utf-8'))
        (root / '.gitignore').write_bytes(b'.artifacts/\n')
        commit(root, 'Synthetic scanner fixture')
        result = run_scan(root, token)
        assert result.returncode, 'Synthetic token was accepted.'
        reports = root / '.artifacts/security'
        history = json.loads((reports / 'history.json').read_text(encoding='utf-8'))
        current = json.loads((reports / 'tracked-files.json').read_text(encoding='utf-8'))
        assert history and current, 'Both history and current scans must detect the fixture.'
        fingerprint = history[0]['Fingerprint']
        (root / 'fixture.txt').unlink()
        ignore = root / '.gitleaksignore'
        ignore.write_bytes((fingerprint + '\n').encode('utf-8'))
        commit(root, 'Deleted fixture with fingerprint exclusion')
        for content in (fingerprint + '\n', token + '\n'):
            expected = content.encode('utf-8')
            ignore.write_bytes(expected)
            result = run_scan(root, token)
            assert result.returncode, 'Repository ignore entry bypassed scanning.'
            assert ignore.read_bytes() == expected, 'Wrapper modified the checkout.'
            summary = json.loads((reports / 'summary.json').read_text(encoding='utf-8'))
            assert summary['status'] == 'blocked-ignore-file', 'Ignore file did not block before scanning.'
        ignore.unlink()
        commit(root, 'Remove unsupported ignore file')
        result = run_scan(root, token)
        assert result.returncode, 'Deleted historical fixture escaped detection.'
        assert json.loads((reports / 'history.json').read_text(encoding='utf-8'))
        assert not json.loads((reports / 'tracked-files.json').read_text(encoding='utf-8'))
        print('PASS: inline allowances and fingerprint ignores cannot hide the historical fixture; invalid ignore data absent from stdout/stderr/artifacts; checkout unchanged.')


if __name__ == '__main__':
    main()
