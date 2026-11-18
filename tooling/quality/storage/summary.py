"""Explain locally collected status without reading/executing raw logs or reports."""
import json
import os
from pathlib import Path
import argparse


def describe(folder):
    lines = ['## Storage run outcome', '', 'Fast coverage is not full safety qualification.', '']
    found = False
    for name in ('coverage-manifest.json', 'manifest.json', 'bundle.json', 'probe.json', 'replay-collection.json', 'replay/result.json',
                 'volume/volume.json', 'native/native.json'):
        path = folder / name
        if not path.is_file():
            continue
        found = True
        report = json.loads(path.read_bytes())
        lines.append(f'- `{name}`: **{report.get("status", "UNKNOWN")}**')
        if report.get('error'):
            # Producer errors are operational descriptions, never raw log bodies.
            message = str(report['error']).replace('\n', ' ')[:1000]
            lines.append('  - Reason: ' + message.replace('<', '&lt;').replace('>', '&gt;'))
        records = report.get('commands', [])
        if name == 'replay/result.json':
            ledger = folder / 'replay/commands.jsonl'
            records = [json.loads(line) for line in ledger.read_text(encoding='utf-8').splitlines()] if ledger.is_file() else []
        for record in records:
            if record.get('timeout') or record.get('exit_code') not in (0, 2):
                log = str(record.get('log', 'unknown')).replace('\\', '/').rsplit('/', 1)[-1]
                lines.append(f'  - Stopped: `{record.get("timeout_reason", "process-error")}`; '
                             f'exit `{record.get("exit_code")}`; elapsed `{record.get("seconds", "unknown")}` seconds; '
                             f'last observed log update `{record.get("last_log_update_age_seconds", "unknown")}` seconds ago.')
                lines.append(f'  - Retained raw log: `{log}` in the retained platform evidence. No timeout kill credit.')
    if not found:
        lines.append('**No runtime evidence produced.** Check the preceding setup/tooling step; no test success is claimed.')
    if (folder / 'bundle.json').is_file() or (folder / 'probe.json').is_file():
        lines.append('**Shard pilot only:** catalogue planning or a partial diagnostic cannot satisfy full qualification.')
    lines.extend(['', 'Storage qualification runs locally; see docs/qa/local-storage-qualification.md.',
                  'Retain raw evidence and obtain exact-head lead attestation before merge.'])
    return '\n'.join(lines) + '\n'


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('folder', type=Path)
    parser.add_argument('--artifact-directory', type=Path,
                        help='Write the human summary outside an immutable sealed bundle')
    args = parser.parse_args()
    folder = args.folder
    folder.mkdir(parents=True, exist_ok=True)
    message = describe(folder)
    destination = args.artifact_directory or folder
    destination.mkdir(parents=True, exist_ok=True)
    (destination / 'outcome.md').write_text(message, encoding='utf-8')
    print(message)
    if os.environ.get('GITHUB_STEP_SUMMARY'):
        with open(os.environ['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as stream:
            stream.write(message)
