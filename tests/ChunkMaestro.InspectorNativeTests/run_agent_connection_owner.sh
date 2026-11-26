#!/bin/sh
set -eu
if [ "$(uname -s)" != Linux ]; then
    echo 'NOTRUN: N1 requires actual Linux; no platform simulation' >&2
    exit 77
fi
if [ "$(id -u)" = 0 ]; then
    echo 'NOTRUN: N1 requires an ordinary user in a disposable fixture' >&2
    exit 77
fi
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
artifacts=${CM_N1_ARTIFACTS:-"$root/.artifacts/ci/agent-connection-owner"}
mkdir -p "$artifacts"
build=$(mktemp -d "$artifacts/run-XXXXXX")
# Keep scratch on failure: a timeout or unknown cleanup is not confirmed teardown.
exec < /dev/null > "$build/raw.log" 2>&1
set -x
compiler=${CC:-cc}
native="$root/src/ChunkMaestro.Inspector.Native"
tests="$root/tests/ChunkMaestro.InspectorNativeTests"
uname -a
id
"$compiler" --version
sha256sum "$native/agent_connection_owner_unqualified.c" "$native/agent_connection_owner_unqualified.h" \
    "$native/session_escrow.c" "$native/session_escrow.h" \
    "$tests/agent_connection_owner_tests.c" "$0"
for variant in plain sanitized; do
    set --
    if [ "$variant" = sanitized ]; then
        set -- -fsanitize=address,undefined -fno-omit-frame-pointer
    fi
    # Separately compile production owner as well, catching standalone build drift.
    "$compiler" -std=c11 -O1 -g -Wall -Wextra -Werror -Wpedantic -pthread "$@" \
        -I"$native" -c "$native/agent_connection_owner_unqualified.c" -o "$build/owner-$variant.o"
    for kind in deterministic kernel; do
        controls=
        if [ "$kind" = deterministic ]; then
            controls=-DCM_OWNER_CONTROLS
        fi
        "$compiler" -std=c11 -O1 -g -Wall -Wextra -Werror -Wpedantic -pthread "$@" $controls \
            -I"$native" "$tests/agent_connection_owner_tests.c" "$native/session_escrow.c" \
            -Wl,-l:libsystemd.so.0 -o "$build/$kind-$variant"
        ASAN_OPTIONS=detect_leaks=1 UBSAN_OPTIONS=halt_on_error=1 \
            timeout 75 "$build/$kind-$variant" > "$build/$kind-$variant.log" 2>&1
        cat "$build/$kind-$variant.log"
    done
