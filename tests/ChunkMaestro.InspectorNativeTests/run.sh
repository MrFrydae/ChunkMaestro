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
                -Dgetsockopt=cm_peer_test_getsockopt \
                -Dgetsockname=cm_peer_test_getsockname \
                -Dgetpeername=cm_peer_test_getpeername \
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
"$compiler" -std=c11 -Wall -Wextra -Werror -Wpedantic -c \
    "$root/src/ChunkMaestro.Inspector.Native/peer.c" -o "$build/peer-production.o"
nm -u "$build/peer-production.o" > "$build/peer-imports"
# An allowlist also rejects future payload, socket creation, exec, ioctl, test
# controls and permission mutation imports, including unanticipated spellings.
if sed -E 's/^.*[[:space:]]U[[:space:]]+//' "$build/peer-imports" | \
    grep -Ev '^(__errno_location|memset|getsockopt|getsockname|getpeername)$'; then
    echo 'FAIL: unexpected peer production import' >&2
    exit 1
fi
cat "$build/peer-imports"
echo 'PASS: peer production imports contain queries only'
