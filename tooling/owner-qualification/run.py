#!/usr/bin/env python3
"""Local disposable host runner. No default mode and no automatic authentication."""
import ctypes
import hashlib
import json
import os
from pathlib import Path
import platform
import plistlib
import re
import selectors
import signal
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[2]
PRODUCTION_BASE = "0df7245e52e910d12be00c4155c53ee03be6f03e"
NATIVE = ROOT / "src/ChunkMaestro.OwnerVerification/Native"
EXPECTED = {
    "mac-owner.m": "6e0caadfccc813098ea6e8d1415d35510f12d8c76cb8eca207731b3d412949b6",
    "mac-owner.h": "90442d7d4f34e39a2b513bb114b83cc3a13d610fc7ac69a27b7e8d2b0b8a68fc",
}
LIMIT = 1024 * 1024


def mode_from(args, environment):
    # Both refusals precede temp creation, ctypes framework loading and all children.
    if "GITHUB_ACTIONS" in environment:
        raise ValueError("REFUSED: hosted GITHUB_ACTIONS")
    if (len(args) != 4 or args[0] not in ("--no-prompt", "--interactive")
            or args[1] != "--reviewed-head" or not re.fullmatch(r"[0-9a-f]{40}", args[2])
            or args[3] != "--desktop-confirmed-unlocked"):
        raise ValueError("REFUSED: select a mode, --reviewed-head SHA and --desktop-confirmed-unlocked")
    return args[0], args[2]


def graphical_preflight():
    if platform.system() != "Darwin" or os.geteuid() == 0:
        return {"available": False, "reason": "local-unprivileged-macOS-required"}
    cg = ctypes.CDLL("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")
    cf = ctypes.CDLL("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")
    cg.CGSessionCopyCurrentDictionary.restype = ctypes.c_void_p
    cf.CFPropertyListCreateData.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_long, ctypes.c_ulong, ctypes.c_void_p]
    cf.CFPropertyListCreateData.restype = ctypes.c_void_p
    cf.CFDataGetLength.argtypes = [ctypes.c_void_p]
    cf.CFDataGetLength.restype = ctypes.c_long
    cf.CFDataGetBytePtr.argtypes = [ctypes.c_void_p]
    cf.CFDataGetBytePtr.restype = ctypes.c_void_p
    cf.CFRelease.argtypes = [ctypes.c_void_p]
    session = cg.CGSessionCopyCurrentDictionary()
    if not session:
        return {"available": False, "reason": "no-session"}
    data = None
    try:
        data = cf.CFPropertyListCreateData(None, session, 100, 0, None)
        if not data:
            return {"available": False, "reason": "session-query-unavailable"}
        value = plistlib.loads(ctypes.string_at(cf.CFDataGetBytePtr(data), cf.CFDataGetLength(data)))
        console = value.get("kCGSSessionOnConsoleKey") is True
        logged_in = value.get("kCGSessionLoginDoneKey") is True
        same_uid = value.get("kCGSSessionUserIDKey") == os.getuid()
        return {"available": console and logged_in and same_uid,
                "console": console, "login_done": logged_in, "same_uid": same_uid,
                "lock_state": "not-attested-operator-confirmation-required",
                "limitation": "public-console-metadata-not-owner-or-lock-attestation"}
    finally:
        if data:
            cf.CFRelease(data)
        cf.CFRelease(session)


def clean_environment(build):
    home = build / "home"
    home.mkdir()
    return {"PATH": "/usr/bin:/bin:/usr/sbin:/sbin", "HOME": str(home),
            "TMPDIR": str(build) + "/", "LANG": "C", "LC_ALL": "C"}


