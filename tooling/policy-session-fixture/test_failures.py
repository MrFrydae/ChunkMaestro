"""Offline injected faults and bounded self-terminating processes; zero native proof."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

import common as c
import guest
import prepare


def manifest(root):
    raw = json.dumps({'schema': 1, 'commit': 'a' * 40,
        'files': {name: hashlib.sha256((root / name).read_bytes()).hexdigest()
                  for name in c.SOURCE_FILES}}, sort_keys=True).encode()
    return raw, hashlib.sha256(raw).hexdigest()


class Failures(unittest.TestCase):
    def test_existing_state_and_symlink_preserve_sentinel(self):
        for symlink in (False, True):
            with self.subTest(symlink=symlink), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                original = root / 'original'
                original.mkdir()
                (original / 'campaign.json').write_bytes(b'ORIGINAL')
                state = root / 'state'
                if symlink:
                    state.symlink_to(original, target_is_directory=True)
                else:
                    state = original
                owner = c.OwnedState(state)
                with self.assertRaises(FileExistsError):
                    owner.acquire()
                with self.assertRaises(c.Failure):
                    owner.publish({'replacement': True})
                self.assertEqual((original / 'campaign.json').read_bytes(), b'ORIGINAL')

    def test_state_swap_never_follows_replacement(self):
        with tempfile.TemporaryDirectory() as tmp:
            state = Path(tmp) / 'state'
            owner = c.OwnedState(state)
            owner.acquire()
            moved = Path(tmp) / 'moved'
            state.rename(moved)
            state.symlink_to(moved)
            try:
                with self.assertRaises(c.Failure):
                    owner.publish({})
                self.assertFalse((moved / 'campaign.json').exists())
            finally:
                owner.close()

    def test_external_manifest_bytes_membership_and_secret(self):
        root = Path(__file__).parent
        raw, digest = manifest(root)
        c.bound_payload(root, raw, digest)  # Explicit synthetic authority, NOT review of repository.
        with tempfile.TemporaryDirectory() as tmp:
            copied = Path(tmp)
            for name in c.SOURCE_FILES:
                path = copied / name
                path.parent.mkdir(exist_ok=True)
                path.write_bytes((root / name).read_bytes())
            for kind in ('secret', 'symlink', 'fifo', 'mutation'):
                with self.subTest(kind=kind):
                    extra = copied / '.ignored-secret'
                    if kind == 'secret':
                        extra.write_text('SYNTHETIC-NOT-A-SECRET')
                    elif kind == 'symlink':
                        extra.symlink_to(root / 'baseline.json')
                    elif kind == 'fifo':
                        os.mkfifo(extra)
                    else:
                        (copied / 'baseline.json').write_text('{}')
                    with self.assertRaises(c.Failure):
                        c.bound_payload(copied, raw, digest)
                    if kind != 'mutation':
                        extra.unlink()
        with self.assertRaises(c.Failure):
            c.bound_payload(root, raw, '0' * 64)

    def test_uncommitted_candidate_never_review_authority(self):
        root = Path(__file__).parent
        raw, digest = manifest(root)
        config = '#cloud-config\n' + json.dumps({'package_update': False,
            'package_upgrade': False, 'write_files': [], 'runcmd': []})
        with self.assertRaisesRegex(c.Failure, 'trusted-source-binding-required'):
            prepare.prepare(config, 'a' * 32)
        with tempfile.NamedTemporaryFile() as binding:
            binding.write(raw)
            binding.flush()
            with self.assertRaisesRegex(c.Failure, 'reviewed-commit-bytes'):
                prepare.prepare(config, 'a' * 32, binding.name, digest)

    def test_generic_command_rejected_before_spawn(self):
        with patch.object(c.subprocess, 'Popen') as spawn:
            with self.assertRaisesRegex(c.Failure, 'fixed-command-required'):
                c.run_raw((sys.executable, '-c', 'pass'))
            spawn.assert_not_called()
        self.assertEqual(c.CONTAINMENT['status'], 'UNKNOWN')

    def test_escaped_child_demonstrates_group_limit(self):
        with tempfile.TemporaryDirectory() as tmp:
            marker = Path(tmp) / 'done'
            ready = Path(tmp) / 'ready'
            code = ('import os,time,pathlib\n'
                'pid=os.fork()\nif pid==0:\n os.setsid()\n'
                f' pathlib.Path({str(ready)!r}).touch()\n time.sleep(0.2)\n'
                f' pathlib.Path({str(marker)!r}).touch()\n os._exit(0)\n'
                f'end=time.monotonic()+1\nwhile not pathlib.Path({str(ready)!r}).exists() and time.monotonic()<end: time.sleep(0.005)\n'
                'os._exit(0)')
            c._run_process((sys.executable, '-B', '-c', code))
            self.assertFalse(marker.exists())
            deadline = time.monotonic() + 2
            while not marker.exists() and time.monotonic() < deadline:
                time.sleep(0.02)
            self.assertTrue(marker.exists())
            time.sleep(0.05)  # child has no more work and self-exits; no daemon.
            self.assertEqual(c.CONTAINMENT['status'], 'UNKNOWN')

    def test_exit7_and_wait_failure_keep_primary(self):
        with patch.object(c.subprocess.Popen, 'wait', side_effect=TimeoutError('private-cleanup')):
            with self.assertRaises(c.Failure) as raised:
                c._run_process((sys.executable, '-B', '-c', 'print("private");raise SystemExit(7)'))
        self.assertEqual(str(raised.exception), 'command-exit')
        self.assertIn('leader-reap-unknown', raised.exception.secondary)
        self.assertNotIn('private', str(raised.exception.secondary))

    def test_partial_positive_write_completes(self):
        with tempfile.TemporaryDirectory() as tmp, patch.object(c, 'STATE', Path(tmp)):
            real = os.write
            with patch.object(c.os, 'write', side_effect=lambda fd, data: real(fd, data[:1])):
                c.write('journal', {'phase': 'ARMED'})
            self.assertEqual(json.loads((Path(tmp) / 'journal').read_bytes()), {'phase': 'ARMED'})

    def test_before_publish_faults_retain_old_bytes(self):
        for fault in ('zero', 'negative', 'enospc', 'fsync', 'close', 'rename', 'cancel'):
            with self.subTest(fault=fault), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                (root / 'journal').write_bytes(b'OLD')
                real_close = os.close
                calls = []
                def close(fd):
                    calls.append(fd)
                    real_close(fd)
                    if len(calls) == 1:
                        raise OSError('injected-close-after-release')
                target, kwargs = {
                    'zero': ('write', {'return_value': 0}),
                    'negative': ('write', {'return_value': -1}),
                    'enospc': ('write', {'side_effect': OSError(28, 'synthetic ENOSPC')}),
                    'fsync': ('fsync', {'side_effect': OSError('synthetic sync')}),
                    'close': ('close', {'side_effect': close}),
                    'rename': ('replace', {'side_effect': OSError('synthetic rename')}),
                    'cancel': ('write', {'side_effect': KeyboardInterrupt()}),
                }[fault]
                with patch.object(c, 'STATE', root), patch.object(c.os, target, **kwargs):
                    with self.assertRaises(c.Failure):
                        c.write('journal', {'phase': 'ARMED'})
                self.assertEqual((root / 'journal').read_bytes(), b'OLD')
                self.assertTrue((root / 'journal.new').exists())
                if fault == 'close':
                    self.assertEqual(len(calls), 2)  # temp then directory; no retry of temp close.

    def test_postrename_sync_failure_preserves_ambiguous_new_record(self):
        with tempfile.TemporaryDirectory() as tmp, patch.object(c, 'STATE', Path(tmp)):
            (Path(tmp) / 'journal').write_bytes(b'OLD')
            real = os.fsync
            count = 0
            def sync(fd):
                nonlocal count
                count += 1
                if count == 2:
                    raise OSError('injected-directory-sync')
                real(fd)
            with patch.object(c.os, 'fsync', side_effect=sync):
                with self.assertRaisesRegex(c.Failure, 'publish-durability-unknown'):
                    c.write('journal', {'phase': 'RETIRED'})
            self.assertEqual(json.loads((Path(tmp) / 'journal').read_bytes())['phase'], 'RETIRED')

    def test_publication_never_masks_primary_or_cancellation(self):
        for error in (OSError('private'), KeyboardInterrupt()):
            with patch.object(c, 'write', side_effect=error):
                secondary = c.publish_failure('result', {'phase': 'FAIL', 'category': 'first'})
            self.assertTrue(secondary[0].startswith('publication-'))
            report = {'primary_failure': 'first', 'cleanup_failures': [],
                      'campaign_completed': False, 'retirement_verified': False}
            guest.first_failure(report, error, publication=True)
            self.assertEqual(report['primary_failure'], 'first')
            self.assertFalse(guest.may_remove(report))

    def test_cancel_before_during_cleanup_and_publication_retains_escrow(self):
        for stage in ('before', 'cleanup', 'publication'):
            report = {'primary_failure': None, 'cleanup_failures': [],
                'campaign_completed': stage != 'before', 'retirement_verified': stage != 'before'}
            cancellation = guest.Cancellation(report)
            with patch.object(guest, 'ctl') as effects, patch.object(guest.signal, 'signal'):
                with self.assertRaises(KeyboardInterrupt):
                    cancellation.handle(2, None)
                cancellation.handle(15, None)
                cancellation.handle(2, None)
                self.assertFalse(guest.may_remove(report))
                self.assertEqual(report['primary_failure'], 'cancelled')
                effects.assert_not_called()

    def test_actual_finalizer_retains_anchor_units_on_injected_cancellation(self):
        from unittest.mock import Mock
        for stage in ('before', 'cleanup', 'publication'):
            with self.subTest(stage=stage):
                report = {'primary_failure': None, 'cleanup_failures': [], 'cases': {},
                    'campaign_completed': stage != 'before', 'retirement_verified': stage != 'before'}
                owner = Mock(fd=12)
                cancellation = Mock()
                dest = Mock()
                if stage == 'before':
                    guest.first_failure(report, KeyboardInterrupt())
                if stage == 'publication':
                    owner.publish.side_effect = KeyboardInterrupt()
                def stop(action, unit):
                    if stage == 'cleanup':
                        raise KeyboardInterrupt()
                with patch.object(guest, 'ctl', side_effect=stop) as effects, \
                     patch.object(guest, 'properties', return_value={'MainPID': '0'}), \
                     patch.object(guest, 'run_raw') as raw, patch('builtins.print'):
                    guest.finalize(report, [dest], True, owner, cancellation)
                dest.unlink.assert_not_called()
                self.assertNotIn(unittest.mock.call('stop', c.UNITS[3]), effects.call_args_list)
                raw.assert_not_called()
                self.assertEqual(report['primary_failure'], 'cancelled')
                self.assertEqual(len(report['cases']), 12)
                self.assertTrue(all(case['status'] == 'NOT RUN' for case in report['cases'].values()))

    def test_finalizer_receipt_precedes_first_signal_during_restoration(self):
        from unittest.mock import Mock
        for signum in (guest.signal.SIGINT, guest.signal.SIGTERM):
            for interrupt_at in (1, 2):
                for publication_failure in (False, True):
                    with self.subTest(signum=signum, interrupt_at=interrupt_at,
                                      publication_failure=publication_failure):
                        report = {'primary_failure': 'command-exit', 'cleanup_failures': [],
                            'cases': {}, 'campaign_completed': False, 'retirement_verified': False}
                        owner = Mock(fd=12)
                        if publication_failure:
                            owner.publish.side_effect = OSError('PRIVATE-publication')
                        cancellation = guest.Cancellation(report)
                        cancellation.previous = {guest.signal.SIGINT: guest.signal.SIG_DFL,
                                                 guest.signal.SIGTERM: guest.signal.SIG_DFL}
                        trace = []
                        receipts = []
                        restored = []
                        def output(raw, **kwargs):
                            trace.append('receipt')
                            receipts.append(json.loads(raw))
                            self.assertTrue(kwargs.get('flush'))
                        def restore_signal(sig, handler):
                            trace.append('restore')
                            restored.append(sig)
                            if len(restored) == interrupt_at:
                                cancellation.handle(signum, None)
                        with patch.object(guest.signal, 'signal', side_effect=restore_signal), \
                             patch('builtins.print', side_effect=output), \
                             patch.object(guest, 'ctl') as effects:
                            guest.finalize(report, [], False, owner, cancellation)
                        self.assertEqual(trace[0], 'receipt')
                        self.assertEqual(restored, [guest.signal.SIGINT, guest.signal.SIGTERM])
                        self.assertEqual(receipts[0]['primary_failure'], 'command-exit')
                        self.assertEqual(receipts[-1]['primary_failure'], 'command-exit')
                        self.assertIn('signal-restoration-cancelled', receipts[-1]['cleanup_failures'])
                        self.assertTrue(all(r['qualification'] == 'INCOMPLETE' for r in receipts))
                        self.assertEqual(report['primary_failure'], 'command-exit')
                        self.assertFalse(report['campaign_completed'])
                        self.assertEqual(owner.publish.call_count, 2)
                        effects.assert_not_called()
                        self.assertNotIn('PRIVATE', json.dumps(receipts))
                        if publication_failure:
                            self.assertEqual(sum(e.startswith('publication-') for e in
                                                 receipts[0]['cleanup_failures']), 2)

    def test_restoration_api_error_preserves_receipt_and_attempts_remaining_handlers(self):
        from unittest.mock import Mock
        report = {'primary_failure': 'command-exit', 'cleanup_failures': [],
            'cases': {}, 'campaign_completed': False, 'retirement_verified': False}
        owner = Mock(fd=12)
        owner.publish.side_effect = OSError('PRIVATE-publication')
        cancellation = guest.Cancellation(report)
        cancellation.previous = {guest.signal.SIGINT: guest.signal.SIG_DFL,
                                 guest.signal.SIGTERM: guest.signal.SIG_DFL}
        with patch.object(guest.signal, 'signal', side_effect=[OSError('PRIVATE-restore'), None]) as restore, \
             patch('builtins.print') as output:
            guest.finalize(report, [], False, owner, cancellation)
        self.assertEqual(restore.call_count, 2)
        receipts = [json.loads(call.args[0]) for call in output.call_args_list]
        self.assertEqual(len(receipts), 2)
        self.assertEqual(receipts[0]['receipt_stage'], 'before-handler-restoration')
        self.assertEqual(receipts[-1]['primary_failure'], 'command-exit')
        self.assertIn('signal-restoration-runtime-unavailable', receipts[-1]['cleanup_failures'])
        self.assertNotIn('PRIVATE', json.dumps(receipts))

    def test_completed_requires_all_cases_and_retirement(self):
        report = {'primary_failure': None, 'cleanup_failures': [],
                  'campaign_completed': False, 'retirement_verified': False}
        self.assertFalse(guest.may_remove(report))
        report['campaign_completed'] = True
        self.assertFalse(guest.may_remove(report))
        report['retirement_verified'] = True
        self.assertTrue(guest.may_remove(report))
