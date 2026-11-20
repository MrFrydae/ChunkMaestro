"""Mechanical I/O bounds with synthetic authenticated preconditions, not runtime QA."""
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import initial_events as events
import replay
import test_cell_ci as cell
import test_test_cell_ci as fixtures


class DiagnosticIoTests(unittest.TestCase):
    def test_cell_oversized_logs_are_streamed_then_refused_without_full_load(self):
        for relative in ('mutation/logs/Runner 0-log.txt', 'mutation/logs/log-20261117.txt', 'private.log'):
            with self.subTest(relative=relative):
                fixture = fixtures.Cells()
                try:
                    fixture.setUp()
                    folder = fixture.folders[0]
                    target = folder / relative
                    with target.open('wb') as stream:
                        stream.truncate(events.MAX_LOG_BYTES + 1)
                    def reseal(directory, value):
                        if relative == 'private.log':
                            value['commands'][4]['log_sha256'] = replay.file_digest(target)
                            (directory / 'commands.jsonl').write_bytes(
                                ''.join(json.dumps(r) + '\n' for r in value['commands']).encode())
                    fixture.reseal(reseal)
                    original_open, original_read_bytes = Path.open, Path.read_bytes
                    reads = []
                    testcase = self
                    class StreamGuard:
                        def __init__(self, stream):
                            self.stream = stream
                        def __enter__(self):
                            self.stream.__enter__()
                            return self
                        def __exit__(self, *args):
                            return self.stream.__exit__(*args)
                        def __getattr__(self, name):
                            return getattr(self.stream, name)
                        def read(self, size=-1):
                            testcase.assertGreater(size, 0)
                            testcase.assertLessEqual(size, 1024 * 1024)
                            reads.append(size)
                            return self.stream.read(size)
                    def open_guard(path, *args, **kwargs):
                        stream = original_open(path, *args, **kwargs)
                        return StreamGuard(stream) if path == target else stream
                    def read_guard(path):
                        testcase.assertNotEqual(path, target, 'oversized diagnostic materialized by read_bytes')
                        return original_read_bytes(path)
                    with patch.object(Path, 'open', open_guard), patch.object(Path, 'read_bytes', read_guard):
                        with self.assertRaisesRegex(ValueError, '32 MiB bound'):
                            cell.validate_cell(folder, fixture.plan, fixture.bundle, fixture.current, fixture.root)
                    self.assertTrue(reads, 'entry point did not exercise inventory hashing')
                    self.assertTrue(target.exists(), 'raw diagnostic must remain retained')
                finally:
                    fixture.doCleanups()

    def test_public_binding_example_refuses_oversized_input_before_materializing(self):
        for name in ('wire', 'phase', 'stdout'):
            with self.subTest(name=name), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                output = root / 'mutation'
                logs = output / 'logs'
                logs.mkdir(parents=True)
                paths = {'wire': logs / 'Runner 0-log.txt', 'phase': logs / 'log-fixture.txt', 'stdout': root / 'stdout.log'}
                for path in paths.values():
                    path.write_bytes(b'small synthetic diagnostic')
                with paths[name].open('wb') as stream:
                    stream.truncate(events.MAX_LOG_BYTES + 1)
                source = root / 'source'
                pins = {}
                for relative in events.SOURCE_FILES:
                    path = source / relative
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_bytes(b'synthetic source precondition')
                    pins[relative] = replay.file_digest(path)
                sdk, tool = root / 'sdk', root / 'tool'
                sdk.write_bytes(b'synthetic SDK precondition')
                tool.write_bytes(b'synthetic tool precondition')
                config = root / 'config.json'
                replay.write(config, {'stryker-config': {'concurrency': 1, 'test-projects': ['fixture']}})
                argv = [str(sdk), str(tool), '--config-file', str(config), '--output', str(output), '--diag', '--log-to-file']
                command = root / 'command.json'
                replay.write(command, {'args': argv, 'cwd': str(root), 'timeout': False, 'exit_code': 0,
                                       'log': str(paths['stdout']), 'log_sha256': replay.file_digest(paths['stdout'])})
                binding = {'source_commit': events.COMMIT, 'archive_sha256': events.ARCHIVE_SHA256,
                    'source_root': str(source), 'source_hashes': pins, 'sdk_version': events.SUPPORTED_SDK,
                    'command_path': str(command), 'command_sha256': replay.file_digest(command),
                    'argv': argv, 'cwd': str(root), 'cancelled': False,
                    'sdk_sha256': replay.file_digest(sdk), 'tool_sha256': replay.file_digest(tool),
                    'config_sha256': replay.file_digest(config), 'wire_path': str(paths['wire']),
                    'wire_sha256': replay.file_digest(paths['wire']), 'phase_path': str(paths['phase']),
                    'phase_sha256': replay.file_digest(paths['phase']), 'assembly': 'fixture', 'cases': []}
                original = Path.read_bytes
                testcase = self
                def guard(path):
                    testcase.assertNotEqual(path, paths[name], 'public API fully loaded oversized diagnostic')
                    return original(path)
                with patch.object(events, 'SOURCE_HASHES', pins), patch.object(Path, 'read_bytes', guard):
                    result = events.validate_initial_events(binding)
                self.assertEqual('UNRESOLVED', result['status'])
                self.assertFalse(result['credit'])
                self.assertIn('32 MiB bound', result['reason'])

    def test_bounded_reader_detects_changed_size_and_preserves_hash_semantics(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'diagnostic'
            path.write_bytes(b'abc')
            expected = replay.digest(b'abc')
            self.assertEqual(expected, replay.file_digest(path))
            self.assertEqual(b'abc', events.read_diagnostic(path, expected))
            actual = path.stat()
            with patch.object(events.os, 'fstat', return_value=SimpleNamespace(st_mode=actual.st_mode, st_size=2)):
                with self.assertRaisesRegex(ValueError, 'changed while reading'):
                    events.read_diagnostic(path)
            with self.assertRaisesRegex(ValueError, 'hash mismatch'):
                events.read_diagnostic(path, '0' * 64)


if __name__ == '__main__':
    unittest.main()
