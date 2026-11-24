"""Offline contracts and a separate Linux-only self-context integration test."""
import errno
import io
import json
import os
from pathlib import Path
import random
import shutil
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from unittest.mock import patch

import collector as c


def fixtures():
    status = ''.join(key + ':\t' + value + '\n' for key, value in {
        'Name': 'private-name', 'Uid': '1000 1000 1000 1000', 'Gid': '1000 1000 1000 1000',
        'Groups': '1000 1001', 'NoNewPrivs': '1', 'Seccomp': '2', 'Seccomp_filters': '1',
        **{key: '0000000000000000' for key in c.CAPS}}.items()).encode()
    return {'boot': b'12345678-1234-1234-1234-123456789abc\n', 'release': b'6.12.115-test\n',
            'status': status, 'stat': b'42 (private-name) S ' + b'0 ' * 18 + b'123 ' + b'0 ' * 29 + b'0\n',
            'userNamespace': b'user:[123]', 'mountNamespace': b'mnt:[456]',
            'uidMap': b'0 0 4294967295\n', 'gidMap': b'0 0 4294967295\n',
            'mounts': b'25 1 8:1 /private-root /private-target rw,nosuid shared:1 - ext4 /private-source rw,secret=value\n',
            'lsm': b'capability,apparmor\n', 'subject': b'private-domain (enforce)\n'}


class Reader:
    def __init__(self, data=None):
        self.data = fixtures() if data is None else data
        self.calls = []

    def read(self, source, limit=c.BLOB_LIMIT + 1):
        self.calls.append(source)
        value = self.data[source]
        if isinstance(value, BaseException):
            raise value
        return value[:limit]


def run(reader=None, **kwargs):
    return c.collect(reader or Reader(), 'offline-1', '0' * 64, fixture=True, pid=42, **kwargs)


class Parsers(unittest.TestCase):
    def test_selected_fields_and_no_private_text(self):
        report = run()
        self.assertEqual(report['collectionCompletion'], 'COMPLETE')
        self.assertEqual(report['domain'], 'OFFLINE_FIXTURE')
        self.assertEqual(report['selfIdentity'], {'pid': 42, 'startTicks': 123})
        self.assertEqual(report['namespaceBinding'], {'userNamespace': 123, 'mountNamespace': 456})
        expected_bytes = 2 * sum(len(data) for data in fixtures().values())
        self.assertEqual(expected_bytes, report['readBytes'])
        self.assertEqual(expected_bytes, report['readBytesKnown'])
        self.assertEqual(expected_bytes, report['readBudgetBytes'])
        output = c.encode(report)
        for private in (b'private-name', b'private-root', b'private-target', b'private-source', b'secret=value', b'private-domain'):
            self.assertNotIn(private, output)
        for evidence in report['evidence']:
            self.assertEqual(evidence['evidenceClass'], 'supplied')
            self.assertEqual(len(evidence['sha256']), 64)
            self.assertLessEqual(evidence['monotonicStart'], evidence['monotonicEnd'])

    def test_status_missing_duplicate_and_types(self):
        original = fixtures()['status']
        for value in (original + b'Uid: 0 0 0 0\n', original.replace(b'NoNewPrivs:\t1', b'NoNewPrivs:\t2'),
                      original.replace(b'Seccomp_filters:\t1\n', b''), original.replace(b'1000 1001', b'-1'),
                      original.replace(b'CapEff:\t0000000000000000', b'CapEff:\txyz')):
            with self.subTest(value=value), self.assertRaises((ValueError, KeyError)):
                c.parse('status', value)

    def test_unknown_claim_is_discarded(self):
        data = fixtures()
        data['status'] += b'qualified: true\nproductPromotion: true\n'
        report = run(Reader(data))
        self.assertFalse(report['productPromotion'])
        self.assertEqual(report['runtimeQualification'], 'UNRESOLVED')
        self.assertNotIn('qualified', report['evidence'][2]['value'])
        self.assertEqual(set(report['remainingProofGaps']), set(c.GAPS))

    def test_mapping_overlaps_overflow_duplicate_and_empty(self):
        for data in (b'0 0 1\n0 1 1\n', b'0 0 1\n1 0 1\n', b'0 0 0\n', b'4294967295 0 2\n', b'\n'):
            with self.subTest(data=data), self.assertRaises(ValueError):
                c.parse('map', data)

    def test_fixed_text_malformed_duplicate_and_long(self):
        for kind, data in [('uuid', b'not-uuid'), ('user', b'mnt:[123]'), ('release', b'kernel\nextra\n'),
                           ('lsm', b'apparmor,apparmor'), ('subject', b'x\x00'), ('stat', b'42 (x) S 0\n'),
                           ('release', b'x' * 8193), ('lsm', b'x' * (c.BLOB_LIMIT + 1))]:
            with self.subTest(kind=kind), self.assertRaises(ValueError):
                c.parse(kind, data)

    def test_mount_duplicate_and_malformed(self):
        good = fixtures()['mounts']
        for data in (good * 2, good.replace(b' - ', b' '), good.replace(b'8:1', b'invalid'),
                     good.replace(b'rw,nosuid', b'rw,rw'), good.replace(b'/private-root', b'relative'), good * 129):
            with self.subTest(data=data[:40]), self.assertRaises(ValueError):
                c.parse('mounts', data)

    def test_bounded_random_inputs_never_escape_parser_taxonomy(self):
        rng = random.Random(14)
        for kind in {kind for _, kind in c.SOURCES.values()}:
            for _ in range(50):
                data = bytes(rng.randrange(256) for _ in range(rng.randrange(300)))
                try:
                    c.parse(kind, data)
                except (ValueError, KeyError, UnicodeError):
                    pass


