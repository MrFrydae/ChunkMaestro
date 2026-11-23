#ifndef CM_METADATA_H
#define CM_METADATA_H

#include <sys/stat.h>

/* Internal Linux foundation only. No authentication, policy or permission result.
 * Caller must serialize ALL handle operations, including disposal, and must never
 * close/reuse an owned FD behind this API. No async cancellation by closing FDs.
 * Handles are opaque, single-owner and non-copyable. No FD escapes a handle.
 * Output handle slots must be empty and must not alias input handles.
 */
typedef struct cm_directory cm_directory;

typedef enum
{
    CM_FACTS = 0, /* Snapshot/acquisition only; never usable observation. */
    CM_NO_DATA_UNQUALIFIED,
    CM_ACL_PRESENT, /* Protected V1 path must refuse any present ACL. */
    CM_UNAVAILABLE,
    CM_DRIFT,
    CM_INVALID
} cm_state;

typedef struct
{
    cm_state state;
    int error; /* Raw errno for internal diagnostics only; no paths. */
} cm_result;

/* Snapshot fields compared: dev/inode/type+mode/uid/gid/nlink/ctime/mtime.
 * Native struct stat stays inside native code, not a managed ABI declaration.
 * expected is an internal caller binding, NOT authenticated root policy.
 * Consumes *owned_fd (sets -1) even on failure; requires O_RDONLY directory and
 * FD_CLOEXEC. Caller has exclusive ownership before and throughout transfer.
 */
cm_result cm_directory_take(int *owned_fd, const struct stat *expected,
    cm_directory **out);
cm_result cm_directory_snapshot(const cm_directory *directory, struct stat *out);

/* Exactly one trusted internal basename, 1..64 ASCII [A-Za-z0-9_.-], excluding
 * dot/dotdot. Never request/decoded-policy data. Caller establishes anchor trust.
 * Parent FD is duplicated CLOEXEC and retained independently in the child.
 * No arbitrary relative path, enumeration, file contents, or openat fallback.
 */
cm_result cm_directory_open(const cm_directory *parent, const char *basename,
    const struct stat *expected, cm_directory **out);

/* Requires child acquired through cm_directory_open. Bounded fixed-name ACL
 * queries on parent, retained child and reopened child, before/after snapshots.
 * ENODATA always remains unqualified; this API has NO qualification input and
 * NO safe/Observed/authorized output. No guarantee against root/ABA/mount races.
 * Syscalls can block: this primitive makes no wall-clock deadline claim.
 */
cm_result cm_directory_check(const cm_directory *directory);
void cm_directory_destroy(cm_directory **directory);

#endif
