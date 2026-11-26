"""Exercise the actual workflow export guard with inert nm/readelf fixtures."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
EXPORTS = (
    'cm_escrow_bind', 'cm_escrow_close', 'cm_escrow_error',
    'cm_escrow_handback', 'cm_escrow_measure_original_v1',
    'cm_escrow_metadata', 'cm_escrow_observe', 'cm_escrow_upload',
)


def workflow_guard():
    lines = (ROOT / '.github/workflows/ci.yml').read_text().splitlines()
    start = lines.index('      - name: Inspect exact additive escrow exports and binary provenance')
    run = lines.index('        run: |', start) + 1
    script = []
    for line in lines[run:]:
        if not line.startswith('          '):
            break
        script.append(line[10:])
    if not script:
        raise AssertionError('Missing export guard')
    return '\n'.join(script) + '\n'


class EscrowExportsTests(unittest.TestCase):
    def run_guard(self, extra=(), expected_type='T'):
        with tempfile.TemporaryDirectory(prefix='escrow-exports-') as directory:
            root = Path(directory)
            commands = root / 'commands'
            commands.mkdir()
            scripts = {
                'nm': '''#!/bin/sh
case " $* " in
  *" --format=posix "*) cat "$POSIX_SYMBOLS" ;;
  *) cat "$BSD_SYMBOLS" ;;
esac
''',
                'readelf': '#!/bin/sh\nprintf "synthetic ELF fixture, not native proof\\n"\n',
                'sha256sum': '#!/bin/sh\nprintf "synthetic digest fixture %s\\n" "$1"\n',
            }
            for name, script in scripts.items():
                executable = commands / name
                executable.write_text(script)
                executable.chmod(0o700)
            symbols = [(name, expected_type) for name in EXPORTS] + list(extra)
            posix = root / 'posix-symbols.txt'
            bsd = root / 'bsd-symbols.txt'
            posix.write_text(''.join(f'{name} {kind} 1000 20\n' for name, kind in symbols))
            bsd.write_text(''.join(f'0000000000001000 {kind} {name}\n' for name, kind in symbols))
            output = root / '.artifacts/ci/inspector-metadata/session-escrow'
            output.mkdir(parents=True)
            environment = os.environ.copy()
            environment.update({
                'PATH': str(commands) + os.pathsep + environment['PATH'],
                'POSIX_SYMBOLS': str(posix), 'BSD_SYMBOLS': str(bsd),
            })
            result = subprocess.run(['/bin/bash'], input=workflow_guard(), text=True,
                                    cwd=root, env=environment, capture_output=True,
                                    timeout=10, check=False)
            return result.returncode, result.stdout + result.stderr

    def test_exact_strong_surface_passes(self):
        code, output = self.run_guard()
        self.assertEqual(0, code, output)

    def test_unexpected_export_types_refuse(self):
        for kind in ('T', 'W', 'w', 'i', 'I', 'D', 'R', 'B', 'V', 'A'):
            with self.subTest(kind=kind):
                code, output = self.run_guard((('unexpected_export', kind),))
                self.assertNotEqual(0, code, output)

    def test_expected_name_with_nonstrong_binding_refuses(self):
        code, output = self.run_guard(expected_type='W')
        self.assertNotEqual(0, code, output)


if __name__ == '__main__':
    unittest.main()
