"""Offline supervision and no-window action checks; never run AppKit or authentication."""
import importlib.util
import json
import os
import platform
from pathlib import Path
import signal
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("qualification_runner", Path(__file__).with_name("run.py"))
runner = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(runner)
HEAD = "a" * 40


def selected(mode="--no-prompt"):
    return [mode, "--reviewed-head", HEAD, "--desktop-confirmed-unlocked"]


class RunnerTests(unittest.TestCase):
    def test_refusal_precedes_children_and_frameworks(self):
        for args, environment in (([], {}), (["--unknown"], {}), (["--no-prompt", "--interactive"], {}),
                                  (selected(), {"GITHUB_ACTIONS": "false"}),
                                  (selected()[:-1], {}),
                                  (["--no-prompt", "--reviewed-head", "wrong", "--desktop-confirmed-unlocked"], {})):
            with self.subTest(args=args, hosted=bool(environment)), patch.dict(os.environ, environment, clear=True), \
                    patch.object(runner.subprocess, "Popen") as child, \
                    patch.object(runner, "graphical_preflight") as preflight, \
                    patch.object(runner.tempfile, "mkdtemp") as scratch:
                self.assertEqual(64, runner.main(args))
                child.assert_not_called()
                preflight.assert_not_called()
                scratch.assert_not_called()

    def test_explicit_selection_only_parses_interactive_without_launching(self):
        self.assertEqual(("--interactive", HEAD), runner.mode_from(selected("--interactive"), {}))
        self.assertEqual(("--no-prompt", HEAD), runner.mode_from(selected(), {}))

    def test_unavailable_preflight_never_builds(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict(os.environ, {}, clear=True), \
                patch.object(runner.tempfile, "mkdtemp", return_value=directory), \
                patch.object(runner, "graphical_preflight", return_value={"available": False}), \
                patch.object(runner.subprocess, "Popen") as child:
            self.assertEqual(77, runner.main(selected()))
            child.assert_not_called()
            self.assertEqual("UNAVAILABLE", json.loads((Path(directory) / "report.json").read_text())["status"])

    def run_child(self, source, deadline=2):
        directory = Path(self.scratch.name)
        return runner.command([sys.executable, "-c", source], self.environment, directory,
                              "case" + str(len(list(directory.glob("*.command.json")))), deadline)

    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory()
        self.addCleanup(self.scratch.cleanup)
        self.environment = runner.clean_environment(Path(self.scratch.name))

    def test_environment_is_allowlist(self):
        self.assertEqual({"PATH", "HOME", "TMPDIR", "LANG", "LC_ALL"}, set(self.environment))
        self.assertTrue(self.environment["HOME"].startswith(self.scratch.name))

    def test_failure_keeps_original_output_and_exit(self):
        record = self.run_child("import sys; print('first failure', flush=True); sys.exit(9)")
        self.assertEqual(9, record["exit"])
        self.assertFalse(runner.successful(record))
        self.assertTrue(record["group_reaped"])
        self.assertEqual("first failure\n", (Path(self.scratch.name) / "case0.stdout").read_text())
        self.assertTrue((Path(self.scratch.name) / "case0.command.json").exists())

    def test_cleanup_error_does_not_erase_primary_failure(self):
        with patch.object(runner, "reap_group", side_effect=runner.subprocess.TimeoutExpired("cleanup", 5)):
            record = self.run_child("import sys; print('original failure', flush=True); sys.exit(9)")
        self.assertEqual(9, record["exit"])
        self.assertEqual("exited", record["reason"])
        self.assertEqual("TimeoutExpired", record["cleanup_error_category"])
        self.assertFalse(record["group_reaped"])
        self.assertFalse(runner.successful(record))
        self.assertIn("original failure", (Path(self.scratch.name) / "case0.stdout").read_text())

    def test_sigterm_during_cleanup_preserves_primary_record(self):
        reap = runner.reap_group
        previous = signal.getsignal(signal.SIGTERM)

        def signal_after_reap(process):
            result = reap(process)
            os.kill(os.getpid(), signal.SIGTERM)
            return result

        try:
            with patch.object(runner, "reap_group", side_effect=signal_after_reap):
                record = self.run_child("import sys; print('primary exit nine', flush=True); sys.exit(9)")
        finally:
            signal.signal(signal.SIGTERM, previous)
        saved = json.loads((Path(self.scratch.name) / "case0.command.json").read_text())
        self.assertEqual(9, saved["exit"])
        self.assertEqual("exited", saved["reason"])
        self.assertEqual(signal.SIGTERM, saved["cleanup_signal"])
        self.assertTrue(saved["group_reaped"])
        self.assertFalse(runner.successful(record))
        self.assertIn("primary exit nine", (Path(self.scratch.name) / "case0.stdout").read_text())

    def test_sigint_during_cleanup_cannot_turn_into_success(self):
        reap = runner.reap_group
        previous = signal.getsignal(signal.SIGINT)

        def signal_after_reap(process):
            result = reap(process)
            os.kill(os.getpid(), signal.SIGINT)
            return result

        try:
            with patch.object(runner, "reap_group", side_effect=signal_after_reap):
                record = self.run_child("print('child completed', flush=True)")
        finally:
            signal.signal(signal.SIGINT, previous)
        self.assertEqual(0, record["exit"])
        self.assertEqual(signal.SIGINT, record["cleanup_signal"])
        self.assertTrue(record["group_reaped"])
        self.assertFalse(runner.successful(record))

    def test_write_window_signals_are_in_saved_and_returned_result(self):
        write = Path.write_text
        for number in (signal.SIGTERM, signal.SIGINT):
            for code in (0, 9):
                with self.subTest(signal=number, exit=code):
                    directory = Path(self.scratch.name)
                    label = "case" + str(len(list(directory.glob("*.command.json"))))
                    fired = False

                    def signal_during_write(path, data, *args, **kwargs):
                        nonlocal fired
                        if ".command.json" in path.name and not fired:
                            fired = True
                            os.kill(os.getpid(), number)
                        return write(path, data, *args, **kwargs)

                    with patch.object(Path, "write_text", signal_during_write):
                        record = self.run_child("import sys; print('primary result', flush=True); sys.exit(" + str(code) + ")")
                    saved = json.loads((directory / (label + ".command.json")).read_text())
                    self.assertTrue(fired)
                    self.assertEqual(record, saved)
                    self.assertEqual(code, saved["exit"])
                    self.assertEqual(number, saved["cleanup_signal"])
                    self.assertFalse(runner.successful(record))
                    self.assertFalse(runner.successful(saved))

    def test_repeated_signal_during_rewrite_keeps_first_cancellation(self):
        write = Path.write_text
        writes = 0

        def signal_during_write(path, data, *args, **kwargs):
            nonlocal writes
            if ".command.json" in path.name:
                writes += 1
                os.kill(os.getpid(), signal.SIGTERM if writes == 1 else signal.SIGINT)
            return write(path, data, *args, **kwargs)

        with patch.object(Path, "write_text", signal_during_write):
            record = self.run_child("print('primary success', flush=True)")
        saved = json.loads((Path(self.scratch.name) / "case0.command.json").read_text())
        self.assertEqual(2, writes)
        self.assertEqual(record, saved)
        self.assertEqual(signal.SIGTERM, saved["cleanup_signal"])
        self.assertFalse(runner.successful(saved))

    def test_signal_during_serialization_is_in_the_final_snapshot(self):
        dumps = json.dumps
        fired = False

        def signal_after_encoding(value, *args, **kwargs):
            nonlocal fired
            encoded = dumps(value, *args, **kwargs)
            if not fired:
                fired = True
                os.kill(os.getpid(), signal.SIGTERM)
            return encoded

        with patch.object(runner.json, "dumps", side_effect=signal_after_encoding):
            record = self.run_child("print('completed child', flush=True)")
        saved = json.loads((Path(self.scratch.name) / "case0.command.json").read_text())
        self.assertTrue(fired)
        self.assertEqual(record, saved)
        self.assertEqual(signal.SIGTERM, saved["cleanup_signal"])
        self.assertFalse(runner.successful(saved))

    def test_signal_during_atomic_publication_is_preserved(self):
        replace = os.replace
        fired = False

        def signal_after_replace(source, destination):
            nonlocal fired
            result = replace(source, destination)
            if not fired:
                fired = True
                os.kill(os.getpid(), signal.SIGINT)
            return result

        with patch.object(runner.os, "replace", side_effect=signal_after_replace):
            record = self.run_child("print('child finished', flush=True)")
        saved = json.loads((Path(self.scratch.name) / "case0.command.json").read_text())
        self.assertTrue(fired)
        self.assertEqual(record, saved)
        self.assertEqual(signal.SIGINT, saved["cleanup_signal"])
        self.assertFalse(runner.successful(saved))

    def test_post_commit_signal_interrupts_caller_not_completed_record(self):
        replace = os.replace
        calls = 0
        previous = signal.getsignal(signal.SIGTERM)

        def signal_after_commit(source, destination):
            nonlocal calls
            result = replace(source, destination)
            calls += 1
            if calls == 2:
                os.kill(os.getpid(), signal.SIGTERM)
            return result

        with patch.object(runner.os, "replace", side_effect=signal_after_commit):
            with self.assertRaises(runner.CommandInterrupted):
                self.run_child("print('completed before caller cancellation', flush=True)")
        self.assertEqual(previous, signal.getsignal(signal.SIGTERM))
        saved = json.loads((Path(self.scratch.name) / "case0.command.json").read_text())
        self.assertEqual(0, saved["exit"])
        self.assertTrue(runner.successful(saved))

    def test_cancellation_rewrite_failure_never_leaves_canonical_success(self):
        write = Path.write_text
        replace = os.replace
        for operation in ("write", "rename"):
            for code in (0, 9):
                with self.subTest(operation=operation, exit=code):
                    directory = Path(self.scratch.name)
                    label = "case" + str(len(list(directory.glob("*.command.json"))))
                    writes = 0
                    renames = 0

                    def injected_write(path, data, *args, **kwargs):
                        nonlocal writes
                        writes += 1
                        if writes == 1:
                            os.kill(os.getpid(), signal.SIGTERM)
                        if operation == "write" and writes == 2:
                            raise OSError("injected final write failure")
                        return write(path, data, *args, **kwargs)

                    def injected_rename(source, destination):
                        nonlocal renames
                        renames += 1
                        if operation == "rename" and renames == 2:
                            raise OSError("injected final rename failure")
                        return replace(source, destination)

                    with patch.object(Path, "write_text", injected_write), \
                            patch.object(runner.os, "replace", side_effect=injected_rename):
                        with self.assertRaises(OSError):
                            self.run_child("import sys; print('primary retained', flush=True); sys.exit(" + str(code) + ")")
                    saved = json.loads((directory / (label + ".command.json")).read_text())
                    self.assertEqual(code, saved["exit"])
                    self.assertEqual("incomplete", saved.get("evidence_state"))
                    self.assertFalse(runner.successful(saved))
                    self.assertEqual("primary retained\n", (directory / (label + ".stdout")).read_text())

    def test_restoration_signal_is_raised_after_both_handlers_are_restored(self):
        for number in (signal.SIGTERM, signal.SIGINT):
            with self.subTest(signal=number):
                install = signal.signal
                original = {item: signal.getsignal(item) for item in (signal.SIGTERM, signal.SIGINT)}
                previous = {signal.SIGTERM: lambda *_: None, signal.SIGINT: lambda *_: None}
                fired = False

                def interrupted_restore(item, handler):
                    nonlocal fired
                    result = install(item, handler)
                    if item == signal.SIGTERM and handler is previous[item] and not fired:
                        fired = True
                        os.kill(os.getpid(), number)
                    return result

                try:
                    for item, handler in previous.items():
                        install(item, handler)
                    with patch.object(runner.signal, "signal", side_effect=interrupted_restore):
                        with self.assertRaises(runner.CommandInterrupted):
                            self.run_child("print('completed child', flush=True)")
                    self.assertTrue(fired)
                    for item, handler in previous.items():
                        self.assertIs(handler, signal.getsignal(item))
                finally:
                    for item, handler in original.items():
                        install(item, handler)

    @unittest.skipUnless(platform.system() == "Darwin", "No-window AppKit action probe requires macOS frameworks")
    def test_native_cancel_consumes_prepared_attempt_before_verify(self):
        directory = Path(self.scratch.name)
        source = directory / "cancel.m"
        source.write_text('#define main qualification_host_main\n#import ' +
                          json.dumps(str(runner.ROOT / "tooling/owner-qualification/host.m")) +
                          '\n#undef main\n' + '''
int main(void)
{
    @autoreleasepool
    {
        interactive = YES;
        attempted = NO;
        Fixture *fixture = [Fixture new];
        [fixture cancel:nil];
        if (!attempted)
        {
            fprintf(stderr, "FAIL: Cancel left prepared attempt enabled\\n");
            return 41;
        }
        [fixture verify:nil];
        if (request != 0 || transferred)
        {
            return 42;
        }
        puts("PASS: Cancel consumed attempt; Verify created no request");
    }
    return 0;
}
''')
        binary = directory / "cancel"
        build = runner.command(["/usr/bin/clang", "-fobjc-arc", "-fblocks", "-Wall", "-Wextra", "-Werror",
                                "-I", str(runner.NATIVE), str(source), str(runner.NATIVE / "mac-owner.m"),
                                "-framework", "Foundation", "-framework", "AppKit",
                                "-framework", "LocalAuthentication", "-framework", "CoreGraphics",
                                "-o", str(binary)], self.environment, directory, "cancel-build", 60)
        self.assertTrue(runner.successful(build), (directory / "cancel-build.stderr").read_text())
        action = runner.command([str(binary)], self.environment, directory, "cancel-action", 5)
        self.assertTrue(runner.successful(action), (directory / "cancel-action.stderr").read_text())
        output = (directory / "cancel-action.stdout").read_text()
        self.assertIn("PASS: Cancel consumed attempt", output)
        self.assertNotIn('"event":"abi2-create"', output)

    def test_sigterm_reaps_command_and_records_interruption(self):
        timer = threading.Timer(0.2, lambda: os.kill(os.getpid(), signal.SIGTERM))
        timer.start()
        try:
            record = self.run_child("import time; print('before signal', flush=True); time.sleep(30)")
        finally:
            timer.cancel()
            timer.join()
        self.assertEqual("interrupted", record["reason"])
        self.assertEqual(signal.SIGTERM, record["signal"])
        self.assertEqual(-signal.SIGKILL, record["exit"])
        self.assertTrue(record["group_reaped"])

    def test_timeout_reaps_group_and_preserves_output(self):
        record = self.run_child("import time; print('before timeout', flush=True); time.sleep(30)", 0.2)
        self.assertEqual("timeout", record["reason"])
        self.assertEqual(-signal.SIGKILL, record["exit"])
        self.assertTrue(record["group_reaped"])
        self.assertIn("before timeout", (Path(self.scratch.name) / "case0.stdout").read_text())

    @unittest.skipUnless(hasattr(os, "fork"), "POSIX process group contract")
    def test_exited_leader_does_not_leave_live_descendant(self):
        # Child closes inherited pipes; runner must still reap the group after leader exit.
        record = self.run_child("import os,time; pid=os.fork(); "
                                "os._exit(0) if pid else None; os.close(1); os.close(2); time.sleep(30)")
        self.assertTrue(record["group_reaped"])
        self.assertEqual(0, record["exit"])

    def test_output_limit_is_failure_and_finite(self):
        record = self.run_child("import os; data=b'x'*8192\nwhile True: os.write(1,data)")
        self.assertEqual("output-limit", record["reason"])
        self.assertEqual(runner.LIMIT, record["output_bytes"])
        self.assertTrue(record["group_reaped"])

    def test_zero_exit_without_native_assertions_is_not_evidence(self):
        path = Path(self.scratch.name) / "empty.stdout"
        path.write_text("")
        valid, observations = runner.no_prompt_observations(path)
        self.assertFalse(valid)
        self.assertEqual([], observations)

    def test_compile_failure_never_launches_host(self):
        def fake_command(args, env, directory, label, deadline):
            if label == "head":
                (directory / "head.stdout").write_text(HEAD + "\n")
            return {"reason": "exited", "exit": 1 if label == "compile" else 0,
                    "group_reaped": True, "evidence_state": "complete"}

        original_is_file = Path.is_file
        with tempfile.TemporaryDirectory() as directory, patch.dict(os.environ, {}, clear=True), \
                patch.object(runner.tempfile, "mkdtemp", return_value=directory), \
                patch.object(runner, "graphical_preflight", return_value={"available": True}), \
                patch.object(Path, "is_file", lambda path: str(path) == "/usr/bin/clang" or original_is_file(path)), \
                patch.object(runner, "snapshot_sources", return_value=({}, [])), \
                patch.object(runner, "command", side_effect=fake_command) as command:
            self.assertEqual(1, runner.main(selected()))
            self.assertEqual(["head", "compiler-version", "compile"], [call.args[3] for call in command.call_args_list])
            self.assertEqual("FAIL-build", json.loads((Path(directory) / "report.json").read_text())["status"])

    def test_changed_source_cannot_be_compiled_as_reviewed_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            live = root / "tooling/owner-qualification/host.m"
            live.parent.mkdir(parents=True)
            live.write_text("changed source\n")

            def command(args, env, build, label, deadline):
                (build / (label + ".stdout")).write_text("reviewed source\n")
                return {"reason": "exited", "exit": 0, "group_reaped": True, "evidence_state": "complete"}

            with patch.object(runner, "ROOT", root), patch.object(runner, "command", side_effect=command):
                with self.assertRaisesRegex(runner.SourceBindingError, "uncommitted-source-mismatch"):
                    runner.snapshot_sources(HEAD, {}, root)
            self.assertFalse((root / "source").exists())

    def test_compiler_snapshot_survives_later_live_source_edit(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            paths = ["tooling/owner-qualification/host.m", "tooling/owner-qualification/run.py",
                     "tooling/owner-qualification/test_runner.py", "docs/qa/owner-native-qualification.md",
                     "src/ChunkMaestro.OwnerVerification/Native/mac-owner.m",
                     "src/ChunkMaestro.OwnerVerification/Native/mac-owner.h"]
            for path in paths:
                live = root / path
                live.parent.mkdir(parents=True, exist_ok=True)
                live.write_text("reviewed source\n")
            expected = {name: runner.digest(root / "src/ChunkMaestro.OwnerVerification/Native" / name)
                        for name in runner.EXPECTED}
            build = root / "build"
            build.mkdir()

            def command(args, env, output, label, deadline):
                (output / (label + ".stdout")).write_text("reviewed source\n")
                return {"reason": "exited", "exit": 0, "group_reaped": True, "evidence_state": "complete"}

            with patch.object(runner, "ROOT", root), patch.object(runner, "EXPECTED", expected), \
                    patch.object(runner, "command", side_effect=command):
                manifest, records = runner.snapshot_sources(HEAD, {}, build)
            (root / paths[0]).write_text("later changed source\n")
            self.assertEqual("reviewed source\n", (build / "source" / paths[0]).read_text())
            self.assertEqual(manifest[paths[0]], runner.digest(build / "source" / paths[0]))
            self.assertNotEqual(manifest[paths[0]], runner.digest(root / paths[0]))
            self.assertEqual(6, len(records))

    def test_launch_failure_is_preserved(self):
        record = runner.command(["/does-not-exist/fixture"], self.environment,
                                Path(self.scratch.name), "missing", 1)
        self.assertEqual("launch-error", record["reason"])
        self.assertTrue(record["group_reaped"])
        self.assertIsNone(record["exit"])

    def test_only_complete_command_evidence_can_succeed(self):
        record = {"reason": "exited", "exit": 0, "group_reaped": True}
        self.assertFalse(runner.successful(record))
        for state in ("incomplete", "unknown", None):
            with self.subTest(state=state):
                record["evidence_state"] = state
                self.assertFalse(runner.successful(record))
        record["evidence_state"] = "complete"
        self.assertTrue(runner.successful(record))

    def test_signal_mask_is_preserved(self):
        previous = signal.pthread_sigmask(signal.SIG_BLOCK, set())
        self.run_child("print('normal child', flush=True)")
        self.assertEqual(previous, signal.pthread_sigmask(signal.SIG_BLOCK, set()))

    def test_late_mixed_restoration_signal_preserves_publication_error(self):
        write = Path.write_text
        replace = os.replace
        dumps = json.dumps
        pending = signal.sigpending
        wait = signal.sigwait
        for operation in ("write", "rename", "serialize"):
            for code in (0, 9):
                with self.subTest(operation=operation, exit=code):
                    directory = Path(self.scratch.name)
                    label = "case" + str(len(list(directory.glob("*.command.json"))))
                    writes = 0
                    fired = False
                    previous = {item: signal.getsignal(item) for item in (signal.SIGTERM, signal.SIGINT)}
                    old_mask = signal.pthread_sigmask(signal.SIG_BLOCK, set())
                    observed = None

                    def injected_write(path, data, *args, **kwargs):
                        nonlocal writes
                        writes += 1
                        if operation == "write" and writes == 2:
                            raise OSError("primary publication failure")
                        return write(path, data, *args, **kwargs)

                    def injected_rename(source, destination):
                        if operation == "rename" and Path(destination).exists():
                            raise OSError("primary publication failure")
                        return replace(source, destination)

                    def injected_encoding(value, *args, **kwargs):
                        if operation == "serialize" and value.get("evidence_state") == "complete":
                            raise OSError("primary publication failure")
                        return dumps(value, *args, **kwargs)

                    def first_pending_signal():
                        nonlocal fired
                        if not fired:
                            fired = True
                            os.kill(os.getpid(), signal.SIGTERM)
                        return pending()

                    def late_second_signal(numbers):
                        os.kill(os.getpid(), signal.SIGINT)
                        return wait(numbers)

                    try:
                        signal.signal(signal.SIGINT, signal.default_int_handler)
                        with patch.object(Path, "write_text", injected_write), \
                                patch.object(runner.os, "replace", side_effect=injected_rename), \
                                patch.object(runner.json, "dumps", side_effect=injected_encoding), \
                                patch.object(runner.signal, "sigpending", side_effect=first_pending_signal), \
                                patch.object(runner.signal, "sigwait", side_effect=late_second_signal):
                            try:
                                self.run_child("import sys; print('primary retained', flush=True); sys.exit(" + str(code) + ")")
                            except BaseException as error:
                                observed = error
                        self.assertIs(previous[signal.SIGTERM], signal.getsignal(signal.SIGTERM))
                        self.assertIs(signal.default_int_handler, signal.getsignal(signal.SIGINT))
                        self.assertEqual(old_mask, signal.pthread_sigmask(signal.SIG_BLOCK, set()))
                    finally:
                        for item, handler in previous.items():
                            signal.signal(item, handler)
                    self.assertIsInstance(observed, OSError)
                    self.assertEqual("primary publication failure", str(observed))
                    saved = json.loads((directory / (label + ".command.json")).read_text())
                    self.assertEqual(code, saved["exit"])
                    self.assertEqual("incomplete", saved["evidence_state"])
                    self.assertFalse(runner.successful(saved))
                    self.assertEqual("primary retained\n", (directory / (label + ".stdout")).read_text())

    def test_handled_caller_exception_does_not_suppress_command_cancellation(self):
        pending = signal.sigpending
        wait = signal.sigwait
        for mode in ("term", "mixed-default", "mixed-custom"):
            for code in (0, 9):
                with self.subTest(mode=mode, exit=code):
                    previous = {item: signal.getsignal(item) for item in (signal.SIGTERM, signal.SIGINT)}
                    old_mask = signal.pthread_sigmask(signal.SIG_BLOCK, set())
                    caller_error = ValueError("already handled by caller")
                    observed = None
                    fired = False

                    def first_pending_signal():
                        nonlocal fired
                        if not fired:
                            fired = True
                            os.kill(os.getpid(), signal.SIGTERM)
                        return pending()

                    def second_signal(numbers):
                        if mode != "term":
                            os.kill(os.getpid(), signal.SIGINT)
                        return wait(numbers)

                    def custom_interrupt(number, frame):
                        raise runner.CommandInterrupted(number)

                    try:
                        signal.signal(signal.SIGINT, custom_interrupt if mode == "mixed-custom" else signal.default_int_handler)
                        try:
                            raise caller_error
                        except ValueError:
                            with patch.object(runner.signal, "sigpending", side_effect=first_pending_signal), \
                                    patch.object(runner.signal, "sigwait", side_effect=second_signal):
                                try:
                                    self.run_child("import sys; print('completed child', flush=True); sys.exit(" + str(code) + ")")
                                except BaseException as error:
                                    observed = error
                        self.assertIsInstance(observed, (KeyboardInterrupt, runner.CommandInterrupted))
                        self.assertFalse(hasattr(caller_error, "__notes__"))
                        self.assertEqual(old_mask, signal.pthread_sigmask(signal.SIG_BLOCK, set()))
                        self.assertIs(previous[signal.SIGTERM], signal.getsignal(signal.SIGTERM))
                        self.assertIs(custom_interrupt if mode == "mixed-custom" else signal.default_int_handler,
                                      signal.getsignal(signal.SIGINT))
                    finally:
                        for item, handler in previous.items():
                            signal.signal(item, handler)


if __name__ == "__main__":
    unittest.main()
