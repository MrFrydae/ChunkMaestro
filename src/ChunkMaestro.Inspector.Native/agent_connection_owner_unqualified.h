#ifndef CM_AGENT_CONNECTION_OWNER_UNQUALIFIED_H
#define CM_AGENT_CONNECTION_OWNER_UNQUALIFIED_H
#include <stddef.h>
#include "session_escrow.h"

/* Private, directly linked fixture API. UNQUALIFIED_FIXTURE_ONLY, never authority.
 * Caller retains LIVE storage for every entering/waiting operation. No stale
 * pointer, concurrent free, concurrent close or arbitrary callback is supported.
 * Operations exclude each other; contention/reentry revokes the generation.
 * close revokes before waiting for synchronous work; same-thread reentrant close
 * refuses, leaving the token for a later enclosing close. No cancellation by FD
 * close. create returns a separate lifetime token which retains owner storage
 * and its mutex after close. Caller joins ALL entrants/waiters before destroy;
 * destroy is a quiescent-only operation, not an automatic waiter detector.
 * Outputs must be writable, nonoverlapping, separate from owner storage/inputs.
 * Missing diagnostics refuses without effects. close(NULL-token) is idempotent
 * and preserves the caller's preceding diagnostic snapshot.
 */
typedef struct cm_connection_owner cm_connection_owner;
typedef struct cm_connection_lifetime cm_connection_lifetime;
typedef enum
{
    CM_OWNER_NONE = 0,
    CM_OWNER_ARGUMENT,
    CM_OWNER_ALLOCATE,
    CM_OWNER_DIRECTORY,
    CM_OWNER_PATH,
    CM_OWNER_SOCKET,
    CM_OWNER_FLAGS,
    CM_OWNER_OPTIONS,
    CM_OWNER_BIND,
    CM_OWNER_LISTEN,
    CM_OWNER_ACCEPT,
    CM_OWNER_CANDIDATE,
    CM_OWNER_MEASURE,
    CM_OWNER_REVOKED,
    CM_OWNER_REPLAY,
    CM_OWNER_CLOSE_CANDIDATE,
    CM_OWNER_CLOSE_ACCEPTED,
    CM_OWNER_CLOSE_LISTENER,
    CM_OWNER_CLOSE_DIRECTORY,
    CM_OWNER_DESTROY
} cm_owner_stage;
typedef struct
{
    unsigned version;
    cm_owner_stage first;
    int first_errno;
    cm_owner_stage cleanup;
    int cleanup_errno;
    unsigned uncertain;
    unsigned attempted;
    cm_agent_candidate_diagnostics_v1 candidate;
} cm_owner_diagnostics;
#define CM_OWNER_BASENAME "agent.sock"
/* length excludes NUL; no NUL may appear in those bytes. Canonical absolute
 * existing directory, owned by euid, mode0700. Fixed socket path fits sun_path.
 * No adopt-FD API. Allocation precedes filesystem effects. */
__attribute__((visibility("hidden"))) int cm_connection_create(const char *directory,
    size_t length, cm_connection_owner **out, cm_connection_lifetime **lifetime,
    cm_owner_diagnostics *diagnostics);
__attribute__((visibility("hidden"))) int cm_connection_attempt(cm_connection_owner *owner,
    cm_owner_diagnostics *diagnostics);
__attribute__((visibility("hidden"))) int cm_connection_measure(cm_connection_owner *owner,
    cm_original_measurement_v1 *out, cm_owner_diagnostics *diagnostics);
__attribute__((visibility("hidden"))) int cm_connection_close(cm_connection_owner **owner,
    cm_owner_diagnostics *diagnostics);
/* Requires close and caller-established quiescence of every owner alias.
 * Refuses while resources remain open. Never closes resources or retries an
 * uncertain mutex destruction. A cleared lifetime token is idempotent.
 */
__attribute__((visibility("hidden"))) int cm_connection_destroy(
    cm_connection_lifetime **lifetime, cm_owner_diagnostics *diagnostics);
#endif
