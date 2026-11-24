#!/bin/sh
set -eu
if [ "$(uname -s)" != Linux ]; then
    echo 'NOT RUN: Linux kernel/openat2/xattr/ACL/sanitizer tests require Linux' >&2
    exit 77
fi
if [ "$(id -u)" = 0 ]; then
    echo 'NOT RUN: run as ordinary user for real DAC denial tests' >&2
    exit 77
fi
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
build=$(mktemp -d /tmp/cm-native-build-XXXXXX)
trap 'rm -rf -- "$build"' EXIT HUP INT TERM
compiler=${CC:-cc}
"$compiler" --version
uname -a
# Explicit plain and instrumented builds. Sanitizers are required for this command;
# unavailable compiler support is a failed prerequisite, never a silent green skip.
for instrumentation in plain sanitized; do
    set --
    if [ "$instrumentation" = sanitized ]; then
        set -- -fsanitize=address,undefined -fno-omit-frame-pointer
    fi
    for mode in kernel controls; do
        if [ "$mode" = controls ]; then
            set -- "$@" -DCM_METADATA_TEST
        fi
        "$compiler" -std=c11 -O1 -g -Wall -Wextra -Werror -Wpedantic "$@" \
            -I"$root/src/ChunkMaestro.Inspector.Native" \
            -I"$root/tests/ChunkMaestro.InspectorNativeTests" \
            "$root/src/ChunkMaestro.Inspector.Native/metadata.c" \
            "$root/tests/ChunkMaestro.InspectorNativeTests/metadata_tests.c" \
            -o "$build/$instrumentation-$mode"
        ASAN_OPTIONS=detect_leaks=1 UBSAN_OPTIONS=halt_on_error=1 \
            "$build/$instrumentation-$mode"
    done
done
# One disposable ownership fault must be detected by the complete FD oracle.
# This is regression sensitivity, not a mutation percentage or release score.
test "$(grep -cF 'close(owned->parent_fd);' "$root/src/ChunkMaestro.Inspector.Native/metadata.c")" = 1
sed 's/close(owned->parent_fd);/\/\* controlled missing-parent-close fault \*\//' \
    "$root/src/ChunkMaestro.Inspector.Native/metadata.c" > "$build/leaking.c"
"$compiler" -std=c11 -O1 -g -Wall -Wextra -Werror -Wpedantic \
    -I"$root/src/ChunkMaestro.Inspector.Native" \
    "$build/leaking.c" "$root/tests/ChunkMaestro.InspectorNativeTests/metadata_tests.c" \
    -o "$build/leaking"
if "$build/leaking" > "$build/leaking.log" 2>&1; then
    echo 'FAIL: controlled missing-parent-close fault escaped the descriptor oracle' >&2
    exit 1
fi
grep -F 'same_descriptor_snapshot(&before_fds, &after_fds)' "$build/leaking.log"
echo 'PASS: expected missing-parent-close failure was detected; production source unchanged'
# Inspect a separately compiled production object, without the test definition.
"$compiler" -std=c11 -Wall -Wextra -Werror -Wpedantic -c \
    "$root/src/ChunkMaestro.Inspector.Native/metadata.c" -o "$build/production.o"
if nm "$build/production.o" | grep -E 'cm_test_|cm_.*control'; then
    echo 'FAIL: test controls leaked into production object' >&2
    exit 1
fi
nm -u "$build/production.o" > "$build/imports"
if grep -E '[[:space:]](read|pread|pread64|readv|getdents|getdents64|opendir|fdopendir|readdir|execve|socket|connect|chmod|fchmod|chown|fchown|setxattr|fsetxattr)$' "$build/imports"; then
    echo 'FAIL: unwanted production read/network/permission/mutation import' >&2
    exit 1
fi
cat "$build/imports"
echo 'PASS: production object excludes injected controls'

# Peer facts use a separate production object; injection is compiler-only and
# confined to a second test object, never an API or conditional in peer.c.
for instrumentation in plain sanitized; do
    set --
    if [ "$instrumentation" = sanitized ]; then
        set -- -fsanitize=address,undefined -fno-omit-frame-pointer
    fi
    for mode in kernel controls; do
        if [ "$mode" = controls ]; then
            "$compiler" -std=c11 -O1 -g -Wall -Wextra -Werror -Wpedantic "$@" \
                -I"$root/src/ChunkMaestro.Inspector.Native" \
                -include "$root/tests/ChunkMaestro.InspectorNativeTests/peer_syscall_controls.h" \
                -DCM_PEER_INTERPOSE \
                -c "$root/src/ChunkMaestro.Inspector.Native/peer.c" -o "$build/peer.o"
            set -- "$@" -DCM_PEER_CONTROLS
        else
            "$compiler" -std=c11 -O1 -g -Wall -Wextra -Werror -Wpedantic "$@" \
                -I"$root/src/ChunkMaestro.Inspector.Native" \
                -c "$root/src/ChunkMaestro.Inspector.Native/peer.c" -o "$build/peer.o"
        fi
        "$compiler" -std=c11 -O1 -g -Wall -Wextra -Werror -Wpedantic "$@" \
            -I"$root/src/ChunkMaestro.Inspector.Native" \
            "$root/tests/ChunkMaestro.InspectorNativeTests/peer_tests.c" "$build/peer.o" \
            -o "$build/peer-$instrumentation-$mode"
        ASAN_OPTIONS=detect_leaks=1 UBSAN_OPTIONS=halt_on_error=1 \
            "$build/peer-$instrumentation-$mode"
    done