done
# Candidate's unchanged deterministic fault harness supplies real candidate
# short-output/dup/original-close tests; owner fixture supplies composition tests.
sh "$tests/run_agent_candidate.sh" > "$build/baseline-candidate.log" 2>&1
# Generate mutations outside checkout. Each must compile and fail its own tagged
# behavioral assertion; compilation failure or a sanitizer crash is NOT detection.
python3 - "$native" "$tests" "$build" <<'PY'
from pathlib import Path
import sys
native, tests, build = map(Path, sys.argv[1:])
s = (native/'agent_connection_owner_unqualified.c').read_text()
mutations = {
 'endpoint': ('cm_agent_candidate_acquire_v1(s->accepted,', 'cm_agent_candidate_acquire_v1(s->listener,', 'fd == live->accepted', 'deterministic'),
 'accept-retry': ('s->accepted = accept4(s->listener, NULL, NULL, SOCK_CLOEXEC | SOCK_NONBLOCK);', 's->accepted = accept4(s->listener, NULL, NULL, SOCK_CLOEXEC | SOCK_NONBLOCK); if (s->accepted < 0) { s->accepted = accept4(s->listener, NULL, NULL, SOCK_CLOEXEC | SOCK_NONBLOCK); }', 'accept_calls == 1', 'deterministic'),
 'cloexec': ('return descriptor == FD_CLOEXEC && status >= 0 && (status & O_NONBLOCK);', '(void)descriptor; return status >= 0 && (status & O_NONBLOCK);', 'CLOEXEC_SENSITIVITY', 'deterministic'),
 'zero': ('if (fd >= 0 && close(fd) < 0)', 'if (fd > 0 && close(fd) < 0)', 'fcntl(0, F_GETFD) < 0', 'deterministic'),
 'close-retry': ('fd >= 0 && close(fd) < 0', 'fd >= 0 && close(fd) < 0 && close(fd) < 0', 'fcntl(reuse_fd, F_GETFD) >= 0', 'deterministic'),
 'secondary': ('cm_agent_candidate_close_v1(&s->candidate, &s->diagnostics.candidate);', 'cm_agent_candidate_close_v1(&s->candidate, &s->diagnostics.candidate); s->diagnostics.candidate.cleanup_code = 0;', 'd.candidate.cleanup_code == CM_ESCROW_IO', 'deterministic'),
 'revoke': ('if (ok)\n    {\n        *out = value;', 'if (ok || atomic_load(&s->revoked))\n    {\n        *out = value;', '!memcmp(&value, &zero, sizeof(value))', 'deterministic'),
 'lifetime': ('/* Keep storage and the mutex alive', 'free(s); /* Keep storage and the mutex alive', 'owner_free_calls == 0', 'deterministic'),
 'unlink': ('/* Never unlink:', 'if (s->diagnostics.attempted) { unlink("REPLACE_ENDPOINT"); } /* Never unlink:', 'lstat(endpoint, &st) == 0', 'kernel'),
}
# The unlink variant needs the actual bound path retained privately only in its
# mutated struct; no additional path field/getter is in the production source.
for name, (old, new, expected, kind) in mutations.items():
 assert s.count(old) == 1, name
 v=s.replace(old,new)
 if name=='unlink':
  v=v.replace('int directory;', 'char mutation_path[108]; int directory;')
  v=v.replace('struct stat existing;', 'strcpy(s->mutation_path, path); struct stat existing;')
  v=v.replace('"REPLACE_ENDPOINT"','s->mutation_path')
 d=build/name;d.mkdir()
 (d/'agent_connection_owner_unqualified.c').write_text(v)
 (d/'agent_connection_owner_tests.c').write_bytes((tests/'agent_connection_owner_tests.c').read_bytes())
 (d/'expected').write_text(expected+'\n')
 (d/'kind').write_text(kind+'\n')
PY
for mutation in endpoint accept-retry cloexec zero close-retry secondary revoke lifetime unlink; do
    controls=-DCM_OWNER_CONTROLS
    if [ "$mutation" = unlink ]; then
        controls=
    fi
    "$compiler" -std=c11 -O1 -g -Wall -Wextra -Werror -Wpedantic -pthread $controls \
        -I"$native" "$build/$mutation/agent_connection_owner_tests.c" "$native/session_escrow.c" \
        -Wl,-l:libsystemd.so.0 -o "$build/$mutation/test"
    if timeout 75 "$build/$mutation/test" > "$build/$mutation/result.log" 2>&1; then
        echo "FAIL escaped compiled control $mutation"
        exit 1
    fi
    grep -F -f "$build/$mutation/expected" "$build/$mutation/result.log"
done
# Optional real compiler coverage, diagnostic only. No installs or score gate.
if "$compiler" --version | grep -qi 'gcc\|Free Software Foundation'; then
    mkdir "$build/coverage"
    (
        cd "$build/coverage"
        "$compiler" -std=c11 -O0 -g --coverage -pthread -DCM_OWNER_CONTROLS -I"$native" \
            "$tests/agent_connection_owner_tests.c" "$native/session_escrow.c" \
            -Wl,-l:libsystemd.so.0 -o test
        timeout 75 ./test
        if command -v gcov >/dev/null 2>&1; then
            gcov -b -c ./*.gcno > coverage.log 2>&1
        else
            echo 'NOTRUN: gcov absent; instrumented execution only' > coverage.log
        fi
    )
else
    echo 'NOTRUN: native coverage collector unavailable for selected compiler' > "$build/coverage.log"
fi
echo 'PASS N1 fixture campaign; UNQUALIFIED. Installed/authentic Agent/N2/N3 NOTRUN.'
