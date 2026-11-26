#!/bin/sh
set -eu
if [ "$(uname -s)" != Linux ]; then
    echo 'NOTRUN: candidate native tests require Linux; no platform simulation' >&2
    exit 77
fi
if [ "$(id -u)" = 0 ]; then
    echo 'NOTRUN: candidate native tests require an ordinary user' >&2
    exit 77
fi
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
artifacts="$root/.artifacts/ci/inspector-metadata/agent-candidate"
mkdir -p "$artifacts"
build=$(mktemp -d "$artifacts/build-XXXXXX")
source_file="$root/src/ChunkMaestro.Inspector.Native/session_escrow.c"
test_file="$root/tests/ChunkMaestro.InspectorNativeTests/agent_candidate_tests.c"
controls="$root/tests/ChunkMaestro.InspectorNativeTests/agent_candidate_controls.h"
compiler=${CC:-cc}
set -x
"$compiler" --version
uname -a
sha256sum "$source_file" "$test_file" "$controls" "$0"
for instrumentation in plain sanitized; do
    set --
    if [ "$instrumentation" = sanitized ]; then
        set -- -fsanitize=address,undefined -fno-omit-frame-pointer
    fi
    "$compiler" -std=c11 -O1 -g -Wall -Wextra -Werror -Wpedantic "$@" \
        -I"$root/src/ChunkMaestro.Inspector.Native" -include "$controls" -DCM_ESCROW_INTERPOSE \
        -c "$source_file" -o "$build/controls-$instrumentation.o"
    "$compiler" -std=c11 -O1 -g -Wall -Wextra -Werror -Wpedantic "$@" \
        -I"$root/src/ChunkMaestro.Inspector.Native" -DCM_CANDIDATE_CONTROLS \
        "$test_file" "$build/controls-$instrumentation.o" -o "$build/controls-$instrumentation"
    ASAN_OPTIONS=detect_leaks=1 UBSAN_OPTIONS=halt_on_error=1 "$build/controls-$instrumentation"
    "$compiler" -std=c11 -O1 -g -Wall -Wextra -Werror -Wpedantic "$@" \
        -I"$root/src/ChunkMaestro.Inspector.Native" "$source_file" "$test_file" \
        -Wl,-l:libsystemd.so.0 -o "$build/kernel-$instrumentation"
    ASAN_OPTIONS=detect_leaks=1 UBSAN_OPTIONS=halt_on_error=1 "$build/kernel-$instrumentation"
done
# Compiled controls are diagnostic sensitivity checks, never coverage gates.
for mutation in secondary retry first publish; do
    case "$mutation" in
        secondary) expression='s/d->cleanup_code = CM_ESCROW_IO;/d->cleanup_code = 0;/'; expected='d.cleanup_code ==' ;;
        retry) expression='s/fd >= 0 && close(fd) < 0/fd >= 0 \&\& close(fd) < 0 \&\& close(fd) < 0/'; expected='closes <= duplicates + 1' ;;
        first) expression='s/d = s->diagnostics;/memset(\&d, 0, sizeof(d)); d.version = 1;/'; expected='d.first_stage == first' ;;
        publish) expression='s/d.first_code == 0 && d.cleanup_code == 0 && (d.ownership_flags \& 6) == 0/out != NULL/'; expected='memcmp(&value, &zero' ;;
    esac
    sed "$expression" "$source_file" > "$build/mutant-$mutation.c"
    "$compiler" -std=c11 -O1 -g -Wall -Wextra -Werror -Wpedantic \
        -I"$root/src/ChunkMaestro.Inspector.Native" -include "$controls" -DCM_ESCROW_INTERPOSE \
        -c "$build/mutant-$mutation.c" -o "$build/mutant-$mutation.o"
    "$compiler" -std=c11 -O1 -g -Wall -Wextra -Werror -Wpedantic \
        -I"$root/src/ChunkMaestro.Inspector.Native" -DCM_CANDIDATE_CONTROLS \
        "$test_file" "$build/mutant-$mutation.o" -o "$build/mutant-$mutation"
    if "$build/mutant-$mutation" > "$build/$mutation.log" 2>&1; then
        echo "FAIL escaped compiled control: $mutation" >&2
        exit 1
    fi
    grep -F "$expected" "$build/$mutation.log"
done
# Old ABI remains loadable; candidate symbols are absent, so no fallback can dispatch.
git -C "$root" show ee3574f206dd2045893a9a15f61773e7483e3587:src/ChunkMaestro.Inspector.Native/session_escrow.c > "$build/old.c"
"$compiler" -std=c11 -O1 -Wall -Wextra -Werror -Wpedantic -fPIC -shared \
    -I"$root/src/ChunkMaestro.Inspector.Native" "$build/old.c" -Wl,-l:libsystemd.so.0 -o "$build/old.so"
cat > "$build/missing.c" <<'C'
#include <assert.h>
#include <dlfcn.h>
int main(int argc, char **argv)
{
    assert(argc == 2);
    void *library = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    assert(library);
    assert(dlsym(library, "cm_escrow_measure_original_v1"));
    assert(dlsym(library, "cm_agent_candidate_acquire_v1") == 0);
    assert(dlsym(library, "cm_agent_candidate_measure_unqualified_v1") == 0);
    assert(dlsym(library, "cm_agent_candidate_close_v1") == 0);
    assert(dlclose(library) == 0);
    return 0;
}
C
"$compiler" -std=c11 -Wall -Wextra -Werror -Wpedantic "$build/missing.c" -ldl -o "$build/missing"
"$build/missing" "$build/old.so"
echo 'PASS candidate tests complete; authentic Agent/guard/Debian qualification NOTRUN'