done
# Small controlled negative: accepting a non-stream socket must be detected.
test "$(grep -cF 'type != SOCK_STREAM' "$root/src/ChunkMaestro.Inspector.Native/peer.c")" = 1
sed 's/type != SOCK_STREAM/0/' "$root/src/ChunkMaestro.Inspector.Native/peer.c" > "$build/peer-nonstream.c"
"$compiler" -std=c11 -O1 -g -Wall -Wextra -Werror -Wpedantic \
    -I"$root/src/ChunkMaestro.Inspector.Native" \
    "$build/peer-nonstream.c" "$root/tests/ChunkMaestro.InspectorNativeTests/peer_tests.c" \
    -o "$build/peer-nonstream"
if "$build/peer-nonstream" > "$build/peer-nonstream.log" 2>&1; then
    echo 'FAIL: controlled non-stream acceptance escaped the peer tests' >&2
    exit 1
fi
grep -F 'status.state == expected && status.error == error' "$build/peer-nonstream.log"
echo 'PASS: expected non-stream guard failure detected; production source unchanged'
"$compiler" -std=c11 -Wall -Wextra -Werror -Wpedantic -fstack-protector-strong -c \
    "$root/src/ChunkMaestro.Inspector.Native/peer.c" -o "$build/peer-production.o"
nm -u "$build/peer-production.o" > "$build/peer-imports"
# An allowlist also rejects future payload, socket creation, exec, ioctl, test
# controls and permission mutation imports, including unanticipated spellings.
# Keep compiler stack protection enabled; its failure helper is not payload I/O.
peer_allowed_imports='^(__errno_location|__stack_chk_fail|memset|getsockopt|getsockname|getpeername)$'
if sed -E 's/^.*[[:space:]]U[[:space:]]+//' "$build/peer-imports" | \
    grep -Ev "$peer_allowed_imports"; then
    echo 'FAIL: unexpected peer production import' >&2
    exit 1
fi
cat "$build/peer-imports"
echo 'PASS: peer production imports contain queries and compiler safety helpers only'
# A real compiled payload import must still fail the same production allowlist.
# This object is inspected only, never linked into production or executed.
cat > "$build/peer-payload.c" <<'EOF'
#include <sys/socket.h>
ssize_t cm_peer_payload_probe(int fd, void *buffer, size_t length)
{
    return recv(fd, buffer, length, 0);
}
EOF
"$compiler" -std=c11 -Wall -Wextra -Werror -Wpedantic -fstack-protector-strong -c \
    "$build/peer-payload.c" -o "$build/peer-payload.o"
nm -u "$build/peer-payload.o" > "$build/peer-payload-imports"
if ! sed -E 's/^.*[[:space:]]U[[:space:]]+//' "$build/peer-payload-imports" | \
    grep -Ev "$peer_allowed_imports" > "$build/peer-payload-rejected"; then
    echo 'FAIL: compiled payload import escaped the peer production allowlist' >&2
    exit 1
fi
grep -Fx 'recv' "$build/peer-payload-rejected"
echo 'PASS: compiled payload import rejected by peer production allowlist'

# Policy snapshot is a separate ABI/object: never widen metadata/peer audits.
for instrumentation in plain sanitized; do
    set --
    if [ "$instrumentation" = sanitized ]; then
        set -- -fsanitize=address,undefined -fno-omit-frame-pointer
    fi
    for mode in kernel controls; do
        if [ "$mode" = controls ]; then
            "$compiler" -std=c11 -O1 -g -Wall -Wextra -Werror -Wpedantic "$@" \
                -I"$root/src/ChunkMaestro.Inspector.Native" \
                -include "$root/tests/ChunkMaestro.InspectorNativeTests/policy_snapshot_syscall_controls.h" \
                -DCM_POLICY_INTERPOSE -c "$root/src/ChunkMaestro.Inspector.Native/policy_snapshot.c" -o "$build/policy.o"
            set -- "$@" -DCM_POLICY_CONTROLS
        else
            "$compiler" -std=c11 -O1 -g -Wall -Wextra -Werror -Wpedantic "$@" \
                -I"$root/src/ChunkMaestro.Inspector.Native" \
                -c "$root/src/ChunkMaestro.Inspector.Native/policy_snapshot.c" -o "$build/policy.o"
        fi
        "$compiler" -std=c11 -O1 -g -Wall -Wextra -Werror -Wpedantic "$@" \
            -I"$root/src/ChunkMaestro.Inspector.Native" \
            -I"$root/tests/ChunkMaestro.InspectorNativeTests" \
            "$root/tests/ChunkMaestro.InspectorNativeTests/policy_snapshot_tests.c" "$build/policy.o" \
            -o "$build/policy-$instrumentation-$mode"
        ASAN_OPTIONS=detect_leaks=1 UBSAN_OPTIONS=halt_on_error=1 "$build/policy-$instrumentation-$mode"
    done
