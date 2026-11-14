#!/usr/bin/env python3
"""Validate repo-local agent packages. Python 3.9+, standard library only.

Metadata intentionally uses a small safe YAML subset: quoted JSON scalar values,
flat frontmatter, and two-level UI/policy mappings. No YAML execution or dependency.
"""
import argparse
import json
from pathlib import Path
import re
import sys
from urllib.parse import unquote, urlsplit

ROOT = Path(__file__).resolve().parents[2]
NAME = re.compile(r'[a-z0-9]+(?:-[a-z0-9]+)*\Z')
LINK = re.compile(r'(?<!!)\[[^\]]*\]\(([^)]+)\)')


def quoted_value(value, context):
    try:
        result = json.loads(value)
    except ValueError as error:
        raise ValueError(f'{context}: use a quoted scalar value') from error
    if not isinstance(result, str) or not result.strip():
        raise ValueError(f'{context}: nonempty text required')
    return result


def frontmatter(text, context):
    match = re.match(r'\A---\n(.*?)\n---\n', text, re.S)
    if not match:
        raise ValueError(f'{context}: missing YAML frontmatter')
    data = {}
    for line in match[1].splitlines():
        field = re.fullmatch(r'(name|description): (.+)', line)
        if not field or field[1] in data:
            raise ValueError(f'{context}: duplicate or unsupported frontmatter field')
        data[field[1]] = quoted_value(field[2], context)
    if set(data) != {'name', 'description'}:
        raise ValueError(f'{context}: name and description required')
    return data


def ui_metadata(text, context):
    data = {}
    section = None
    for line in text.splitlines():
        if not line.strip() or line.lstrip().startswith('#'):
            continue
        parent = re.fullmatch(r'(interface|policy):', line)
        if parent:
            section = parent[1]
            if section in data:
                raise ValueError(f'{context}: duplicate metadata section')
            data[section] = {}
            continue
        field = re.fullmatch(r'  ([a-z_]+): (.+)', line)
        if not field or section is None or field[1] in data[section]:
            raise ValueError(f'{context}: invalid or duplicate metadata field')
        if section == 'policy':
            if field[1] != 'allow_implicit_invocation' or field[2] not in {'true', 'false'}:
                raise ValueError(f'{context}: invocation policy must be a boolean')
            data[section][field[1]] = field[2] == 'true'
        else:
            if field[1] not in {'display_name', 'short_description', 'default_prompt'}:
                raise ValueError(f'{context}: unsupported interface field')
            data[section][field[1]] = quoted_value(field[2], context)
    if set(data.get('interface', {})) != {'display_name', 'short_description', 'default_prompt'}:
        raise ValueError(f'{context}: display name, description and prompt required')
    return data


def local_links(path, text, root):
    errors = []
    for target in LINK.findall(text):
        target = target.removeprefix('<').removesuffix('>')
        try:
            parsed = urlsplit(target)
        except ValueError:
            errors.append(f'{path.relative_to(root)}: malformed link')
            continue
        if parsed.scheme in {'https', 'http', 'mailto'}:
            continue
        if parsed.scheme or parsed.netloc or Path(unquote(parsed.path)).is_absolute():
            errors.append(f'{path.relative_to(root)}: nonportable link {target}')
            continue
        if not parsed.path:
            continue
        destination = (path.parent / unquote(parsed.path)).resolve()
        try:
            destination.relative_to(root)
        except ValueError:
            errors.append(f'{path.relative_to(root)}: link outside repository {target}')
            continue
        if not destination.is_file():
            errors.append(f'{path.relative_to(root)}: missing link {target}')
    return errors


def validate(root):
    root = root.resolve()
    errors = []
    skills = root / '.agents/skills'
    if (root / '.agents').is_symlink() or skills.is_symlink():
        return ['Skill root must not be a symlink.']
    packages = sorted(skills.iterdir()) if skills.is_dir() else []
    if not packages:
        return ['No project skills found.']
    routing_path = root / 'AGENTS.md'
    try:
        routing = routing_path.read_text(encoding='utf-8')
    except (OSError, UnicodeError) as error:
        return [f'Cannot read AGENTS.md: {type(error).__name__}']
    errors.extend(local_links(routing_path, routing, root))
    registered = set(re.findall(r'\.agents/skills/([^/]+)/SKILL\.md', routing))
    names = set()
    for package in packages:
        context = package.relative_to(root).as_posix()
        if package.is_symlink() or not package.is_dir():
            errors.append(f'{context}: skill must be a regular directory')
            continue
        if any(path.is_symlink() for path in package.rglob('*')):
            errors.append(f'{context}: symlink resource is not allowed')
            continue
        try:
            text = (package / 'SKILL.md').read_text(encoding='utf-8')
            header = frontmatter(text, context)
            name = header['name']
            if not NAME.fullmatch(name) or len(name) > 64 or name != package.name:
                errors.append(f'{context}: invalid or mismatched skill name')
            if name in names:
                errors.append(f'{context}: duplicate skill name')
            names.add(name)
            if len(header['description']) > 1024:
                errors.append(f'{context}: description too long')
            if package.name not in registered:
                errors.append(f'{context}: missing AGENTS routing')
            if re.search(r'\[TODO[:\]]|\[INSERT\b', text, re.I):
                errors.append(f'{context}: unfinished scaffold')
            if re.search(r'SCHOOLCAL_|SchoolSync|/Users/[^\s`]+', text):
                errors.append(f'{context}: foreign or machine-specific instruction')
            metadata = ui_metadata((package / 'agents/openai.yaml').read_text(encoding='utf-8'), context)
            ui = metadata['interface']
            if not 25 <= len(ui['short_description']) <= 64:
                errors.append(f'{context}: short description must be 25–64 characters')
            if name not in re.findall(r'\$([a-z0-9-]+)', ui['default_prompt']):
                errors.append(f'{context}: default prompt names the wrong skill')
            for path in package.rglob('*.md'):
                errors.extend(local_links(path, path.read_text(encoding='utf-8'), root))
        except (OSError, UnicodeError, ValueError) as error:
            errors.append(f'{context}: {error}')
    actual = {package.name for package in packages if package.is_dir()}
    for stale in sorted(registered - actual):
        errors.append(f'AGENTS.md: missing skill {stale}')
    return errors


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=ROOT)
    args = parser.parse_args()
    errors = validate(args.root)
    if errors:
        for error in errors:
            print('FAIL:', error, file=sys.stderr)
        return 1
    print('PASS: project skill metadata, routing, local links and resource paths')
    return 0


if __name__ == '__main__':
    sys.exit(main())
