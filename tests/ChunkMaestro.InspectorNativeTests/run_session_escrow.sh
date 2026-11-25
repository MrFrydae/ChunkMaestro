#!/bin/sh
set -eu
if [ "$(uname -s)" != Linux ]; then
    echo 'NOT RUN: session escrow requires Linux pidfs/public libsystemd.so.0' >&2
    exit 77
fi
if [ "$(id -u)" = 0 ]; then
    echo 'NOT RUN: escrow tests require an ordinary user' >&2
    exit 77
fi
mode=${1:---all}
case "$mode" in --all|--controls|--kernel) ;; *) echo 'usage: run_session_escrow.sh [--all|--controls|--kernel]' >&2; exit 2 ;; esac
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
# Retain partial objects, executables and negative-control logs on first failure.
artifacts="$root/.artifacts/ci/inspector-metadata/session-escrow"
mkdir -p "$artifacts"
build=$(mktemp -d "$artifacts/build-XXXXXX")
finish()
{
    result=$?
    trap - EXIT HUP INT TERM
    set +e
    # Read ELF metadata only: never execute a library or contact the manager.
    for object in "$build"/*.o "$build/kernel-plain" "$build/kernel-sanitized"; do
        [ -f "$object" ] || continue
        readelf -h -d -V -Ws "$object" > "$object.elf.txt" 2>&1
        nm -u "$object" > "$object.undefined.txt" 2>&1
    done
    printf 'runner exit=%s; retained=%s\n' "$result" "$build"
    exit "$result"
}
trap finish EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
set -x
compiler=${CC:-cc}
source_file="$root/src/ChunkMaestro.Inspector.Native/session_escrow.c"
test_file="$root/tests/ChunkMaestro.InspectorNativeTests/session_escrow_tests.c"
controls="$root/tests/ChunkMaestro.InspectorNativeTests/session_escrow_controls.h"
"$compiler" --version
uname -a
sha256sum "$source_file" "$test_file" "$controls" "$0"
for instrumentation in plain sanitized; do
    set --
    if [ "$instrumentation" = sanitized ]; then
        set -- -fsanitize=address,undefined -fno-omit-frame-pointer
    fi
    if [ "$mode" != --kernel ]; then
        echo "BEGIN: fake API controls $instrumentation (no manager library)"
        "$compiler" -pthread -std=c11 -O1 -g -Wall -Wextra -Werror -Wpedantic "$@" \
            -I"$root/src/ChunkMaestro.Inspector.Native" \
            -include "$controls" -DCM_ESCROW_INTERPOSE -c "$source_file" -o "$build/controls-$instrumentation.o"
        "$compiler" -pthread -std=c11 -O1 -g -Wall -Wextra -Werror -Wpedantic "$@" \
            -I"$root/src/ChunkMaestro.Inspector.Native" -DCM_ESCROW_CONTROLS \
            "$test_file" "$build/controls-$instrumentation.o" -o "$build/controls-$instrumentation"
        ASAN_OPTIONS=detect_leaks=1 UBSAN_OPTIONS=halt_on_error=1 "$build/controls-$instrumentation"
    fi
    if [ "$mode" != --controls ]; then
        echo "BEGIN: owned-resource kernel $instrumentation (no notify/handback calls)"
        # No development headers or libsystemd.so linker symlink required.
        # Missing runtime SONAME/symbol version is a prerequisite failure.
        "$compiler" -pthread -std=c11 -O1 -g -Wall -Wextra -Werror -Wpedantic "$@" \
            -I"$root/src/ChunkMaestro.Inspector.Native" "$source_file" "$test_file" \
            -Wl,-l:libsystemd.so.0 -o "$build/kernel-$instrumentation"
        readelf -h -d -V -Ws "$build/kernel-$instrumentation" > "$build/kernel-$instrumentation.elf.txt"
        ldd "$build/kernel-$instrumentation" > "$build/kernel-$instrumentation.ldd.txt"
        cat "$build/kernel-$instrumentation.ldd.txt"
        library=$(awk '$1 == "libsystemd.so.0" && $2 == "=>" && $3 ~ /^\// {print $3}' "$build/kernel-$instrumentation.ldd.txt")
        test -n "$library"
        sha256sum "$library" > "$build/libsystemd-$instrumentation.sha256"
        readelf -h -d -V "$library" > "$build/libsystemd-$instrumentation.elf.txt"
        ASAN_OPTIONS=detect_leaks=1 UBSAN_OPTIONS=halt_on_error=1 "$build/kernel-$instrumentation"
    fi
done
if [ "$mode" != --kernel ]; then
    for mutation in close fdpoll measurement-close measurement-borrowed measurement-retry measurement-publish measurement-flags; do
        echo "BEGIN: invariant sensitivity $mutation (fake API only)"
        case "$mutation" in
            measurement-borrowed) expression='s/close(owned)/close(borrowed)/g'; expected='fd >= 0' ;;
            measurement-retry) expression='s/close(owned) < 0/(close(owned) < 0 \&\& close(owned) < 0)/g'; expected='fd >= 0' ;;
            measurement-close) expression='s/close(owned)/0/g'; expected='closes == (fault == 1' ;;
            measurement-publish) expression='s/if (result == CM_ESCROW_OK)/if (result == CM_ESCROW_OK || result == CM_ESCROW_IO)/g'; expected='memcmp(&value, &zero' ;;
            measurement-flags) expression='s/identity_profile(owned, &measured, 1)/identity_profile(owned, \&measured, 0)/g'; expected='result == expected' ;;
            close) expression='s/close(s->fd\[i\])/0/g'; expected='!live[i]' ;;
            fdpoll) expression='s/FDPOLL=0/FDPOLL=1/g'; expected='strstr(state, "FDSTORE=1' ;;
        esac
        sed "$expression" "$source_file" > "$build/mutant-$mutation.c"
        "$compiler" -pthread -std=c11 -O1 -g -Wall -Wextra -Werror -Wpedantic \
            -I"$root/src/ChunkMaestro.Inspector.Native" -include "$controls" \
            -DCM_ESCROW_INTERPOSE -c "$build/mutant-$mutation.c" -o "$build/mutant-$mutation.o"
        "$compiler" -pthread -std=c11 -O1 -g -Wall -Wextra -Werror -Wpedantic \
            -I"$root/src/ChunkMaestro.Inspector.Native" -DCM_ESCROW_CONTROLS \
            "$test_file" "$build/mutant-$mutation.o" -o "$build/mutant-$mutation"
        if "$build/mutant-$mutation" > "$build/$mutation.log" 2>&1; then
            echo "FAIL: $mutation control escaped its assertion" >&2
            exit 1
        fi
        grep -F "$expected" "$build/$mutation.log"
        echo "PASS: $mutation control detected by invariant assertion (diagnostic only)"
    done
fi
# Separately inspect production; compiler-only injection must not leak.
"$compiler" -pthread -std=c11 -O1 -Wall -Wextra -Werror -Wpedantic -fPIC \
    -I"$root/src/ChunkMaestro.Inspector.Native" -c "$source_file" -o "$build/production.o"
nm "$build/production.o" > "$build/production.symbols.txt"
if grep 'cm_control_' "$build/production.symbols.txt"; then
    echo 'FAIL: controls in production object' >&2
    exit 1
fi
nm -u "$build/production.o"
echo 'PASS: requested native tests complete; no manager store acceptance or product authority proven'