done
"$compiler" -std=c11 -Wall -Wextra -Werror -Wpedantic -fstack-protector-strong \
    -c "$root/src/ChunkMaestro.Inspector.Native/policy_snapshot.c" -o "$build/policy-production.o"
nm -u "$build/policy-production.o" > "$build/policy-imports"
policy_allowed_imports='^(__errno_location|__stack_chk_fail|calloc|close|fcntl|fgetxattr|free|fstat|memcmp|memcpy|pread|syscall)$'
if sed -E 's/^.*[[:space:]]U[[:space:]]+//' "$build/policy-imports" | grep -Ev "$policy_allowed_imports"; then
    echo 'FAIL: unexpected policy production import' >&2
    exit 1
fi
cat "$build/policy-imports"
echo 'PASS: separate policy production imports exclude test controls/mutation/network'
# Direct negative sensitivity: guard bypasses must fail their invariant assertions.
policy_artifacts="$root/.artifacts/policy-snapshot"
mkdir -p "$policy_artifacts"
for mutation in size type bytes cleanup; do
    case "$mutation" in
        size) expression='s/s->file_stat.st_size < 1/0/' ;;
        type) expression='s/!S_ISREG(s->file_stat.st_mode)/0/' ;;
        bytes) expression='s/memcmp(s->bytes, second, s->length) != 0/0/' ;;
        cleanup) expression='s/close(s->parent);/(void)0;/' ;;
    esac
    sed "$expression" "$root/src/ChunkMaestro.Inspector.Native/policy_snapshot.c" > "$build/policy-$mutation.c"
    "$compiler" -std=c11 -O1 -g -Wall -Wextra -Werror -Wpedantic \
        -I"$root/src/ChunkMaestro.Inspector.Native" -I"$root/tests/ChunkMaestro.InspectorNativeTests" \
        -include "$root/tests/ChunkMaestro.InspectorNativeTests/policy_snapshot_syscall_controls.h" \
        -DCM_POLICY_INTERPOSE -c "$build/policy-$mutation.c" -o "$build/policy-mutant.o"
    "$compiler" -std=c11 -O1 -g -Wall -Wextra -Werror -Wpedantic \
        -I"$root/src/ChunkMaestro.Inspector.Native" -I"$root/tests/ChunkMaestro.InspectorNativeTests" \
        -DCM_POLICY_CONTROLS "$root/tests/ChunkMaestro.InspectorNativeTests/policy_snapshot_tests.c" \
        "$build/policy-mutant.o" -o "$build/policy-$mutation"
    if "$build/policy-$mutation" > "$policy_artifacts/mutation-$mutation.log" 2>&1; then
        echo "FAIL: $mutation bypass escaped policy tests" >&2
        exit 1
    fi
    # A crash or timeout alone is not credit: require the intended assertion.
    case "$mutation" in
        size) expected='take(&s) == 1 && !s' ;;
        type) expected='actual == 1' ;;
        bytes) expected='actual == 3' ;;
        cleanup) expected='memcmp(before, after, sizeof(before)) == 0' ;;
    esac
    grep -F "$expected" "$policy_artifacts/mutation-$mutation.log"
done
# Diagnostic branch/line accounting, no numeric acceptance threshold.
"$compiler" -std=c11 -O0 -g --coverage -Wall -Wextra -Werror -Wpedantic \
    -I"$root/src/ChunkMaestro.Inspector.Native" \
    -include "$root/tests/ChunkMaestro.InspectorNativeTests/policy_snapshot_syscall_controls.h" \
    -DCM_POLICY_INTERPOSE -c "$root/src/ChunkMaestro.Inspector.Native/policy_snapshot.c" -o "$build/policy-coverage.o"
"$compiler" -std=c11 -O0 -g --coverage -Wall -Wextra -Werror -Wpedantic \
    -I"$root/src/ChunkMaestro.Inspector.Native" -I"$root/tests/ChunkMaestro.InspectorNativeTests" \
    -DCM_POLICY_CONTROLS "$root/tests/ChunkMaestro.InspectorNativeTests/policy_snapshot_tests.c" \
    "$build/policy-coverage.o" -o "$build/policy-coverage"
"$build/policy-coverage" > "$policy_artifacts/fault-controls.log" 2>&1
cp "$build"/*.gcno "$build"/*.gcda "$policy_artifacts/"
(cd "$policy_artifacts" && gcov -b -c policy-coverage.gcno > coverage.log 2>&1)
cat "$policy_artifacts/coverage.log"
