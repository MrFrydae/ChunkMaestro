"""Create ONLY a new bounded disposable image, exercise it, detach ONLY that image."""
import json
import os
from pathlib import Path
import platform
import plistlib
import re
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[3]


def free_drive_letter(occupied):
    if isinstance(occupied, str):
        occupied = [occupied]
    if not isinstance(occupied, list) or not occupied or any(
            not isinstance(d, str) or re.fullmatch(r'[A-Za-z]:\\', d) is None for d in occupied):
        raise ValueError('Unexpected occupied drive-letter inventory')
    used = {d.upper() for d in occupied}
    letter = next((c for c in 'ZYXWVUTSRQPONMLKJIHGFED' if c + ':\\' not in used), None)
    if letter is None:
        raise ValueError('No free drive letter for disposable image')
    return letter


def mac_image_device(info, image, mount):
    matches = [entry for entry in info['images'] if Path(entry['image-path']).resolve() == image.resolve()]
    if not matches:
        return None
    if len(matches) != 1:
        raise ValueError('Ambiguous disposable image attachment')
    entities = matches[0]['system-entities']
    devices = [entry['dev-entry'] for entry in entities if re.fullmatch(r'/dev/disk[0-9]+', entry['dev-entry'])]
    mounts = {Path(entry['mount-point']).resolve() for entry in entities if entry.get('mount-point')}
    if (len(devices) != 1 or mounts != {mount.resolve()} or any(
            not re.fullmatch(re.escape(devices[0]) + r'(s[0-9]+)*', entry['dev-entry']) for entry in entities)):
        raise ValueError('Disposable image device or mount identity differs')
    return devices[0]


def detach_mac_image(image, mount, log, execute=subprocess.run):
    """Detach only this fixture. Busy cleanup may force only a reverified device."""
    def device():
        state = execute(['hdiutil', 'info', '-plist'], stdout=subprocess.PIPE,
                        stderr=subprocess.PIPE, timeout=30)
        if state.returncode:
            raise RuntimeError('Could not inspect disposable image attachment')
        # Do not log unrelated mounted image paths from the host-wide inventory.
        return mac_image_device(plistlib.loads(state.stdout), image, mount)

    def detach(args):
        result = execute(args, stdout=log, stderr=subprocess.STDOUT, timeout=30)
        log.flush()
        return result.returncode

    original = device()
    if original is None:
        if os.path.ismount(mount):
            raise RuntimeError('Fixture mount belongs to an unexpected image')
        return
    code = detach(['hdiutil', 'detach', original])
    if code == 16:
        current = device()
        if current == original:
            log.write('Busy fixture: exact image/device/mount reverified before forced detachment.\n')
            code = detach(['hdiutil', 'detach', '-force', original])
        elif current is not None:
            raise RuntimeError('Disposable image identity changed during cleanup')
        else:
            code = 0
    if code or device() is not None or os.path.ismount(mount):
        raise RuntimeError('Disposable image detachment failed or remains mounted')


