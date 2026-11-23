"""Bounded raw report loading and exact artifact inventory checks."""
import hashlib
import json
from pathlib import Path


def read_json(path, limit=16 * 1024 * 1024):
    with path.open('rb') as stream:
        raw = stream.read(limit + 1)
    if len(raw) > limit:
        raise ValueError('Report exceeds size bound: ' + path.name)
    return json.loads(raw)


def inventory(folder, exclude=()):
    result = {}
    for path in sorted(folder.rglob('*')):
        if path.is_symlink() or not path.resolve().is_relative_to(folder.resolve()):
            raise ValueError('Artifact path escapes its directory')
        if path.is_file():
            name = path.relative_to(folder).as_posix()
            if name not in exclude:
                with path.open('rb') as stream:
                    result[name] = hashlib.file_digest(stream, 'sha256').hexdigest()
    return result


def verify_inventory(folder, expected, exclude=()):
    if not isinstance(expected, dict) or not expected:
        raise ValueError('Missing raw artifact inventory')
    actual = inventory(folder, exclude)
    if set(actual) != set(expected):
        raise ValueError('Raw artifact inventory has missing or unrecorded files')
    if actual != expected:
        raise ValueError('Raw artifact changed after execution')
