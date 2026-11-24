#ifndef CM_PEER_H
#define CM_PEER_H

#include <sys/types.h>

/* Internal native facts only, never a managed ABI or authorization result. */
typedef struct
{
    pid_t pid;
    uid_t uid;
    gid_t gid;
} cm_peer_facts;

typedef enum
{
    CM_PEER_FACTS = 0,
    CM_PEER_UNAVAILABLE,
    CM_PEER_INVALID
} cm_peer_state;

typedef struct
{
    cm_peer_state state;
    int error; /* Raw syscall errno; zero for a rejected result/shape. */
} cm_peer_result;

/* Caller exclusively owns and serializes fd throughout this synchronous call:
 * never close/reuse it concurrently, including for cancellation. No FD retained,
 * closed or created, no payload accessed, no retry/fallback or hard deadline.
 * Failed output is zeroed (zero fields do NOT indicate facts). out must be valid
 * writable storage. NULL is invalid. fd must be connected AF_UNIX SOCK_STREAM.
 * Credentials are connect/listen/socketpair-time facts, not current process identity,
 * executable attestation, pathname/systemd-launch verification or expected UID
 * acceptance. Any same-UID process shares that identity. Root, UID reuse and
 * namespace qualification remain future caller policy. Invisible namespace PID
 * zero or unmapped UID/GID cannot produce facts here. No policy is retained.
 */
#if defined(__GNUC__)
__attribute__((visibility("hidden")))
#endif
cm_peer_result cm_peer_query(int fd, cm_peer_facts *out);

#endif