def run(output):
    output.mkdir(parents=True, exist_ok=False)
    dotnet = os.environ.get('CHUNKMAESTRO_DOTNET', 'dotnet')
    result = {'schema_version': 1, 'platform': platform.system(), 'status': 'FAIL',
              'head': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
              'mechanism': 'new disposable 64 MiB image; real filesystem exhaustion; writes bounded at 128 MiB'}
    attached = False
    # Retain the image if detachment fails; never remove a live mounted fixture.
    temp = Path(tempfile.mkdtemp(prefix='chunkmaestro-full-volume-', dir='/private/tmp' if platform.system() == 'Darwin' else None))
    mount = temp / 'mount'
    mount.mkdir()
    image = temp / ('fixture.dmg' if platform.system() == 'Darwin' else 'fixture.vhd')
    result['fixture'] = str(temp)
    log = (output / 'commands.log').open('w')

    def call(args, env=None):
        code = subprocess.run(args, cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT).returncode
        log.flush()
        if code:
            raise RuntimeError(f'Command failed ({code}): {args[0]}')

    def diskpart(lines):
        script = temp / 'diskpart.txt'
        script.write_text('\n'.join(lines) + '\n', encoding='utf-8')
        call(['diskpart', '/s', str(script)])

    def image_attached(detach=False):
        # Query only the exact image created inside this fixture, never a physical disk.
        script = temp / 'image-state.ps1'
        script.write_text('param([string]$ImagePath, [switch]$Detach)\n$ErrorActionPreference = "Stop"\n'
                          '$image = Get-DiskImage -ImagePath $ImagePath\n'
                          'if ($Detach -and $image.Attached) { Dismount-DiskImage -ImagePath $ImagePath | Out-Null }\n'
                          '(Get-DiskImage -ImagePath $ImagePath).Attached | ConvertTo-Json -Compress\n', encoding='utf-8')
        state = subprocess.run(['powershell', '-NoProfile', '-NonInteractive', '-File', str(script),
                                '-ImagePath', str(image), *(['-Detach'] if detach else [])], cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        log.write(state.stdout + state.stderr)
        log.flush()
        if state.returncode:
            raise RuntimeError('Could not verify disposable image attachment state')
        value = json.loads(state.stdout.strip())
        if not isinstance(value, bool):
            raise RuntimeError('Unexpected disposable image attachment state')
        return value

    try:
        call([dotnet, 'restore', 'tooling/quality/storage/volume/VolumeTests.csproj', '--locked-mode'])
        call([dotnet, 'build', 'tooling/quality/storage/volume/VolumeTests.csproj', '-c', 'Release', '--no-restore'])
        if platform.system() == 'Darwin':
            call(['hdiutil', 'create', '-size', '64m', '-fs', 'HFS+', '-volname', 'ChunkMaestroDisposable', str(image)])
            attached = True
            call(['hdiutil', 'attach', '-nobrowse', '-mountpoint', str(mount), str(image)])
            attached = True
            if not os.path.ismount(mount):
                raise RuntimeError('Expected a new image mount')
        elif platform.system() == 'Windows':
            if image.exists():
                raise RuntimeError('Image must not exist')
            drives = subprocess.run(['powershell', '-NoProfile', '-NonInteractive', '-Command',
                                     '[Environment]::GetLogicalDrives() | ConvertTo-Json -Compress'],
                                    stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            if drives.returncode:
                raise RuntimeError('Could not enumerate occupied drive letters')
            letter = free_drive_letter(json.loads(drives.stdout))
            # Assignment fails if another process claims the letter; never remove an existing mapping.
            mount = Path(letter + ':\\')
            attached = True
            diskpart([f'create vdisk file="{image}" maximum=64 type=fixed',
                      f'select vdisk file="{image}"', 'attach vdisk', 'create partition primary',
                      'format fs=ntfs quick label=ChunkMaestroDisposable', f'assign letter={letter}'])
            attached = True
            query = subprocess.run(['mountvol', str(mount), '/L'], cwd=ROOT, stdout=subprocess.PIPE,
                                   stderr=subprocess.STDOUT, text=True)
            log.write(query.stdout)
            log.flush()
            volume = query.stdout.strip()
            if query.returncode or re.fullmatch(r'\\\\\?\\Volume\{[0-9a-fA-F-]{36}\}\\', volume) is None:
                raise RuntimeError('Expected the volume GUID of the newly mounted disposable image')
            # Keep a normal local drive path accepted by production, not the GUID namespace.
            result['volume_guid'] = volume
            result['drive_root'] = str(mount)
        else:
            raise RuntimeError('Unsupported controller platform; no disk action performed')
        env = dict(os.environ, CHUNKMAESTRO_DISPOSABLE_VOLUME=str(mount))
        call([dotnet, 'test', 'tooling/quality/storage/volume/VolumeTests.csproj', '-c', 'Release', '--no-build', '--no-restore',
              '--logger', 'trx', '--results-directory', str(output / 'tests')], env)
        result['status'] = 'PASS'
    except (RuntimeError, OSError, ValueError) as error:
        result['error'] = str(error)
    finally:
        try:
            if attached:
                if platform.system() == 'Darwin':
                    detach_mac_image(image, mount, log)
                else:
                    if image.exists() and image_attached(detach=True):
                        raise RuntimeError('Disposable image remains attached after detachment')
                attached = False
        except (RuntimeError, OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
            result['status'] = 'FAIL'
            result['cleanup_error'] = str(error)
        log.close()
        if not attached and platform.system() == 'Darwin':
            import shutil
            shutil.rmtree(temp)
        elif platform.system() == 'Windows':
            result['retained_fixture'] = str(temp)
        (output / 'volume.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result, indent=2))
    return result['status'] != 'PASS'


if __name__ == '__main__':
    sys.exit(run(Path(sys.argv[1]).resolve()))