class CollectionFailures(unittest.TestCase):
    def test_failed_partial_reads_remain_within_total_budget(self):
        actual = 0

        class BrokenStream:
            def __enter__(self):
                self.calls = 0
                return self

            def __exit__(self, *args):
                return False

            def read(self, count):
                nonlocal actual
                self.calls += 1
                if self.calls == 64:
                    raise OSError(errno.EIO, 'failed after partial transfer')
                data = b'x' * min(count, 1024)
                actual += len(data)
                return data

        def namespace(path):
            nonlocal actual
            value = 'user:[123]' if path.endswith('/user') else 'mnt:[456]'
            actual += len(value.encode('ascii'))
            return value

        with patch('builtins.open', side_effect=lambda *args, **kwargs: BrokenStream()), \
                patch.object(c.os, 'readlink', side_effect=namespace):
            report = c.collect(c.LinuxSelfReader(), 'partial-budget', '0' * 64,
                               fixture=True, pid=42, clock=lambda: 0)
        self.assertEqual('INCOMPLETE', report['collectionCompletion'])
        self.assertLessEqual(actual, c.TOTAL_LIMIT)
        self.assertLessEqual(actual, report['readBudgetBytes'])
        self.assertLessEqual(report['readBudgetBytes'], c.TOTAL_LIMIT)
        self.assertIsNone(report['readBytes'])
        self.assertFalse(report['productPromotion'])

    def test_mount_access_flags_must_be_consistent(self):
        for options in (b'ro,rw,nosuid', b'nosuid'):
            data = fixtures()
            data['mounts'] = data['mounts'].replace(b'rw,nosuid', options)
            with self.subTest(options=options):
                report = run(Reader(data))
                self.assertEqual('MALFORMED', report['evidence'][8]['status'])
                self.assertEqual('INCOMPLETE', report['collectionCompletion'])
                self.assertEqual('UNRESOLVED', report['runtimeQualification'])
        for options in (b'ro,nosuid', b'rw,nosuid'):
            data = fixtures()
            data['mounts'] = data['mounts'].replace(b'rw,nosuid', options)
            with self.subTest(valid=options):
                self.assertEqual('COMPLETE', run(Reader(data))['collectionCompletion'])

    def test_namespace_query_requires_its_full_byte_reservation_before_io(self):
        budget = c.Budget(clock=lambda: 0)
        budget.charge(c.TOTAL_LIMIT - 8)
        with patch.object(c.os, 'readlink', side_effect=AssertionError('namespace read must not start')):
            record = c.sample(c.LinuxSelfReader(), 'userNamespace', budget, 'offline', 'supplied')
        self.assertEqual('OVERSIZE', record['status'])
        self.assertEqual(0, record['readBudgetBytes'])
        self.assertEqual(c.TOTAL_LIMIT - 8, budget.used)

    def test_errors_remain_unknown(self):
        for code, status in [(errno.EACCES, 'DENIED'), (errno.EPERM, 'DENIED'), (errno.ENOENT, 'MISSING'),
                             (errno.EOPNOTSUPP, 'UNSUPPORTED'), (errno.EINTR, 'INTERRUPTED'), (errno.EIO, 'IO_ERROR')]:
            data = fixtures()
            data['status'] = OSError(code, 'private-path-must-not-leak')
            report = run(Reader(data))
            record = report['evidence'][2]
            self.assertEqual(record['status'], status)
            self.assertEqual(record['errno'], code)
            self.assertEqual(record['evidenceClass'], 'unknown')
            self.assertIsNone(record['value'])
            self.assertEqual(report['collectionCompletion'], 'INCOMPLETE')
            self.assertNotIn(b'private-path-must-not-leak', c.encode(report))

    def test_oversize_and_total_cap(self):
        data = fixtures()
        data['mounts'] = b'x' * (c.BLOB_LIMIT + 1)
        report = run(Reader(data))
        self.assertEqual(report['evidence'][8]['status'], 'OVERSIZE')
        with patch.object(c, 'TOTAL_LIMIT', 1):
            self.assertEqual(run()['collectionCompletion'], 'INCOMPLETE')

    def test_timeout_stops_future_reads(self):
        reader = Reader()
        clock_values = iter([0] + [6] * 100)
        report = run(reader, clock=lambda: next(clock_values))
        self.assertEqual(reader.calls, [])
        self.assertTrue(all(r['status'] == 'TIMEOUT' for r in report['evidence']))
        self.assertEqual(report['collectionCompletion'], 'INCOMPLETE')

    def test_interrupt(self):
        data = fixtures()
        data['boot'] = KeyboardInterrupt()
        report = run(Reader(data))
        self.assertEqual(report['evidence'][0]['status'], 'INTERRUPTED')
        self.assertEqual(report['collectionCompletion'], 'INCOMPLETE')

    def test_namespace_start_mount_and_credential_drift(self):
        for key, replacement in [('userNamespace', b'user:[999]'), ('mountNamespace', b'mnt:[999]'),
                                 ('stat', fixtures()['stat'].replace(b'123 ', b'124 ')),
                                 ('mounts', fixtures()['mounts'].replace(b'25 1', b'26 1')),
                                 ('status', fixtures()['status'].replace(b'1000 1001', b'1000 1002'))]:
            class DriftReader(Reader):
                def read(self, name, limit=c.BLOB_LIMIT + 1):
                    if name == key and name in self.calls:
                        return replacement
                    return super().read(name, limit)
            report = run(DriftReader())
            self.assertIn(key, report['drift'])
            self.assertEqual(report['collectionCompletion'], 'INCOMPLETE')

    def test_wrong_pid(self):
        data = fixtures()
        data['stat'] = data['stat'].replace(b'42 (', b'43 (')
        self.assertIn('selfPid', run(Reader(data))['drift'])

    def test_source_hash_does_not_authenticate_provenance(self):
        report = run()
        self.assertEqual(report['sourceClosure'], 'UNKNOWN')
        self.assertEqual(report['targetInspectorContext'], 'NOT ESTABLISHED')
        self.assertFalse(report['productPromotion'])
        self.assertEqual(report['runtimeQualification'], 'UNRESOLVED')


