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
from unittest.mock import Mock, patch

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

    def test_child_event_is_readable_before_eof_and_record_publication(self):
        read = runner.os.read
        received = False
        observed = False

        def check_live_file(descriptor, size):
            nonlocal received, observed
            if received:
                self.assertEqual("live-event\n", (Path(self.scratch.name) / "case0.stdout").read_text())
                self.assertFalse((Path(self.scratch.name) / "case0.command.json").exists())
                observed = True
            chunk = read(descriptor, size)
            if chunk == b"live-event\n":
                received = True
            return chunk

        with patch.object(runner.os, "read", side_effect=check_live_file):
            record = self.run_child("print('live-event', flush=True)")
        self.assertTrue(observed)
        self.assertTrue(runner.successful(record))

    @unittest.skipUnless(platform.system() == "Darwin", "No-window presentation probe requires macOS frameworks")
    def test_presentation_rejects_inactive_offspace_occluded_and_offscreen_windows(self):
        directory = Path(self.scratch.name)
        source = directory / "presentation.m"
        source.write_text('#define main qualification_host_main\n#import ' +
                          json.dumps(str(runner.ROOT / "tooling/owner-qualification/host.m")) +
                          '\n#undef main\n' + '''
@interface ProbeWindow : NSObject
@property BOOL activeSpace;
@property BOOL key;
@property BOOL shown;
@property BOOL mini;
@property NSInteger number;
@property NSWindowOcclusionState occlusionState;
@property NSRect frame;
@end
@implementation ProbeWindow
- (BOOL)isOnActiveSpace
{
    return self.activeSpace;
}
- (BOOL)isKeyWindow
{
    return self.key;
}
- (BOOL)isVisible
{
    return self.shown;
}
- (BOOL)isMiniaturized
{
    return self.mini;
}
- (NSInteger)windowNumber
{
    return self.number;
}
@end
@interface ProbeApplication : NSObject
@property BOOL focused;
@property(strong) NSArray<NSWindow *> *windows;
@end
@implementation ProbeApplication
- (BOOL)isActive
{
    return self.focused;
}
@end
@interface ProbeScreen : NSObject
@property NSRect visibleFrame;
@end
@implementation ProbeScreen
@end
int main(void)
{
    @autoreleasepool
    {
        // NSObject doubles only: no NSApplication/NSWindow creation or OS evaluation.
        ProbeWindow *window = [ProbeWindow new];
        ProbeApplication *application = [ProbeApplication new];
        ProbeScreen *screen = [ProbeScreen new];
        NSWindow *windowBitsOnly = (NSWindow *)(id)window;
        NSApplication *appBitsOnly = (NSApplication *)(id)application;
        NSArray<NSScreen *> *screens = @[(NSScreen *)(id)screen];
        application.windows = @[windowBitsOnly];
        application.focused = YES;
        window.activeSpace = YES;
        window.key = YES;
        window.shown = YES;
        window.number = 1;
        window.occlusionState = NSWindowOcclusionStateVisible;
        screen.visibleFrame = NSMakeRect(-1600, -900, 1200, 800);
        window.frame = NSMakeRect(-1500, -800, 760, 250);
        if (!presentationReady(windowBitsOnly, appBitsOnly, screens))
        {
            return 1;
        }
        application.focused = NO;
        if (presentationReady(windowBitsOnly, appBitsOnly, screens) ||
            presentationFailures(windowBitsOnly, appBitsOnly, screens) != PresentationAppInactive)
        {
            return 2;
        }
        application.focused = YES;
        window.key = NO;
        if (presentationReady(windowBitsOnly, appBitsOnly, screens) ||
            presentationFailures(windowBitsOnly, appBitsOnly, screens) != PresentationNotKey)
        {
            return 3;
        }
        window.key = YES;
        window.activeSpace = NO;
        if (presentationReady(windowBitsOnly, appBitsOnly, screens) ||
            presentationFailures(windowBitsOnly, appBitsOnly, screens) != PresentationOffSpace)
        {
            return 4;
        }
        window.activeSpace = YES;
        window.occlusionState = 0;
        if (presentationReady(windowBitsOnly, appBitsOnly, screens) ||
            presentationFailures(windowBitsOnly, appBitsOnly, screens) != PresentationOccluded)
        {
            return 5;
        }
        window.occlusionState = NSWindowOcclusionStateVisible;
        window.frame = NSMakeRect(160, 160, 760, 250);
        if (presentationReady(windowBitsOnly, appBitsOnly, screens) ||
            presentationFailures(windowBitsOnly, appBitsOnly, screens) != PresentationOutsideScreen)
        {
            return 6;
        }
        window.frame = NSMakeRect(-500, -800, 760, 250);
        if (presentationReady(windowBitsOnly, appBitsOnly, screens) ||
            presentationFailures(windowBitsOnly, appBitsOnly, screens) != PresentationOutsideScreen)
        {
            return 7;
        }
        window.frame = NSMakeRect(-1500, -800, 760, 250);
        application.windows = @[];
        if (presentationReady(windowBitsOnly, appBitsOnly, screens) ||
            presentationFailures(windowBitsOnly, appBitsOnly, screens) != PresentationNotOwned)
        {
            return 8;
        }
        application.windows = @[windowBitsOnly];
        window.shown = NO;
        if (presentationFailures(windowBitsOnly, appBitsOnly, screens) != PresentationNotVisible)
        {
            return 9;
        }
        window.shown = YES;
        window.mini = YES;
        if (presentationFailures(windowBitsOnly, appBitsOnly, screens) != PresentationMiniaturized)
        {
            return 10;
        }
        window.mini = NO;
        window.number = 0;
        if (presentationFailures(windowBitsOnly, appBitsOnly, screens) != PresentationNoWindowNumber)
        {
            return 11;
        }
        window.number = 1;
        window.frame = NSZeroRect;
        if (presentationFailures(windowBitsOnly, appBitsOnly, screens) != PresentationOutsideScreen)
        {
            return 12;
        }
        window.frame = NSMakeRect(-1500, -800, 760, 250);
        if (presentationFailures(nil, appBitsOnly, screens) != PresentationMissingObject)
        {
            return 13;
        }
        if (presentationFailures(windowBitsOnly, nil, screens) != PresentationMissingObject)
        {
            return 14;
        }
        if (presentationFailures(windowBitsOnly, appBitsOnly, @[]) != PresentationOutsideScreen)
        {
            return 15;
        }
        reportPresentationFailures(0); // Emits nothing, never a success assertion.
        application.focused = NO;
        window.key = NO;
        uint32_t combined = presentationFailures(windowBitsOnly, appBitsOnly, screens);
        if (combined != (PresentationAppInactive | PresentationNotKey))
        {
            return 16;
        }
        reportPresentationFailures(combined);
        // Exercise every fixed label with synthetic bits only; no OS session query.
        reportPresentationFailures((1u << 12) - 1);
        puts("PASS: presentation metadata refusals; no window or authentication");
    }
    return 0;
}
''')
        binary = directory / "presentation"
        build = runner.command(["/usr/bin/clang", "-fobjc-arc", "-fblocks", "-Wall", "-Wextra", "-Werror",
                                "-I", str(runner.NATIVE), str(source), str(runner.NATIVE / "mac-owner.m"),
                                "-framework", "Foundation", "-framework", "AppKit",
                                "-framework", "LocalAuthentication", "-framework", "CoreGraphics",
                                "-o", str(binary)], self.environment, directory, "presentation-build", 60)
        self.assertTrue(runner.successful(build), (directory / "presentation-build.stderr").read_text())
        action = runner.command([str(binary)], self.environment, directory, "presentation-action", 5)
        self.assertTrue(runner.successful(action), (directory / "presentation-action.stderr").read_text())
        output = (directory / "presentation-action.stdout").read_text()
        self.assertIn("PASS: presentation metadata refusals", output)
        diagnostics = [json.loads(line) for line in output.splitlines() if line.startswith("{")]
        reasons = [row["state"] for row in diagnostics]
        self.assertEqual(["application-inactive", "window-not-key"], reasons[:2])
        self.assertEqual(12, len(set(reasons[2:])))
        self.assertEqual(14, len(reasons))
        self.assertTrue(all(row["event"] == "presentation-prerequisite" for row in diagnostics))
        self.assertNotIn("PASS", reasons)
        self.assertNotIn("FAIL", reasons)

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
        self.assertTrue(record["group_reaped"], json.dumps(record))
        self.assertEqual(0, record["exit"])

    def test_reaping_requires_disappearance_even_after_longer_grace(self):
        process = Mock(pid=12345)
        with patch.object(runner.os, "killpg") as kill, \
                patch.object(runner.time, "monotonic", side_effect=[0, 11]), \
                patch.object(runner.time, "sleep"):
            self.assertFalse(runner.reap_group(process))
        self.assertEqual([(12345, signal.SIGKILL), (12345, 0)],
                         [call.args for call in kill.call_args_list])
        process.wait.assert_called_once_with(timeout=5)

    def test_delayed_disappearance_after_old_grace_is_proven_not_assumed(self):
        process = Mock(pid=12345)
        with patch.object(runner.os, "killpg", side_effect=[None, None, ProcessLookupError()]), \
                patch.object(runner.time, "monotonic", side_effect=[0, 5]), \
                patch.object(runner.time, "sleep"):
            self.assertTrue(runner.reap_group(process))

    def test_permission_denial_needs_later_explicit_group_absence(self):
        for signals in ([PermissionError("kill denied"), ProcessLookupError()],
                        [None, PermissionError("probe denied"), ProcessLookupError()],
                        [PermissionError("kill denied"), PermissionError("probe denied"), ProcessLookupError()]):
            with self.subTest(signals=signals):
                process = Mock(pid=12345)
                with patch.object(runner.os, "killpg", side_effect=signals) as kill, \
                        patch.object(runner.time, "monotonic", side_effect=[0, 1]), \
                        patch.object(runner.time, "sleep") as sleep:
                    self.assertTrue(runner.reap_group(process))
                process.wait.assert_called_once_with(timeout=5)
                self.assertEqual([(12345, signal.SIGKILL)] + [(12345, 0)] * (len(signals) - 1),
                                 [call.args for call in kill.call_args_list])
                self.assertEqual(len(signals) - 2, sleep.call_count)

    def test_permission_denial_without_disappearance_preserves_first_error(self):
        for initial_denial, probe_denial in ((True, False), (False, True), (True, True)):
            with self.subTest(initial_denial=initial_denial, probe_denial=probe_denial):
                first = PermissionError("kill denied")
                second = PermissionError("probe denied")
                process = Mock(pid=12345)
                with patch.object(runner.os, "killpg", side_effect=[first if initial_denial else None,
                                                                   second if probe_denial else None]) as kill, \
                        patch.object(runner.time, "monotonic", side_effect=[0, 11]), \
                        patch.object(runner.time, "sleep") as sleep:
                    with self.assertRaises(PermissionError) as caught:
                        runner.reap_group(process)
                self.assertIs(first if initial_denial else second, caught.exception)
                process.wait.assert_called_once_with(timeout=5)
                self.assertEqual([(12345, signal.SIGKILL), (12345, 0)],
                                 [call.args for call in kill.call_args_list])
                sleep.assert_not_called()

    def test_denied_kill_preserves_first_error_if_leader_wait_fails(self):
        denied = PermissionError("kill denied")
        timeout = runner.subprocess.TimeoutExpired("owned child", 5)
        process = Mock(pid=12345)
        process.wait.side_effect = timeout
        with patch.object(runner.os, "killpg", side_effect=denied) as kill:
            with self.assertRaises(PermissionError) as caught:
                runner.reap_group(process)
        self.assertIs(denied, caught.exception)
        self.assertIs(timeout, caught.exception.__cause__)
        process.wait.assert_called_once_with(timeout=5)
        kill.assert_called_once_with(12345, signal.SIGKILL)

    def test_cleanup_diagnostics_only_disclose_owned_group_numeric_state(self):
        result = Mock(stdout="11 55 Z\n12 99 S\n13 55 R+\nmalformed\n")
        with patch.object(runner.subprocess, "run", return_value=result):
            self.assertEqual({"available": True, "members": [
                {"pid": 11, "state": "Z"}, {"pid": 13, "state": "R+"}]},
                runner.group_diagnostics(55))

    def test_unproven_disappearance_cannot_pass_even_with_no_live_members_in_snapshot(self):
        with patch.object(runner, "reap_group", return_value=False), \
                patch.object(runner, "group_diagnostics", return_value={"available": True, "members": []}):
            record = self.run_child("pass")
        self.assertEqual(0, record["exit"])
        self.assertFalse(record["group_reaped"])
        self.assertFalse(runner.successful(record))

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
