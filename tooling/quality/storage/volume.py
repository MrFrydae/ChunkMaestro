"""Create ONLY a new bounded disposable image, exercise it, detach ONLY that image."""
import json
import os
from pathlib import Path
import platform
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[3]


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
        script.write_text('\n'.join(lines) + '\n')
        call(['diskpart', '/s', str(script)])

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
            # All destructive commands operate on the vdisk just created by this script.
            attached = True
            diskpart([f'create vdisk file="{image}" maximum=64 type=fixed',
                      f'select vdisk file="{image}"', 'attach vdisk', 'create partition primary',
                      'format fs=ntfs quick label=ChunkMaestroDisposable', f'assign mount="{mount}"'])
            attached = True
            call(['mountvol', str(mount), '/L'])
        else:
            raise RuntimeError('Unsupported controller platform; no disk action performed')
        env = dict(os.environ, CHUNKMAESTRO_DISPOSABLE_VOLUME=str(mount))
        call([dotnet, 'test', 'tooling/quality/storage/volume/VolumeTests.csproj', '-c', 'Release', '--no-build', '--no-restore',
              '--logger', 'trx', '--results-directory', str(output / 'tests')], env)
        result['status'] = 'PASS'
    except (RuntimeError, OSError) as error:
        result['error'] = str(error)
    finally:
        try:
            if attached:
                if platform.system() == 'Darwin':
                    call(['hdiutil', 'detach', str(mount)])
                else:
                    diskpart([f'select vdisk file="{image}"', 'detach vdisk'])
                attached = False
        except (RuntimeError, OSError) as error:
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
