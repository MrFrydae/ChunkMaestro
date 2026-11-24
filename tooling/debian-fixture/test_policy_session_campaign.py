"""Offline host boundary probes. No VM, setup, provision or service activation."""
import base64
import contextlib
import copy
import hashlib
import io
import json
import signal
from pathlib import Path
from types import SimpleNamespace
import sys
import tempfile
import unittest
from unittest.mock import patch

import fixture as f


def frame(obj, prefix=b'CM14-EVIDENCE:'):
    return prefix + base64.b64encode(json.dumps(obj).encode()) + b'\n'


def model():
    c = f.Campaign.__new__(f.Campaign)
    c.nonce, c.digest, c.commit = 'a' * 32, 'b' * 64, 'c' * 40
    c.template = {'schema': 1, 'nonce': c.nonce, 'source_sha256': c.digest,
                  'image_sha512': f.PIN['sha512'], 'packages': {'systemd': '257'}, 'kernel_release': 'test'}
    c.records, c.exit_record, c.parse_error = [], None, None
    c.ssh = c.cleaned = False
    rows = [['worker-' + 'e' * 32, 1, 0, 5, 101, 0, 0, 'anon_inode:[pidfd]', 2],
            ['agent-' + 'e' * 32, 1, 0, 5, 102, 0, 0, 'anon_inode:[pidfd]', 2]]
    def phase(name, count=2):
        return {'phase': name, 'raw_dump': rows[:count]}
    raw = [phase('ARMED'), dict(phase('FULL_REJECTED'), barrier='processed'), phase('SENDER_REJECTED'),
           dict(phase('RECOVERED'), hup={'worker': False, 'agent': False}, recovery=True),
           dict(phase('RECOVERED'), hup={'worker': True, 'agent': True}, recovery=True), rows,
           {'holder': {'ActiveState': 'inactive', 'LoadState': 'loaded'}, 'raw_dump': rows},
           dict(phase('RECOVERED'), hup={'worker': True, 'agent': True}, recovery=True),
           {'starts': '1', 'invocation': 'd' * 32},
           {'partial': {'phase': 'PARTIAL', 'raw_dump': rows[1:]},
            'recovered': {'phase': 'RECOVERED', 'raw_dump': rows[1:], 'recovery': True}},
           phase('EMPTY', 0), phase('EMPTY', 0)]
    report = {'kind': 'PROTOCOL_FIXTURE_ONLY', 'authorization': dict(c.template, boot_id='12345678-1234-1234-1234-123456789012'),
        'cases': {k: {'status': 'PASS', 'raw': v} for k, v in zip(f.CASE_NAMES, raw)},
        'gaps': dict.fromkeys(f.GAP_NAMES, 'NOT RUN'), 'primary_failure': None, 'cleanup_failures': [],
        'qualification': 'INCOMPLETE', 'command_containment': {'status': 'UNKNOWN',
            'scope': 'initial-process-group-only', 'required_fallback': 'outer-disposable-VM-destroy-and-reap'},
        'campaign_completed': True, 'retirement_verified': True, 'journal_snapshot': {'phase': 'RETIRED', 'epoch': 'e' * 32, 'worker_id': 'd' * 32,
            'expected': {row[0]: row[1:7] + [row[8]] for row in rows}}}
    return c, report


def serial(c, report, code=2):
    return b''.join(frame({'schema': 1, 'stage': stage, 'report': report}) for stage in f.STAGES) + frame(
        {'schema': 1, 'nonce': c.nonce, 'source_sha256': c.digest, 'exit': code}, b'CM14-EXIT:')


