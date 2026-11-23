"""Create ONLY a new bounded disposable image, exercise it, detach ONLY that image."""
import hashlib
import json
import os
from pathlib import Path
import platform
import plistlib
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

from command import child_environment

ROOT = Path(__file__).resolve().parents[2]
TEST_CLASS = 'ChunkMaestro.PersistenceTests.VolumeTests'
TEST_METHOD = 'ActualFullVolumeRejectsBackupAndEfMigrationWithoutLosingSource'
MAX_TRX_BYTES = 4 * 1024 * 1024
INPUTS = ('tooling/ci/volume.py', 'tooling/quality/storage/volume/VolumeTests.cs',
          'tooling/quality/storage/volume/VolumeTests.csproj', 'tooling/quality/storage/volume/packages.lock.json')


def input_hashes(root=ROOT):
    return {name: hashlib.sha256((root / name).read_bytes()).hexdigest() for name in INPUTS}


def executed_case(folder):
    reports = list((folder / 'tests').glob('*.trx'))
    if len(reports) != 1:
        raise ValueError('Expected exactly one retained full-volume TRX')
    report = reports[0].resolve()
    if not report.is_relative_to(folder.resolve()):
        raise ValueError('Full-volume TRX escapes evidence directory')
    with report.open('rb') as stream:
        raw = stream.read(MAX_TRX_BYTES + 1)
    if len(raw) > MAX_TRX_BYTES:
        raise ValueError('Full-volume TRX exceeds evidence size bound')
    document = ET.fromstring(raw)
    counters = document.findall('.//{*}Counters')
    results = document.findall('.//{*}UnitTestResult')
    definitions = document.findall('.//{*}UnitTest')
    if (len(counters) != 1 or len(results) != 1 or len(definitions) != 1 or
            any(counters[0].get(key) != '1' for key in ('total', 'executed', 'passed')) or
            any(counters[0].get(key) != '0' for key in
                ('failed', 'error', 'timeout', 'aborted', 'inconclusive', 'notExecuted')) or
            any(value != '0' for key, value in counters[0].attrib.items()
                if key not in ('total', 'executed', 'passed'))):
        raise ValueError('Full-volume execution is missing, failing, skipped or ambiguous')
    result, definition = results[0], definitions[0]
    methods = definition.findall('./{*}TestMethod')
    expected = TEST_CLASS + '.' + TEST_METHOD
    if (result.get('outcome') != 'Passed' or result.get('testName') != expected or
            not result.get('testId') or result.get('testId') != definition.get('id') or
            definition.get('name') != expected or len(methods) != 1 or
            methods[0].get('className') != TEST_CLASS or methods[0].get('name') != TEST_METHOD):
        raise ValueError('Full-volume result does not bind the required executed test')
    return {'trx': report.relative_to(folder.resolve()).as_posix(),
            'sha256': hashlib.sha256(raw).hexdigest(), 'case': expected, 'passed': 1}


def validate_evidence(folder, head, system, root=ROOT):
    result = json.loads((folder / 'volume.json').read_bytes())
    if (result.get('schema_version') != 2 or result.get('status') != 'PASS' or
            result.get('head') != head or result.get('platform') != system or
            result.get('inputs') != input_hashes(root) or result.get('execution') != executed_case(folder)):
        raise ValueError('Full-volume execution evidence failed or differs from current inputs')
    return result


