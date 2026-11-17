"""Explain locally collected status without reading/executing raw logs or reports."""
import json
import os
from pathlib import Path
import sys


def describe(folder):
    lines = ['## Storage run outcome', '', 'Fast coverage is not full safety qualification.', '']
    found = False
    for name in ('coverage-manifest.json', 'manifest.json', 'replay-collection.json', 'replay/result.json',
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
                lines.append(f'  - Retained raw log: `{log}` in the uploaded platform artifact. No timeout kill credit.')
    if not found:
        lines.append('**No runtime evidence produced.** Check the preceding setup/tooling step; no test success is claimed.')
    lines.extend(['', 'If qualification was not requested, add `storage-qualify` to the final candidate PR.',
                  'After another push, remove/re-add that label for the new head. Full evidence and independent QA still block merge.'])
    return '\n'.join(lines) + '\n'


if __name__ == '__main__':
    folder = Path(sys.argv[1])
    folder.mkdir(parents=True, exist_ok=True)
    message = describe(folder)
    (folder / 'outcome.md').write_text(message, encoding='utf-8')
    print(message)
    if os.environ.get('GITHUB_STEP_SUMMARY'):
        with open(os.environ['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as stream:
            stream.write(message)
