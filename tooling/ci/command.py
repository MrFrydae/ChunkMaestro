"""Run one ordinary CI command with live output and no credential environment."""
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

ALLOWED = {'CI', 'PATH', 'HOME', 'USER', 'USERNAME', 'USERPROFILE', 'SYSTEMROOT', 'WINDIR',
           'SYSTEMDRIVE', 'COMSPEC', 'PATHEXT', 'PROGRAMFILES', 'PROGRAMFILES(X86)',
           'PROGRAMW6432', 'APPDATA', 'LOCALAPPDATA', 'TEMP', 'TMP', 'TMPDIR',
           'DOTNET_ROOT', 'CHUNKMAESTRO_DOTNET', 'NUGET_PACKAGES', 'SDKROOT',
           'DEVELOPER_DIR', 'LANG', 'LC_ALL'}


def child_environment(source):
    env = {key: value for key, value in source.items() if key.upper() in ALLOWED}
    env.update(DOTNET_NOLOGO='1', DOTNET_CLI_TELEMETRY_OPTOUT='1',
               MSBUILDDISABLENODEREUSE='1', DOTNET_CLI_USE_MSBUILD_SERVER='0',
               PYTHONUTF8='1', PYTHONUNBUFFERED='1')
    return env


def main():
    args = sys.argv[1:]
    if args[:1] == ['--']:
        args = args[1:]
    if not args:
        raise ValueError('An explicit command is required')
    env = child_environment(os.environ)
    # Windows storage deliberately refuses shared-writable runner ancestors.
    base = None
    if sys.platform == 'darwin':
        # macOS named-pipe sockets have a 104-byte path limit. The default
        # /var/folders temporary prefix leaves too little room for crash tests.
        base = Path('/private/tmp')
    if os.name == 'nt':
        base = Path(env['LOCALAPPDATA']) / 'ChunkMaestroCI'
        if not base.is_absolute():
            raise ValueError('LOCALAPPDATA must be absolute')
        base.mkdir(exist_ok=True)
    scratch = tempfile.mkdtemp(prefix='storage-', dir=base)
    env.update(TEMP=scratch, TMP=scratch, TMPDIR=scratch)
    print('+ ' + ' '.join(args), flush=True)
    result = subprocess.run(args, env=env).returncode
    if result == 0:
        shutil.rmtree(scratch)
    else:
        # A failed disk-image detach must not be followed by recursive deletion
        # of an attached fixture. Keep failed/interrupted command scratch intact.
        print('Failed command scratch retained: ' + scratch, flush=True)
    return result


if __name__ == '__main__':
    sys.exit(main())