def reap_group(process):
    """Kill the owned session even if its leader already exited, then reap leader."""
    try:
        os.killpg(process.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass
    process.wait(timeout=5)
    # Signal zero proves the group is gone, rather than trusting only leader exit.
    end = time.monotonic() + 3
    while True:
        try:
            os.killpg(process.pid, 0)
        except ProcessLookupError:
            return True
        if time.monotonic() >= end:
            return False
        time.sleep(0.02)


def command(args, env, directory, label, deadline):
    record = {"argv": [str(arg) for arg in args], "environment": env,
              "cwd": str(directory), "deadline_seconds": deadline}
    start = time.monotonic()
    process = None
    selector = selectors.DefaultSelector()
    total = 0
    reason = "exited"
    cleaning_up = False
    record_sealed = False
    restoring_handlers = False
    restoration_signal = None
    previous = {number: signal.getsignal(number) for number in (signal.SIGTERM, signal.SIGINT)}

    def interrupted(number, frame):
        nonlocal restoration_signal
        if restoring_handlers:
            restoration_signal = restoration_signal or number
            return
        if cleaning_up:
            if not record_sealed:
                record.setdefault("cleanup_signal", number)
                return
            if record.get("cleanup_signal"):
                # A first cancellation already makes the immutable final write
                # fail. Repeated signals cannot change that committed snapshot.
                return
        raise CommandInterrupted(number)

    try:
        for number in previous:
            signal.signal(number, interrupted)
        process = subprocess.Popen(record["argv"], cwd=directory, env=env, stdin=subprocess.DEVNULL,
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=True)
        for pipe, name in ((process.stdout, "stdout"), (process.stderr, "stderr")):
            os.set_blocking(pipe.fileno(), False)
            selector.register(pipe, selectors.EVENT_READ, name)
        with (directory / (label + ".stdout")).open("wb") as out, (directory / (label + ".stderr")).open("wb") as err:
            streams = {"stdout": out, "stderr": err}
            while selector.get_map():
                if time.monotonic() - start >= deadline:
                    reason = "timeout"
                    break
                for key, _ in selector.select(0.05):
                    chunk = os.read(key.fileobj.fileno(), 8192)
                    if not chunk:
                        selector.unregister(key.fileobj)
                        continue
                    remaining = LIMIT - total
                    streams[key.data].write(chunk[:remaining])
                    total += min(len(chunk), remaining)
                    if len(chunk) > remaining:
                        reason = "output-limit"
                        break
                if reason != "exited":
                    break
            # Pipes can close before the process exits; remaining wait is still finite.
            if reason == "exited":
                try:
                    process.wait(timeout=max(0.01, deadline - (time.monotonic() - start)))
                except subprocess.TimeoutExpired:
                    reason = "timeout"
    except OSError as error:
        reason = "launch-error"
        record["error_category"] = type(error).__name__
    except (KeyboardInterrupt, CommandInterrupted) as error:
        reason = "interrupted"
        record["signal"] = error.number if isinstance(error, CommandInterrupted) else signal.SIGINT
    finally:
        # Repeated cancellation must not interrupt preservation of the first
        # result. Signals remain failures, but are deferred through the record.
        cleaning_up = True
        try:
            record["group_reaped"] = reap_group(process) if process else True
        except (OSError, subprocess.SubprocessError, KeyboardInterrupt, CommandInterrupted) as error:
            record["group_reaped"] = False
            record["cleanup_error_category"] = type(error).__name__
        record["exit"] = process.returncode if process else None
        record["reason"] = reason
        record["seconds"] = round(time.monotonic() - start, 3)
        record["output_bytes"] = total
        try:
            selector.close()
            if process:
                process.stdout.close()
                process.stderr.close()
        except OSError as error:
            record["cleanup_error_category"] = type(error).__name__
        publication_error = None
        try:
            target = directory / (label + ".command.json")
            pending = target.with_suffix(target.suffix + ".pending")
            # Copy before encoding: signal collection never mutates the encoder's
            # dictionary. Publish whole JSON bytes, not a partially written file.
            incomplete = dict(record, evidence_state="incomplete")
            payload = json.dumps(incomplete, indent=2) + "\n"
            pending.write_text(payload)
            os.replace(pending, target)
            # Freeze the final snapshot before encoding. A new cancellation now
            # interrupts the caller; incomplete evidence remains if this fails.
            record_sealed = True
            record["evidence_state"] = "complete"
            final_payload = json.dumps(dict(record), indent=2) + "\n"
            pending.write_text(final_payload)
            os.replace(pending, target)
        except BaseException as error:
            publication_error = error
            raise
        finally:
            # Defer delivery until every previous process-global handler is back.
            # The flag covers arrival immediately before the mask is installed.
            restoring_handlers = True
            old_mask = signal.pthread_sigmask(signal.SIG_BLOCK, previous)
            try:
                for number, handler in previous.items():
                    signal.signal(number, handler)
                pending_signals = signal.sigpending().intersection(previous).difference(old_mask)
                for number in sorted(pending_signals):
                    signal.sigwait({number})
                    restoration_signal = restoration_signal or number
            finally:
                try:
                    signal.pthread_sigmask(signal.SIG_SETMASK, old_mask)
                except (KeyboardInterrupt, CommandInterrupted) as error:
                    # Delivery can occur as the restored mask takes effect,
                    # including a signal arriving after the pending snapshot.
                    # Keep the first publication failure as the active error.
                    if publication_error is None:
                        raise
                    publication_error.add_note("Cancellation during restored-mask delivery: " + type(error).__name__)
            if restoration_signal is not None and publication_error is None:
                raise CommandInterrupted(restoration_signal)
    return record


class CommandInterrupted(Exception):
    def __init__(self, number):
        self.number = number


class SourceBindingError(ValueError):
    def __init__(self, reason):
        self.reason = reason
        super().__init__(reason)


def snapshot_sources(head, env, build):
    """Compile a private copy of exact committed bytes, not mutable live sources."""
    relative = ["tooling/owner-qualification/host.m", "tooling/owner-qualification/run.py",
                "tooling/owner-qualification/test_runner.py", "docs/qa/owner-native-qualification.md"]
    relative += ["src/ChunkMaestro.OwnerVerification/Native/" + name for name in EXPECTED]
    manifest, records = {}, []
    for index, name in enumerate(relative):
        label = "source-" + str(index)
        record = command(["/usr/bin/git", "-C", str(ROOT), "cat-file", "blob", head + ":" + name],
                         env, build, label, 10)
        records.append(record)
        if not successful(record):
            raise SourceBindingError("reviewed-source-unavailable")
        source = build / (label + ".stdout")
        value = digest(source)
        if value != digest(ROOT / name):
            raise SourceBindingError("uncommitted-source-mismatch")
        if name.startswith("src/") and value != EXPECTED[Path(name).name]:
            raise SourceBindingError("production-source-mismatch")
        target = build / "source" / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(source.read_bytes())
        manifest[name] = value
    return manifest, records


def successful(record):
    return (record.get("evidence_state") == "complete"
            and record["reason"] == "exited" and record["exit"] == 0 and record["group_reaped"]
            and not record.get("cleanup_signal") and not record.get("cleanup_error_category"))


def no_prompt_observations(path):
    records = [json.loads(line) for line in path.read_text().splitlines()]
    required = {"production-abi2", "abi2-create", "foreign-bits-refused", "stale-generation-refused",
                "off-main-capture-refused", "actual-positive-capture", "single-use-binding",
                "probe-no-authentication", "cancel", "release", "released-ingress-refused",
                "fresh-request-capture", "real-will-close-revocation", "close-release", "no-prompt-complete"}
    passed = {record.get("event") for record in records if record.get("state") == "PASS"}
    fences = {record.get("event") for record in records if record.get("state", "").startswith("main-queue-fence-observed")}
    safe = {"event": "evaluation-dispatch", "state": "NOT-CALLED-by-no-prompt-control-flow"} in records
    valid = required <= passed and {"release-observer-cleanup", "close-observer-cleanup"} <= fences and safe
    valid = valid and all(record.get("state") != "FAIL" for record in records)
    return valid, records


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main(args=None):
    try:
        mode, reviewed_head = mode_from(sys.argv[1:] if args is None else args, os.environ)
    except ValueError as error:
        print(str(error), file=sys.stderr)
        return 64
    bundle = Path(tempfile.mkdtemp(prefix="chunkmaestro-owner-qualification-", dir="/private/tmp" if platform.system() == "Darwin" else None))
    build = bundle / "build"
    build.mkdir()
    env = clean_environment(build)
    report = {"production_base": PRODUCTION_BASE, "reviewed_head": reviewed_head,
              "operator_desktop_confirmation": True, "mode": mode, "status": "NOT RUN",
              "authentication": "NOT AUTHORIZED by fixture creation; no automatic opt-in",
              "os": platform.system(), "release": platform.release(), "architecture": platform.machine(),
              "python": sys.version, "bundle": str(bundle), "commands": [],
              "screen_evidence": "NOT RUN; NSWindow metadata is not independent screen evidence",
              "coverage": "diagnostic only; not measured", "build_cleanup": "retained; no cleanup before tree reaping"}
    result = 77
    try:
        report["preflight"] = graphical_preflight()
        if not report["preflight"]["available"]:
            report["status"] = "UNAVAILABLE"
            return result
        if any(digest(NATIVE / name) != expected for name, expected in EXPECTED.items()):
            report["status"] = "REFUSED-production-source-mismatch"
            return 65
        if not Path("/usr/bin/clang").is_file():
            report["status"] = "UNAVAILABLE-compiler"
            return 77
        for label, arguments in (("head", ["/usr/bin/git", "-C", str(ROOT), "rev-parse", "HEAD"]),
                                 ("compiler-version", ["/usr/bin/clang", "--version"])):
            record = command(arguments, env, build, label, 10)
            report["commands"].append(record)
            if not successful(record):
                report["status"] = "FAIL-provenance"
                return 1
        report["actual_head"] = (build / "head.stdout").read_text().strip()
        if report["actual_head"] != reviewed_head:
            report["status"] = "REFUSED-nonreviewed-head"
            return 65
        manifest, records = snapshot_sources(reviewed_head, env, build)
        report["commands"].extend(records)
        report["compiled_sources"] = manifest
        snapshot = build / "source"
        native_snapshot = snapshot / "src/ChunkMaestro.OwnerVerification/Native"
        compiler = ["/usr/bin/clang", "-fobjc-arc", "-fblocks", "-Wall", "-Wextra", "-Werror",
                    "-I", str(native_snapshot), str(snapshot / "tooling/owner-qualification/host.m"),
                    str(native_snapshot / "mac-owner.m"), "-framework", "Foundation", "-framework", "AppKit",
                    "-framework", "LocalAuthentication", "-framework", "CoreGraphics", "-o", str(build / "host")]
        record = command(compiler, env, build, "compile", 60)
        report["commands"].append(record)
        if not successful(record):
            report["status"] = "FAIL-build"
            return 1
        record = command([str(build / "host"), mode], env, build, "host", 180 if mode == "--interactive" else 20)
        report["commands"].append(record)
        if mode == "--no-prompt" and successful(record):
            valid, observations = no_prompt_observations(build / "host.stdout")
            report["observations"] = observations
            if not valid:
                report["status"] = "FAIL-incomplete-native-observations"
                return 1
        report["status"] = "PASS-no-prompt" if successful(record) and mode == "--no-prompt" else (
            "interactive-diagnostic-only" if successful(record) else "UNAVAILABLE" if record["exit"] == 77 else "FAIL-host")
        return 0 if successful(record) else 1
    except SourceBindingError as error:
        report["status"] = "REFUSED-provenance"
        report["reason"] = error.reason
        return 65
    except Exception as error:
        report["status"] = "FAIL-runner"
        report["error_category"] = type(error).__name__
        return 1
    finally:
        sources = list((ROOT / "tooling/owner-qualification").glob("*.py")) + [ROOT / "tooling/owner-qualification/host.m"]
        sources += [NATIVE / name for name in EXPECTED]
        sources += [ROOT / "docs/qa/owner-native-qualification.md"]
        source_manifest = report.get("compiled_sources", {})
        (bundle / "source-manifest.json").write_text(json.dumps(source_manifest, indent=2) + "\n")
        report["live_source_hashes_after_run"] = {str(path.relative_to(ROOT)): digest(path) for path in sources if path.exists()}
        (bundle / "report.json").write_text(json.dumps(report, indent=2) + "\n")
        (bundle / "artifact-manifest.json").write_text(json.dumps({str(path.relative_to(bundle)): digest(path) for path in bundle.rglob("*") if path.is_file()}, indent=2) + "\n")
        print(str(bundle))


if __name__ == "__main__":
    sys.exit(main())
