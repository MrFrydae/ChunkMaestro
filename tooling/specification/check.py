#!/usr/bin/env python3
"""Offline source/matrix integrity check. Python 3.9+, standard library only."""
import argparse
import hashlib
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DATA = Path('docs/product/requirements-data.json')
MATRIX = Path('docs/product/requirements-matrix.md')
SCOPES = {'required', 'constraint', 'conditional', 'inventory', 'unresolved', 'excluded'}
OWNED = [Path('docs/architecture'), Path('docs/security'), Path('docs/qa/specification-verification.md'),
         Path('docs/product/open-decisions.md'), Path('docs/product/capability-matrix.md'), MATRIX]


def render(data, root):
    header = (root / 'tooling/specification/matrix-header.txt').read_text()
    lines = []
    for r in data['requirements']:
        case = r['acceptance'].replace('|', '\\|')
        lines.append(f"| <a id=\"{r['id'].lower()}\"></a>[{r['id']}](requirements-source.md) L{r['source_line']} | "
                     f"{r['component']} | {r['milestone']} | {r['scope']} | **{r['acceptance_id']}**: {case} | "
                     f"{r['status']} | None |\n")
    return header + ''.join(lines)


def validate(root, write=False):
    data = json.loads((root / DATA).read_text())
    source = root / data['source']
    raw = source.read_bytes()
    source_text = raw.decode()
    errors = []
    def require(condition, message):
        if not condition:
            errors.append(message)
    require(data['schema_version'] == 1, 'unknown schema version')
    require(data['baseline'] == 'ab631d4', 'bootstrap baseline changed')
    require(hashlib.sha256(raw).hexdigest() == data['source_sha256'], 'source digest mismatch: review source changes explicitly')
    matches = list(re.finditer(r'^### (\d+)\. (.+)$', source_text, re.M))
    require([int(m[1]) for m in matches] == list(range(1, 206)), 'source must contain exactly Q1–205 in order')
    rows = data['requirements']
    require([r['id'] for r in rows] == [f'Q{i:03}' for i in range(1, 206)], 'matrix must contain exactly Q001–Q205 once in order')
    for i, r in enumerate(rows):
        qid = r['id']
        if i >= len(matches):
            break
        m = matches[i]
        end = matches[i+1].start() if i+1 < len(matches) else source_text.index('## Open decisions')
        block = source_text[m.start():end]
        require(block.count('**Answer:**') == 1, f'{qid}: expected one answer')
        answer = block.split('**Answer:**', 1)[-1].strip()
        require(bool(answer), f'{qid}: empty answer')
        require(hashlib.sha256(answer.encode()).hexdigest() == r['answer_sha256'], f'{qid}: answer digest mismatch')
        require(r['source_line'] == source_text[:m.start()].count('\n') + 1, f'{qid}: source line mismatch')
        require(r['question'] == m[2], f'{qid}: question mismatch')
        require(r['scope'] in SCOPES, f'{qid}: unknown scope')
        require(r['milestone'] in {f'M{x}' for x in range(8)} | {'none', 'post-v1'}, f'{qid}: unknown milestone')
        require(bool(r['component'].strip()), f'{qid}: missing component')
        require(r['acceptance_id'] == f'AC-{qid}' and len(r['acceptance']) > 60, f'{qid}: missing concrete acceptance')
        require(r['status'] == 'not-implemented' and r['evidence'] == [], f'{qid}: bootstrap cannot claim implementation or product evidence')
    desired = render(data, root)
    if write and not errors:
        with (root / MATRIX).open('w', encoding='utf-8', newline='\n') as stream:
            stream.write(desired)
    require((root / MATRIX).is_file() and (root / MATRIX).read_text() == desired, 'matrix rendering stale; use --render')
    files = []
    for item in OWNED:
        path = root / item
        if path.is_dir():
            files.extend(path.rglob('*.md'))
        else:
            require(path.is_file(), f'missing document {item}')
            if path.is_file():
                files.append(path)
    # Validate relative Markdown targets and explicit/GitHub-style heading fragments in owned docs only.
    # Authoritative source has an absent original attachment; it is intentionally not rewritten or counted.
    for path in files:
        content = path.read_text()
        for target in re.findall(r'(?<!!)\[[^\]]*\]\(([^)]+)\)', content):
            if re.match(r'[a-zA-Z][a-zA-Z0-9+.-]*:', target):
                continue
            dest, _, fragment = target.partition('#')
            resolved = (path.parent / dest).resolve() if dest else path
            require(resolved.is_file(), f'{path.relative_to(root)}: missing link {target}')
            if fragment and resolved.is_file():
                body = resolved.read_text()
                anchors = set(re.findall(r'id="([^"]+)"', body))
                for heading in re.findall(r'^#+ (.+)$', body, re.M):
                    slug = re.sub(r'[^\w\- ]', '', heading.lower()).replace(' ', '-')
                    anchors.add(slug)
                require(fragment in anchors, f'{path.relative_to(root)}: missing fragment {target}')
    decisions = (root / 'docs/product/open-decisions.md').read_text()
    require(re.findall(r'^## OD-(\d\d) ', decisions, re.M) == [f'{i:02}' for i in range(1, 13)], 'expected twelve open decisions')
    adrs = sorted((root / 'docs/architecture/adr').glob('*.md'))
    require(len(adrs) == 13, 'expected thirteen boundary ADRs')
    for path in adrs:
        body = path.read_text()
        for section in ['Status: Proposed', 'Source:', 'Decision', 'Alternatives', 'Consequences', 'Failure and recovery', 'Test strategy']:
            require(section in body, f'{path.name}: missing {section}')
    return errors


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--render', action='store_true', help='regenerate matrix after validating source/data')
    args = parser.parse_args()
    try:
        errors = validate(ROOT, args.render)
    except (KeyError, ValueError, OSError, IndexError, TypeError) as exc:
        print(f'FAIL: invalid specification input: {exc}', file=sys.stderr)
        return 1
    if errors:
        print('\n'.join('FAIL: ' + error for error in errors), file=sys.stderr)
        return 1
    print('PASS: 205 source IDs/answers, 205 matrix rows/cases, digests, bootstrap status/evidence, rendered matrix, local links, 12 decisions, 13 ADRs')
    return 0


if __name__ == '__main__':
    sys.exit(main())