class CampaignContracts(unittest.TestCase):
    def test_mode_authorization_before_prerequisites(self):
        for args in (['--policy-session-campaign'], ['--execute-owner-reviewed-fixture', '--policy-session-campaign'],
                     ['--execute-owner-reviewed-fixture', '--reviewed-source-commit', 'a' * 40],
                     ['--campaign-preflight', '--execute-owner-reviewed-fixture']):
            with self.subTest(args=args), patch.object(sys, 'argv', ['fixture.py'] + args), patch.object(f, 'prerequisites') as pre:
                with self.assertRaises(f.Failure):
                    f.main()
                pre.assert_not_called()

    def test_missing_manifest_and_exact_commit_digest_denied(self):
        for args in ((None, None, None), ('missing', 'b' * 64, 'short')):
            with self.assertRaises(f.Failure):
                f.Campaign(*args)
        with tempfile.NamedTemporaryFile() as stream:
            raw = b'{"commit":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}'
            stream.write(raw); stream.flush()
            with self.assertRaisesRegex(f.Failure, 'manifest-binding'):
                f.Campaign(stream.name, 'b' * 64, 'a' * 40)
            with self.assertRaisesRegex(f.Failure, 'commit-binding'):
                f.Campaign(stream.name, hashlib.sha256(raw).hexdigest(), 'c' * 40)

    def test_real_uncommitted_payload_is_not_reviewed_activation(self):
        module = f.policy_prepare()
        from common import SOURCE_FILES
        root = f.HERE.parent / 'policy-session-fixture'
        raw = json.dumps({'schema': 1, 'commit': 'd6a6805ee5d1aa5e10b87722f24b47317729deb6',
            'files': {n: hashlib.sha256((root / n).read_bytes()).hexdigest() for n in SOURCE_FILES}}).encode()
        with tempfile.NamedTemporaryFile() as stream:
            stream.write(raw); stream.flush()
            with self.assertRaisesRegex(Exception, 'reviewed-commit-bytes'):
                f.Campaign(stream.name, hashlib.sha256(raw).hexdigest(), json.loads(raw)['commit'])

    def test_manifest_construction_cannot_authorize_wrong_digest(self):
        with patch.object(f.subprocess, 'run', return_value=SimpleNamespace(returncode=0, stdout=b'fixed')):
            with self.assertRaisesRegex(f.Failure, 'manifest-binding'):
                f.reviewed_manifest('a' * 40, 'b' * 64)

    def test_pass_requires_both_receipts_smoke_and_outer_cleanup(self):
        c, r = model(); c.capture(serial(c, r))
        self.assertFalse(c.protocol_pass())
        c.ssh = True
        self.assertFalse(c.protocol_pass())
        c.cleaned = True
        self.assertTrue(c.protocol_pass())

    def test_exit_two_never_blanket_pass(self):
        c, r = model(); r['cases'][f.CASE_NAMES[-1]] = {'status': 'NOT RUN'}
        c.capture(serial(c, r)); c.ssh = c.cleaned = True
        self.assertFalse(c.protocol_pass())

    def test_each_raw_case_is_required_and_assessed(self):
        for name in f.CASE_NAMES:
            c, r = model(); r['cases'][name]['raw'] = {'invented': 'PASS'}
            c.capture(serial(c, r)); c.ssh = c.cleaned = True
            self.assertFalse(c.protocol_pass(), name)

    def test_wrong_nonce_source_image_template_and_boot_refused(self):
        for field in ('nonce', 'source_sha256', 'image_sha512', 'packages', 'kernel_release', 'boot_id'):
            c, r = model(); r['authorization'][field] = 'wrong'
            with self.subTest(field=field), self.assertRaises(f.Failure):
                c.capture(serial(c, r))

    def test_missing_extra_duplicate_cases_and_duplicate_json_members(self):
        for change in ('missing', 'extra'):
            c, r = model()
            if change == 'missing': del r['cases'][f.CASE_NAMES[0]]
            else: r['cases']['extra'] = {'status': 'NOT RUN'}
            with self.assertRaises(f.Failure): c.capture(serial(c, r))
        with self.assertRaisesRegex(f.Failure, 'duplicate-member'):
            f.strict_json(b'{"case":1,"case":2}')

    def test_caps_depth_encoding_schema_and_truncation(self):
        c, r = model()
        for data in (b'x' * (1024**2 + 1), b'CM14-EVIDENCE:!!!!\n',
                     frame({'schema': True}), frame({'schema': 1, 'nested': [[[[[]]]]]})[:-2] + b'!\n'):
            with self.assertRaises(f.Failure): c.capture(data)
        with self.assertRaises(f.Failure): f.strict_json('[' * 18 + '0' + ']' * 18)
        c.capture(serial(c, r)[:-1]); self.assertIsNone(c.exit_record)

    def test_receipts_are_stage_bound_no_last_json_selection(self):
        c, r = model()
        before = frame({'schema': 1, 'stage': f.STAGES[0], 'report': r})
        after = frame({'schema': 1, 'stage': f.STAGES[1], 'report': r})
        for data in (after, before + before, before + after + after):
            with self.assertRaises(f.Failure): c.capture(data)
        c.capture(before); c.ssh = c.cleaned = True
        self.assertFalse(c.protocol_pass())

    def test_restoration_errors_and_all_guest_failure_exits_remain_incomplete(self):
        for code in (1, 2, 3, -2, -15):
            c, r = model(); r['primary_failure'] = 'command-exit'; r['cleanup_failures'] = ['signal-restoration-cancelled']
            c.capture(serial(c, r, code)); c.ssh = c.cleaned = True
            self.assertFalse(c.protocol_pass())
            self.assertEqual(c.records[0]['report']['primary_failure'], 'command-exit')

    def test_missing_outputs_are_unresolved_and_do_not_export_serial(self):
        c, r = model(); c.capture(b'PRIVATE_KEY_SENTINEL\n{"qualification":"PASS"}\n')
        with tempfile.TemporaryDirectory() as d:
            c.persist(d, f.Failure('campaign-exit-missing'))
            raw = (Path(d) / 'campaign-summary.json').read_text()
            self.assertNotIn('PRIVATE_KEY_SENTINEL', raw)
            self.assertEqual(json.loads(raw)['synthetic_protocol'], 'UNRESOLVED')
            self.assertEqual(json.loads(raw)['qualification'], 'INCOMPLETE')

    def test_guest_receipt_and_collision_export_are_current_memory_only(self):
        module = f.policy_prepare()
        import importlib.util
        spec = importlib.util.spec_from_file_location('cm14_guest', f.HERE.parent / 'policy-session-fixture/guest.py')
        guest = importlib.util.module_from_spec(spec); spec.loader.exec_module(guest)
        c, r = model(); r['primary_failure'] = 'occupied-fixture'; r['cases'] = {}
        with patch.object(Path, 'read_bytes', side_effect=AssertionError('no unknown file')), contextlib.redirect_stdout(io.StringIO()) as output:
            guest.receipt(r, f.STAGES[0])
        value = json.loads(output.getvalue())
        exported = json.loads(base64.b64decode(value['cm14_evidence']))
        self.assertEqual(exported['report']['primary_failure'], 'occupied-fixture')
        self.assertEqual(exported['report']['cases'], {})

    def test_cleanup_failure_and_interrupt_preserve_first_host_failure(self):
        for primary in (f.Failure('probe-exit'), KeyboardInterrupt()):
            c, r = model(); c.capture(serial(c, r)); c.ssh = True
            with tempfile.TemporaryDirectory() as d:
                root = Path(d) / 'workspace'; root.mkdir()
                args = ['fixture.py', '--execute-owner-reviewed-fixture', '--policy-session-campaign',
                    '--campaign-acknowledge', f.CAMPAIGN_ACK, '--campaign-evidence', d]
                Path(d).chmod(0o700)
                with patch.object(sys, 'argv', args), patch.object(f, 'Campaign', return_value=c), \
                     patch.object(f, 'prerequisites'), patch.object(f.ctypes, 'CDLL') as libc, \
                     patch.object(f.tempfile, 'mkdtemp', return_value=str(root)), \
                     patch.object(f.shutil, 'disk_usage', return_value=SimpleNamespace(free=11 * 1024**3)), \
                     patch.object(f.signal, 'signal'), patch.object(f.signal, 'alarm'), \
                     patch.object(f, 'exercise', side_effect=primary), \
                     patch.object(f.Harness, 'cleanup', side_effect=f.Failure('cleanup-descendants-live')), \
                     patch.object(f.shutil, 'rmtree') as delete, contextlib.redirect_stdout(io.StringIO()):
                    libc.return_value.prctl.return_value = 0
                    with self.assertRaises(BaseException) as caught: f.main()
                self.assertIs(caught.exception, primary); delete.assert_not_called()
                summary = json.loads((Path(d) / 'campaign-summary.json').read_text())
                self.assertEqual(summary['synthetic_protocol'], 'UNRESOLVED')
                self.assertFalse(summary['outer_cleanup_confirmed'])

    def test_real_guest_receipts_round_trip_and_oversize_keeps_first_cause(self):
        f.policy_prepare()
        import guest
        c, r = model()
        with contextlib.redirect_stdout(io.StringIO()) as output:
            for stage in f.STAGES: guest.receipt(r, stage)
        data = output.getvalue().encode() + frame({'schema': 1, 'nonce': c.nonce,
            'source_sha256': c.digest, 'exit': 2}, b'CM14-EXIT:')
        c.capture(data); c.ssh = c.cleaned = True
        self.assertTrue(c.protocol_pass())
        wrapper = json.loads(output.getvalue().splitlines()[0]); wrapper['receipt_stage'] = f.STAGES[1]
        with self.assertRaisesRegex(f.Failure, 'receipt-binding'):
            c.capture(json.dumps(wrapper).encode() + b'\n')
        c, r = model(); r['primary_failure'] = 'command-exit'; r['kernel'] = 'x' * 200000
        with contextlib.redirect_stdout(io.StringIO()) as output: guest.receipt(r, f.STAGES[0])
        receipt = json.loads(output.getvalue())
        self.assertEqual(receipt['primary_failure'], 'command-exit')
        self.assertIsNone(receipt['cm14_evidence'])
        self.assertIn('publication-serial-evidence-limit', receipt['cleanup_failures'])

    def test_default_smoke_functions_are_identical_to_prepared_head(self):
        import ast
        import subprocess
        before = subprocess.check_output(['git', 'show', 'HEAD:tooling/debian-fixture/fixture.py']).decode()
        after = (f.HERE / 'fixture.py').read_text()
        def nodes(text):
            return {n.name: ast.dump(n, include_attributes=False) for n in ast.parse(text).body if isinstance(n, ast.FunctionDef)}
        for name in ('cloud_config', 'ssh_args', 'wait_ready', 'validate_provenance', 'prerequisites'):
            self.assertEqual(nodes(before)[name], nodes(after)[name])
        old = {}; exec(compile(before, 'baseline', 'exec'), {'__file__': str(f.HERE / 'fixture.py'), '__name__': 'baseline'}, old)
        self.assertEqual(old['SMOKE'], f.SMOKE); self.assertEqual(old['EXPECTED'], f.EXPECTED)

    def test_actual_campaign_payload_and_firmware_reach_fixed_qemu_builder(self):
        f.policy_prepare()
        from common import SOURCE_FILES
        payload = f.HERE.parent / 'policy-session-fixture'
        binding = json.dumps({'schema': 1, 'commit': 'a' * 40,
            'files': {n: hashlib.sha256((payload / n).read_bytes()).hexdigest() for n in SOURCE_FILES}}).encode()
        import subprocess
        def committed(argv, **kwargs):
            name = argv[-1].split(':tooling/policy-session-fixture/')[1]
            return subprocess.CompletedProcess(argv, 0, (payload / name).read_bytes())
        with tempfile.TemporaryDirectory() as d:
            root = Path(d); manifest = root / 'external.json'; manifest.write_bytes(binding)
            with patch.object(f.subprocess, 'run', side_effect=committed):
                campaign = f.Campaign(manifest, hashlib.sha256(binding).hexdigest(), 'a' * 40)
                h = f.Harness(root)
                def download(name, *args): (root / name).write_bytes(b'base')
                def run(args, phase, seconds=30):
                    if phase == 'disk-info': return b'{"format":"qcow2","virtual-size":1024}'
                    if phase == 'keys':
                        (root / args[-1]).write_text('synthetic-private')
                        (root / (args[-1] + '.pub')).write_text('ssh-ed25519 synthetic')
                    return b''
                with patch.object(h, 'download', side_effect=download), patch.object(h, 'run', side_effect=run), \
                     patch.object(h, 'start', side_effect=f.Failure('stop-before-vm')) as start, \
                     patch.object(f, 'validate_provenance'), patch.dict(f.PIN, sha512=hashlib.sha512(b'base').hexdigest()), \
                     contextlib.redirect_stdout(io.StringIO()):
                    with self.assertRaisesRegex(f.Failure, 'stop-before-vm'): f.exercise(h, campaign)
                argv = start.call_args.args[0]
                self.assertEqual(argv.count('-fw_cfg'), 4)
                self.assertIn('name=opt/cm14/nonce,string=' + campaign.nonce, argv)
                config = json.loads((root / 'user-data').read_text().split('\n', 1)[1])
                self.assertFalse(config['users'][0]['sudo'])
                self.assertEqual(config['runcmd'][0], ['/usr/bin/python3', '-I', '/opt/cm14-fixture/provision.py'])
                self.assertFalse(config['package_update'])

    def test_artifact_failure_cannot_replace_primary(self):
        c, r = model(); c.capture(serial(c, r))
        primary = f.Failure('probe-exit')
        with tempfile.TemporaryDirectory() as d:
            root = Path(d) / 'workspace'; root.mkdir()
            Path(d).chmod(0o700)
            args = ['fixture.py', '--execute-owner-reviewed-fixture', '--policy-session-campaign',
                '--campaign-acknowledge', f.CAMPAIGN_ACK, '--campaign-evidence', d]
            with patch.object(sys, 'argv', args), patch.object(f, 'Campaign', return_value=c), \
                 patch.object(f, 'prerequisites'), patch.object(f.ctypes, 'CDLL') as libc, \
                 patch.object(f.tempfile, 'mkdtemp', return_value=str(root)), \
                 patch.object(f.shutil, 'disk_usage', return_value=SimpleNamespace(free=11 * 1024**3)), \
                 patch.object(f.signal, 'signal'), patch.object(f.signal, 'alarm'), \
                 patch.object(f, 'exercise', side_effect=primary), patch.object(f.Harness, 'cleanup'), \
                 patch.object(c, 'persist', side_effect=OSError('PRIVATE')), contextlib.redirect_stdout(io.StringIO()) as log:
                libc.return_value.prctl.return_value = 0
                with self.assertRaises(f.Failure) as caught: f.main()
            self.assertIs(caught.exception, primary)
            self.assertNotIn('PRIVATE', log.getvalue())

    def test_workflow_campaign_has_distinct_manual_owner_authority(self):
        text = (f.HERE.parents[1] / '.github/workflows/debian-fixture.yml').read_text()
        smoke, campaign = text.split('  policy-session:', 1)
        self.assertNotIn('--policy-session-campaign', smoke)
        self.assertIn("github.actor_id == '38875853'", campaign)
        self.assertIn("github.event_name == 'workflow_dispatch'", campaign)
        self.assertIn('inputs.reviewed_source_commit == github.sha', campaign)
        self.assertNotIn("github.event.label.name", campaign)
        self.assertLess(campaign.index('Bind external review authority'), campaign.index('runner_setup.py setup'))
        self.assertNotIn('campaign.json', campaign)

    def test_raw_pidfd_tuple_identity_type_and_epoch_fail_closed(self):
        for mutation in ('duplicate', 'duplicate-with-flags', 'type', 'epoch', 'journal-epoch',
                         'bool', 'negative', 'inode-one', 'inode-overflow', 'flags'):
            with self.subTest(mutation=mutation):
                c, r = model()
                rows = r['cases'][f.CASE_NAMES[0]]['raw']['raw_dump']
                if mutation.startswith('duplicate'):
                    rows[1][2:5] = rows[0][2:5]
                    if mutation.endswith('flags'): rows[1][8] |= 0o4000
                elif mutation == 'type': rows[0][7] = rows[1][7] = 'regular-file'
                elif mutation == 'epoch': rows[1][0] = 'agent-' + 'f' * 32
                elif mutation == 'journal-epoch': r['journal_snapshot']['epoch'] = 'f' * 32
                elif mutation == 'bool': rows[0][2] = True
                elif mutation == 'negative': rows[0][3] = -1
                elif mutation == 'inode-one': rows[0][4] = 1
                elif mutation == 'inode-overflow': rows[0][4] = 2**64
                else: rows[0][8] = 2 | 0o200
                # Corrupt correlated records too; journal equality alone is insufficient.
                r['journal_snapshot']['expected'] = {row[0]: row[1:7] + [row[8]] for row in rows}
                c.capture(serial(c, r)); c.ssh = c.cleaned = True
                self.assertFalse(c.protocol_pass())
        c, r = model(); rows = r['cases'][f.CASE_NAMES[0]]['raw']['raw_dump']; rows[1][8] |= 0o4000
        r['journal_snapshot']['expected'] = {row[0]: row[1:7] + [row[8]] for row in rows}
        c.capture(serial(c, r)); c.ssh = c.cleaned = True
        self.assertTrue(c.protocol_pass())

    def test_final_capture_refuses_recognized_partial_frames_and_allows_live_fragmentation(self):
        for tail in (b'CM14-EVIDENCE:truncated', b'CM14-EXIT:truncated', b'{"cm14_evidence":"partial'):
            c, r = model(); data = serial(c, r) + tail
            c.capture(data); c.ssh = c.cleaned = True
            self.assertFalse(c.protocol_pass())
            with self.assertRaisesRegex(f.Failure, 'campaign-frame-truncated'):
                c.capture(data, final=True)
        c, r = model(); data = serial(c, r)
        c.capture(data[:-10]); c.ssh = c.cleaned = True
        self.assertFalse(c.protocol_pass())
        c.capture(data, final=True); self.assertTrue(c.protocol_pass())
        c.capture(data + b'ordinary console noise', final=True)
        self.assertTrue(c.protocol_pass())

    def test_later_pidfd_and_journal_numeric_types_are_not_coerced(self):
        def replacements(value):
            return [float(value)] + ([bool(value)] if value in (0, 1) else [])
        # Copies must not mutate the valid ARMED originals and hide the later-row gap.
        locations = [(index, None) for index in (1, 2, 3, 4, 5, 6, 7)] + [(9, 'partial'), (9, 'recovered')]
        def rows(raw, index, part):
            return raw if index == 5 else raw[part]['raw_dump'] if part else raw['raw_dump']
        for index, part in locations:
            _, baseline = model()
            original = baseline['cases'][f.CASE_NAMES[index]]['raw']
            for row_index, row in enumerate(rows(original, index, part)):
                for column in (1, 2, 3, 4, 5, 6, 8):
                    for replacement in replacements(row[column]):
                        with self.subTest(case=index, part=part, row=row_index, column=column,
                                          kind=type(replacement).__name__):
                            c, r = model()
                            raw = copy.deepcopy(r['cases'][f.CASE_NAMES[index]]['raw'])
                            rows(raw, index, part)[row_index][column] = replacement
                            r['cases'][f.CASE_NAMES[index]]['raw'] = raw
                            c.capture(serial(c, r), final=True); c.ssh = c.cleaned = True
                            self.assertFalse(c.protocol_pass())
        _, baseline = model()
        for name, identity in baseline['journal_snapshot']['expected'].items():
            for column, value in enumerate(identity):
                for replacement in replacements(value):
                    with self.subTest(journal_role=name, column=column, kind=type(replacement).__name__):
                        c, r = model(); r['journal_snapshot']['expected'][name][column] = replacement
                        c.capture(serial(c, r), final=True); c.ssh = c.cleaned = True
                        self.assertFalse(c.protocol_pass())
        for index, dead in ((3, False), (4, True), (7, True)):
            for role in ('worker', 'agent'):
                for replacement in (int(dead), float(dead)):
                    with self.subTest(hup_case=index, role=role, kind=type(replacement).__name__):
                        c, r = model(); r['cases'][f.CASE_NAMES[index]]['raw']['hup'][role] = replacement
                        c.capture(serial(c, r), final=True); c.ssh = c.cleaned = True
                        self.assertFalse(c.protocol_pass())
        c, r = model(); c.capture(serial(c, r), final=True); c.ssh = c.cleaned = True
        self.assertTrue(c.protocol_pass())

    def test_cleanup_validates_last_serial_bytes_after_reaping_before_closing(self):
        for tail in (b'', b'CM14-EVIDENCE:truncated', b'x' * (1024**2 + 1)):
            c, r = model()
            with tempfile.TemporaryDirectory() as d, tempfile.TemporaryFile() as output:
                output.write(serial(c, r)); output.flush(); h = f.Harness(Path(d))
                def wait(timeout):
                    output.write(tail); output.flush()
                process = SimpleNamespace(pid=424242, poll=lambda: 0, wait=wait)
                h.children = [(process, output)]; h.campaign_output = (c, output)
                reaped = []
                def reap(*args):
                    reaped.append(True)
                    raise ChildProcessError()
                original = c.capture
                def capture(data, final=False):
                    self.assertTrue(reaped); self.assertFalse(output.closed); self.assertTrue(final)
                    return original(data, final=final)
                with patch.object(f.os, 'killpg'), patch.object(f.os, 'waitpid', side_effect=reap), \
                     patch.object(f.time, 'sleep'), patch.object(c, 'capture', side_effect=capture), \
                     contextlib.redirect_stdout(io.StringIO()):
                    if tail:
                        with self.assertRaises(f.Failure): h.cleanup()
                    else: h.cleanup()
                self.assertTrue(output.closed)

    def test_finalization_signals_preserve_first_cause_and_never_publish_pass(self):
        for stage in ('cleanup', 'deletion', 'persist-before', 'persist-after',
                      'completion-before', 'completion-after'):
            for first in (signal.SIGINT, signal.SIGTERM):
                for earlier_failure in (False, True):
                    with self.subTest(stage=stage, signal=first, earlier_failure=earlier_failure):
                        c, r = model(); c.capture(serial(c, r)); c.ssh = True
                        primary = f.Failure('probe-exit') if earlier_failure else None
                        handlers = {}; delivered = []; errors = []; phases = []
                        def register(number, handler):
                            previous = handlers.get(number, signal.SIG_DFL)
                            handlers[number] = handler
                            return previous
                        def deliver():
                            for number in (first, signal.SIGTERM if first == signal.SIGINT else signal.SIGINT):
                                handler = handlers[number]; delivered.append(number)
                                if handler == signal.SIG_IGN: continue
                                self.assertTrue(callable(handler))
                                try: handler(number, None)
                                except BaseException as error: errors.append(error)
                            if errors: raise errors[0]
                        with tempfile.TemporaryDirectory() as d:
                            root = Path(d) / 'workspace'; root.mkdir(); Path(d).chmod(0o700)
                            original_delete = f.shutil.rmtree; original_persist = c.persist
                            def cleanup(h):
                                phases.append('cleanup')
                                if stage == 'cleanup': deliver()
                            def delete(path):
                                phases.append('deletion')
                                if stage == 'deletion': deliver()
                                original_delete(path)
                            def persist(directory, cause):
                                phases.append('persist')
                                if stage == 'persist-before': deliver()
                                original_persist(directory, cause)
                                if stage == 'persist-after': deliver()
                            actual_print = print
                            def publish(*args, **kwargs):
                                completion = args and '"phase": "campaign-complete"' in str(args[0])
                                if completion and stage == 'completion-before': deliver()
                                actual_print(*args, **kwargs)
                                if completion and stage == 'completion-after': deliver()
                            args = ['fixture.py', '--execute-owner-reviewed-fixture', '--policy-session-campaign',
                                '--campaign-acknowledge', f.CAMPAIGN_ACK, '--campaign-evidence', d]
                            with patch.object(sys, 'argv', args), patch.object(f, 'Campaign', return_value=c), \
                                 patch.object(f, 'prerequisites'), patch.object(f.ctypes, 'CDLL') as libc, \
                                 patch.object(f.tempfile, 'mkdtemp', return_value=str(root)), \
                                 patch.object(f.shutil, 'disk_usage', return_value=SimpleNamespace(free=11 * 1024**3)), \
                                 patch.object(f.signal, 'signal', side_effect=register), patch.object(f.signal, 'alarm'), \
                                 patch.object(f.os, 'umask'), patch.object(f, 'exercise', side_effect=primary), \
                                 patch.object(f.Harness, 'cleanup', cleanup), patch.object(f.shutil, 'rmtree', side_effect=delete), \
                                 patch.object(c, 'persist', side_effect=persist), patch('builtins.print', side_effect=publish), \
                                 contextlib.redirect_stdout(io.StringIO()) as log:
                                libc.return_value.prctl.return_value = 0
                                with self.assertRaises(f.Failure) as caught: f.main()
                            self.assertEqual(phases, ['cleanup', 'deletion', 'persist'])
                            if primary is not None: self.assertIs(caught.exception, primary)
                            else: self.assertEqual(str(caught.exception), 'interrupted')
                            if not earlier_failure or not stage.startswith('completion'):
                                self.assertEqual(len(delivered), 2)
                            if errors: self.assertTrue(all(error is errors[0] for error in errors))
                            self.assertNotIn('"synthetic_protocol": "PASS"', log.getvalue())
                            self.assertFalse(c.protocol_pass() if delivered else False)
                            self.assertEqual(json.loads((Path(d) / 'campaign-summary.json').read_text())['qualification'], 'INCOMPLETE')


if __name__ == '__main__':
    unittest.main()