class Boundaries(unittest.TestCase):
    def test_short_reads_do_not_hide_later_valid_records(self):
        first = fixtures()['mounts']
        complete = first + first.replace(b'25 1', b'26 1')

        class ShortReads(io.BytesIO):
            def read(self, count=-1):
                return super().read(min(count, len(first)))

        with patch('builtins.open', return_value=ShortReads(complete)):
            data = c.LinuxSelfReader().read('mounts')
        self.assertEqual(complete, data)
        self.assertEqual(2, len(c.parse('mounts', data)))

    def test_nonterminating_short_reads_exhaust_fragment_bound(self):
        stream = unittest.mock.MagicMock()
        stream.__enter__.return_value.read.return_value = b'x'
        with patch('builtins.open', return_value=stream):
            with self.assertRaises(OverflowError):
                c.LinuxSelfReader().read('release')
        self.assertLessEqual(stream.__enter__.return_value.read.call_count, 64)

    def test_default_invalid_and_nonlinux_refuse_before_file_access(self):
        with patch('builtins.open', side_effect=AssertionError('host file access')), \
             patch.object(c.tempfile, 'mkdtemp', side_effect=AssertionError('output access')), \
             patch.object(c.os, 'readlink', side_effect=AssertionError('host access')), \
             patch.object(c.sys, 'stderr', io.StringIO()):
            for platform in ('darwin', 'linux', 'win32'):
                with patch.object(c.sys, 'platform', platform):
                    for args in ([], ['collect'], ['collect', '../x'], ['collect', 'x' * 33],
                                 ['collect', 'x', '--qualified'], ['collect', 'x', '/etc/passwd']):
                        self.assertEqual(c.main(args), 2)
                    if platform != 'linux':
                        self.assertEqual(c.main(['collect', 'x']), 2)

    def test_reader_finite_paths_and_bound(self):
        for key, (path, kind) in c.SOURCES.items():
            stream = unittest.mock.MagicMock()
            stream.__enter__.return_value.read.side_effect = [b'fixture', b'']
            with patch('builtins.open', return_value=stream) as opened, patch.object(c.os, 'readlink', return_value='mnt:[1]') as link:
                c.LinuxSelfReader().read(key)
                if kind in ('user', 'mnt'):
                    link.assert_called_once_with(path)
                    opened.assert_not_called()
                else:
                    opened.assert_called_once_with(path, 'rb', buffering=0)
                    self.assertEqual(stream.__enter__.return_value.read.call_args_list,
                                     [unittest.mock.call(c.BLOB_LIMIT + 1),
                                      unittest.mock.call(c.BLOB_LIMIT + 1 - len(b'fixture'))])
        with self.assertRaises(KeyError):
            c.LinuxSelfReader().read('/etc/passwd')

    def test_new_output_receipt_and_hash(self):
        with tempfile.TemporaryDirectory() as root:
            directory = Path(root)
            c.publish(directory, run())
            import json
            receipt = json.loads((directory / 'receipt.json').read_text())
            self.assertEqual(receipt['reportSha256'], c.sha((directory / 'report.json').read_bytes()))
            self.assertEqual((directory / 'report.json').stat().st_mode & 0o777, 0o600)
            with self.assertRaises(FileExistsError):
                c.write_new(directory, 'report.json', b'replacement')

    def test_partial_output_and_disk_error_never_receipted(self):
        for failure in (OSError(errno.ENOSPC, 'full'), KeyboardInterrupt()):
            with tempfile.TemporaryDirectory() as root:
                directory = Path(root)
                with patch.object(c.os, 'fsync', side_effect=failure):
                    with self.assertRaises(type(failure)):
                        c.publish(directory, run())
                self.assertFalse((directory / 'receipt.json').exists())
                self.assertTrue((directory / 'report.partial').exists())

    def test_output_cap_before_publication(self):
        with tempfile.TemporaryDirectory() as root, patch.object(c, 'OUTPUT_LIMIT', 1):
            with self.assertRaises(OverflowError):
                c.publish(Path(root), run())
            self.assertEqual(list(Path(root).iterdir()), [])


