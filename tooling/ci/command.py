"""Run one ordinary CI command with live output and no credential environment."""
import os
import argparse
import csv
from pathlib import Path
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import time

ALLOWED = {'CI', 'PATH', 'HOME', 'USER', 'USERNAME', 'USERPROFILE', 'SYSTEMROOT', 'WINDIR',
           'SYSTEMDRIVE', 'COMSPEC', 'PATHEXT', 'PROGRAMFILES', 'PROGRAMFILES(X86)',
           'PROGRAMW6432', 'APPDATA', 'LOCALAPPDATA', 'TEMP', 'TMP', 'TMPDIR',
           'DOTNET_ROOT', 'CHUNKMAESTRO_DOTNET', 'NUGET_PACKAGES', 'SDKROOT',
           'DEVELOPER_DIR', 'LANG', 'LC_ALL', 'PSMODULEPATH'}


def child_environment(source):
    env = {key: value for key, value in source.items() if key.upper() in ALLOWED}
    env.update(DOTNET_NOLOGO='1', DOTNET_CLI_TELEMETRY_OPTOUT='1',
               MSBUILDDISABLENODEREUSE='1', DOTNET_CLI_USE_MSBUILD_SERVER='0',
               PYTHONUTF8='1', PYTHONUNBUFFERED='1')
    return env


def finish(scratch, result, windows):
    if result != 0 or windows:
        # Compiler/analyzer servers can retain open DLLs on Windows after a
        # successful build. The disposable runner owns final scratch cleanup.
        print('Command scratch retained: ' + scratch, flush=True)
    else:
        try:
            shutil.rmtree(scratch)
        except OSError as error:
            print('Command succeeded; scratch cleanup deferred: ' + str(error), flush=True)
    return result


def protect_windows_scratch(scratch, env):
    # Only called for the new empty directory just created by mkdtemp below.
    # Python 3.13 uses OWNER RIGHTS (S-1-3-4) for mode 0700; production requires
    # explicit trusted SIDs rather than that owner alias on storage ancestors.
    identity = subprocess.check_output(['whoami', '/user', '/fo', 'csv', '/nh'],
                                       env=env, text=True, timeout=15)
    rows = list(csv.reader(identity.strip().splitlines()))
    if len(rows) != 1 or len(rows[0]) != 2 or re.fullmatch(r'S-1-[0-9]+(?:-[0-9]+)+', rows[0][1]) is None:
        raise ValueError('Could not establish the actual Windows user SID')
    sid = rows[0][1]
    operations = [('/grant:r', '*' + sid + ':(OI)(CI)F'),
                  ('/setowner', '*' + sid),
                  ('/remove:g', '*S-1-3-4'), ('/inheritancelevel:r',)]
    for operation in operations:
        subprocess.run(['icacls', scratch, *operation, '/Q'], env=env, check=True, timeout=15)
    print('New Windows scratch uses explicit user SID; OWNER RIGHTS alias removed: ' + scratch, flush=True)
    subprocess.run(['icacls', scratch], env=env, check=True, timeout=15)


class CommandInterrupted(Exception):
    def __init__(self, number):
        self.number = number


def execute(args, env, timeout=180, heartbeat=15):
    """Bound the command locally even if runner cancellation cannot finish it."""
    if not 0 < timeout <= 180 or not 0 < heartbeat <= 15:
        raise ValueError('Command deadline must be at most 180s; heartbeat at most 15s')
    started = time.monotonic()
    process = subprocess.Popen(args, env=env, start_new_session=os.name != 'nt')

    def interrupted(number, frame):
        raise CommandInterrupted(number)

    previous = signal.signal(signal.SIGTERM, interrupted)
    try:
        while True:
            remaining = timeout - (time.monotonic() - started)
            if remaining <= 0:
                raise subprocess.TimeoutExpired(args, timeout)
            try:
                result = process.wait(timeout=min(heartbeat, remaining))
            except subprocess.TimeoutExpired:
                print(f'[ci-command] running pid={process.pid}; elapsed={time.monotonic() - started:.1f}s; '
                      f'deadline={timeout}s', flush=True)
                continue
            if os.name != 'nt':
                # A child inheriting runner output can keep a completed step
                # open after its command exits. This group belongs only to us.
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                except OSError as failure:
                    print('[ci-command] Remaining process-group cleanup failed: ' + str(failure), flush=True)
                    return 1
            return result
    except (subprocess.TimeoutExpired, KeyboardInterrupt, CommandInterrupted) as error:
        code = 124 if isinstance(error, subprocess.TimeoutExpired) else (
            128 + error.number if isinstance(error, CommandInterrupted) else 130)
        print(f'[ci-command] STOP exit={code}; terminating owned process tree pid={process.pid}', flush=True)
        try:
            if os.name == 'nt':
                subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'],
                               env=env, timeout=10, check=False)
            else:
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
            process.wait(timeout=5)
        except (OSError, subprocess.SubprocessError) as failure:
            print('[ci-command] Process termination unconfirmed: ' + str(failure), flush=True)
        return code
    finally:
        signal.signal(signal.SIGTERM, previous)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--timeout-seconds', type=int, default=180, choices=range(1, 181), metavar='1..180')
    parser.add_argument('command', nargs=argparse.REMAINDER)
    options = parser.parse_args()
    args = options.command
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
    if os.name == 'nt':
        protect_windows_scratch(scratch, env)
    env.update(TEMP=scratch, TMP=scratch, TMPDIR=scratch)
    print('+ ' + ' '.join(args) + f'; local deadline={options.timeout_seconds}s', flush=True)
    result = execute(args, env, timeout=options.timeout_seconds)
    # Actual fixture detach failures still return nonzero and retain scratch.
    # Generic cleanup must never replace the real command result.
    return finish(scratch, result, os.name == 'nt')


if __name__ == '__main__':
    sys.exit(main())
