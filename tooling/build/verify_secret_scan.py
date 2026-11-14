"""Real negative fixture for the pinned scanner; no functioning credential is used."""
import json
from pathlib import Path
import subprocess
import tempfile
from unittest.mock import patch

import scan_secrets


def main():
    with tempfile.TemporaryDirectory(prefix='chunkmaestro-scan-fixture-') as temporary:
        root = Path(temporary)
        subprocess.run(['git', 'init', '--quiet', str(root)], check=True)
        # Construct a nonfunctional high-entropy token-shaped fixture at runtime.
        token = 'ghp_' + 'X7m2P9q4R8s1T6v3W0y5Z9a2B7c4D8e1F6g3'
        (root / 'fixture.txt').write_text('token=' + token + '\n', encoding='utf-8')
        subprocess.run(['git', 'add', 'fixture.txt'], cwd=root, check=True)
        subprocess.run(['git', '-c', 'user.name=QA fixture', '-c', 'user.email=fixture@example.invalid',
                        '-c', 'commit.gpgsign=false', 'commit', '--quiet', '-m', 'Synthetic scanner fixture'],
                       cwd=root, check=True)
        with patch.object(scan_secrets, 'ROOT', root):
            try:
                scan_secrets.main()
            except RuntimeError:
                pass
            else:
                raise AssertionError('Scanner accepted a synthetic credential-shaped fixture.')
        reports = root / '.artifacts/security'
        for name in ('history.json', 'tracked-files.json'):
            text = (reports / name).read_text(encoding='utf-8')
            assert json.loads(text), 'Missing finding: ' + name
            assert token not in text, 'Report did not redact the synthetic value.'
        print('PASS: history and tracked-file detectors reject the nonfunctional fixture and redact reports.')


if __name__ == '__main__':
    main()