def windows_image_binding(image, mount, expected_volume, log, env):
    """Prove the selected drive belongs to this newly created image before writes."""
    script = image.parent / 'verify-image.ps1'
    script.write_text('param([string]$ImagePath, [string]$DriveLetter)\n'
        '$ErrorActionPreference = "Stop"\n'
        '$image = Get-DiskImage -ImagePath $ImagePath\n'
        '$disks = @($image | Get-Disk)\n'
        'if (!$image.Attached -or $disks.Count -ne 1) { throw "Fixture image is not uniquely attached" }\n'
        '$parts = @($disks[0] | Get-Partition | Where-Object { $_.DriveLetter -eq $DriveLetter })\n'
        'if ($parts.Count -ne 1) { throw "Fixture drive does not belong to the image" }\n'
        '$volumes = @($parts[0] | Get-Volume)\n'
        'if ($volumes.Count -ne 1) { throw "Fixture volume is not unique" }\n'
        '@{ image_path=$image.ImagePath; disk_number=$disks[0].Number; '
        'drive_root=([string]$parts[0].DriveLetter + ":\\"); volume_guid=$volumes[0].UniqueId } '
        '| ConvertTo-Json -Compress\n', encoding='utf-8')
    state = subprocess.run(['powershell', '-NoProfile', '-NonInteractive', '-File', str(script),
                            '-ImagePath', str(image), '-DriveLetter', str(mount)[0]],
                           env=env, timeout=30, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    log.write(state.stdout + state.stderr)
    log.flush()
    if state.returncode:
        raise RuntimeError('Could not bind the disposable drive to its image')
    binding = json.loads(state.stdout)
    if (not isinstance(binding, dict) or
            str(binding.get('image_path', '')).casefold() != str(image.resolve()).casefold() or
            str(binding.get('drive_root', '')).casefold() != str(mount).casefold() or
            str(binding.get('volume_guid', '')).casefold() != expected_volume.casefold() or
            type(binding.get('disk_number')) is not int or binding['disk_number'] < 0):
        raise RuntimeError('Disposable image/partition/volume binding differs')
    return binding


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
                        stderr=subprocess.PIPE, timeout=30, env=child_environment(os.environ))
        if state.returncode:
            raise RuntimeError('Could not inspect disposable image attachment')
        # Do not log unrelated mounted image paths from the host-wide inventory.
        return mac_image_device(plistlib.loads(state.stdout), image, mount)

    def detach(args):
        result = execute(args, stdout=log, stderr=subprocess.STDOUT, timeout=30,
                         env=child_environment(os.environ))
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
    tool_env = child_environment(os.environ)
    result = {'schema_version': 2, 'platform': platform.system(), 'status': 'FAIL', 'inputs': input_hashes(),
              'head': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT,
                                             env=tool_env, timeout=30, text=True).strip(),
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
        print('[disk-full] START ' + str(args[0]) + '; deadline=120s; log=' + str(output / 'commands.log'), flush=True)
        code = subprocess.run(args, cwd=ROOT, env=tool_env if env is None else env,
                              stdout=log, stderr=subprocess.STDOUT, timeout=120).returncode
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
                                '-ImagePath', str(image), *(['-Detach'] if detach else [])], cwd=ROOT,
                               env=tool_env, timeout=30, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
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
                                    env=tool_env, timeout=30, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
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
                                   env=tool_env, timeout=30, stderr=subprocess.STDOUT, text=True)
            log.write(query.stdout)
            log.flush()
            volume = query.stdout.strip()
            if query.returncode or re.fullmatch(r'\\\\\?\\Volume\{[0-9a-fA-F-]{36}\}\\', volume) is None:
                raise RuntimeError('Expected the volume GUID of the newly mounted disposable image')
            # Keep a normal local drive path accepted by production, not the GUID namespace.
            result['volume_guid'] = volume
            result['drive_root'] = str(mount)
            result['image_binding'] = windows_image_binding(image, mount, volume, log, tool_env)
        else:
            raise RuntimeError('Unsupported controller platform; no disk action performed')
        env = dict(tool_env, CHUNKMAESTRO_DISPOSABLE_VOLUME=str(mount))
        call([dotnet, 'test', 'tooling/quality/storage/volume/VolumeTests.csproj', '-c', 'Release', '--no-build', '--no-restore',
              '--logger', 'trx', '--results-directory', str(output / 'tests')], env)
        result['execution'] = executed_case(output)
        result['status'] = 'PASS'
    except (RuntimeError, OSError, ValueError, ET.ParseError, subprocess.SubprocessError) as error:
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
