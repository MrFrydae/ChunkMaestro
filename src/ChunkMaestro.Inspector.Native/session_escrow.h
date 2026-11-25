#ifndef CM_SESSION_ESCROW_H
#define CM_SESSION_ESCROW_H
#include <stdint.h>

/* Linux-only, synchronous, serialized internal ABI. No authorization result.
 * Caller must qualify kernel/pidfs, loader, main-process provenance, protected
 * boot/epoch/journal and manager graph before use. Never pass agent-owned FDs.
 * No raw FD is returned. Handles cannot be shared/closed concurrently. */
typedef struct cm_escrow cm_escrow;
typedef struct
{
    uint64_t device;
    uint64_t inode;
    uint32_t flags;
} cm_escrow_identity;
/* Snapshot only; source is a claim under protected-holder preconditions,
 * never acquisition authentication or liveness. Distinct from identity padding. */
typedef struct
{
    uint64_t device;
    uint64_t inode;
    uint32_t flags;
    uint8_t architecture;
    uint8_t normalization;
    uint8_t source;
    uint8_t reserved;
} cm_original_measurement_v1;
typedef enum
{
    CM_ESCROW_OK = 0,
    CM_ESCROW_INVALID,
    CM_ESCROW_UNAVAILABLE,
    CM_ESCROW_IDENTITY,
    CM_ESCROW_IO,
    CM_ESCROW_NOTIFY,
    CM_ESCROW_DEADLINE,
    CM_ESCROW_REPLAY
} cm_escrow_result;
typedef struct
{
    int readable;
    int hup;
} cm_escrow_observation;

/* Borrows original, duplicates CLOEXEC>=3 once, measures, closes duplicate once.
 * All 24 output bytes zero on refusal; NULL out causes no syscall. Caller must
 * exclude mutation/replacement and retain first refusal in its generation owner. */
cm_escrow_result cm_escrow_measure_original_v1(int borrowed, cm_original_measurement_v1 *out);
/* Duplicate borrowed independently bound ORIGINAL non-thread worker/agent
 * pidfds. Does not open a PID, bind invocation, or accept numeric reconstruction.
 * epoch is exactly 32 lowercase hex bytes plus NUL. out reset on failure.
 * Each attempt is caller's occupied generation; constructing another handle
 * must never refund a consumed attempt. Original borrowed FDs remain owned by caller. */
cm_escrow_result cm_escrow_bind(int worker, int agent, const char *epoch, cm_escrow **out);
cm_escrow_result cm_escrow_metadata(const cm_escrow *session, cm_escrow_identity out[2]);
/* Absolute CLOCK_BOOTTIME microseconds, nonzero and no more than 30 seconds
 * away. One upload attempt per handle, no retry even after partial success.
 * Returns OK ONLY for send+barrier processing. NOT a stored/ARMED receipt. */
cm_escrow_result cm_escrow_upload(cm_escrow *session, uint64_t deadline_usec);
/* Same protected holder service only. Consumes declared inherited FDs 3..N+2
 * once a bounded canonical LISTEN_FDS in [0,16] is recognized, even on refusal.
 * Invalid/unbounded declaration leaves FD ownership to startup quarantine.
 * Caller must exclude all other inherited FDs and authenticate manager origin;
 * environment/names/metadata alone are not authentication. Calls unset=1.
 * One handback attempt per process; repeat returns REPLAY without touching FDs.
 * Exactly two role+epoch names and protected expected pidfs identities required.
 * Recovery is retire-only; never publishes policy input or resumes old work. */
cm_escrow_result cm_escrow_handback(const char *epoch, const cm_escrow_identity expected[2], cm_escrow **out);
/* Poll current originals without waiting. HUP is NOT cleanup/product proof.
 * Errors stick: first failure poisons all later observations/uploads. */
cm_escrow_result cm_escrow_observe(cm_escrow *session, unsigned role, cm_escrow_observation *out);
cm_escrow_result cm_escrow_close(cm_escrow **session);
const char *cm_escrow_error(cm_escrow_result result);
#endif