class CliOfflineIntegration(unittest.TestCase):
    def test_actual_entrypoint_with_mocked_linux_io_only(self):
        # Linux branch exercised without ANY real /proc/sysfs access on macOS.
        with tempfile.TemporaryDirectory() as root:
            directory = Path(root) / 'new-owned'
            directory.mkdir(mode=0o700)
            with patch.object(c.sys, 'platform', 'linux'), patch.object(c.os, 'getpid', return_value=42), \
                 patch.object(c, 'LinuxSelfReader', return_value=Reader()), \
                 patch.object(c.tempfile, 'mkdtemp', return_value=str(directory)) as created, \
                 patch.object(c.sys, 'stdout', io.StringIO()):
                self.assertEqual(c.main(['collect', 'fixture-run']), 0)
                self.assertEqual(created.call_args.kwargs['dir'], '/tmp')
                self.assertTrue((directory / 'receipt.json').exists())
            # This mocked entrypoint result is NOT retained as native evidence.

    def test_existing_final_output_is_never_replaced(self):
        with tempfile.TemporaryDirectory() as root:
            directory = Path(root)
            c.publish(directory, run())
            original = (directory / 'report.json').read_bytes()
            with self.assertRaises(FileExistsError):
                c.publish(directory, run())
            self.assertEqual((directory / 'report.json').read_bytes(), original)

    def test_late_timeout_and_short_write(self):
        clock = iter([0, 0, 0, 6, 6])
        record = c.sample(Reader(), 'boot', c.Budget(lambda: next(clock)), 'x', 'supplied')
        self.assertEqual(record['status'], 'TIMEOUT')
        self.assertIsNone(record['value'])
        stream = unittest.mock.MagicMock()
        stream.__enter__.return_value.write.return_value = 0
        with patch('builtins.open', return_value=stream), patch.object(c.os, 'chmod'):
            with self.assertRaises(OSError):
                c.write_new(Path('/offline-unused'), 'x', b'abc')


class LinuxSelfContextIntegration(unittest.TestCase):
    @unittest.skipUnless(sys.platform == 'linux', 'Real self-context collection requires Linux; offline fixtures are separate')
    def test_real_linux_collection_retains_conservative_receipt_and_identity(self):
        output = io.StringIO()
        created = []
        create = tempfile.mkdtemp
        code = None

        def track_output(*args, **kwargs):
            directory = create(*args, **kwargs)
            created.append(Path(directory))
            return directory

        def retain_evidence():
            # unittest records cleanup errors separately from primary failures.
            source = Path(c.__file__)
            artifacts = source.resolve().parents[2] / '.artifacts/ci/inspector-context'
            artifacts.mkdir(parents=True, exist_ok=True)
            retained = Path(create(prefix='native-', dir=artifacts))
            if created:
                for name in ('report.json', 'receipt.json', 'INCOMPLETE', 'receipt.partial'):
                    original = created[0] / name
                    if original.is_file() and not original.is_symlink():
                        shutil.copyfile(original, retained / name)
                        os.chmod(retained / name, 0o600)
            shutil.copyfile(source, retained / 'collector.py')
            os.chmod(retained / 'collector.py', 0o600)
            attempt = retained / 'attempt.json'
            attempt.write_text(json.dumps({'platform': sys.platform, 'collectorExit': code,
                                           'createdOutputs': [str(path) for path in created]}, indent=2) + '\n')
            os.chmod(attempt, 0o600)

        self.addCleanup(retain_evidence)
        # Spy on the real output factory only; reader/platform/source stay real.
        with patch.object(c.tempfile, 'mkdtemp', side_effect=track_output), redirect_stdout(output):
            code = c.main(['collect', 'native-ci'])
        self.assertEqual(1, len(created))
        directory = created[0]
        self.assertEqual(str(directory), output.getvalue().strip())
        self.assertEqual(Path('/tmp'), directory.parent)
        self.assertTrue(directory.name.startswith('cm-self-native-ci-'))
        self.assertFalse(directory.is_symlink())
        self.assertEqual(os.getuid(), directory.stat().st_uid)
        self.assertEqual(0o700, directory.stat().st_mode & 0o777)
        report_bytes = (directory / 'report.json').read_bytes()
        report = json.loads(report_bytes)
        receipt = json.loads((directory / 'receipt.json').read_bytes())
        self.assertEqual(c.sha(report_bytes), receipt['reportSha256'])
        self.assertEqual(report['collectionCompletion'], receipt['collectionCompletion'])
        self.assertEqual(0 if report['collectionCompletion'] == 'COMPLETE' else 1, code)
        self.assertEqual('SELF_CONTEXT', report['domain'])
        for record in report['evidence']:
            if record['key'] in ('boot', 'stat', 'userNamespace', 'mountNamespace'):
                self.assertEqual('OBSERVED', record['status'])
        self.assertEqual(os.getpid(), report['selfIdentity']['pid'])
        self.assertEqual(22, len(report['evidence']))
        self.assertLessEqual(report['readBudgetBytes'], c.TOTAL_LIMIT)
        for document in (report, receipt):
            self.assertEqual('UNRESOLVED', document['runtimeQualification'])
            self.assertIs(False, document['productPromotion'])
        self.assertEqual('NOT ESTABLISHED', report['targetInspectorContext'])
        source = Path(c.__file__)
        self.assertEqual(c.sha(source.read_bytes()), report['sourceSha256'])


if __name__ == '__main__':
    unittest.main()
