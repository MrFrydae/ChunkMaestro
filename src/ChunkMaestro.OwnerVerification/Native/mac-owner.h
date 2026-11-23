#ifndef CHUNKMAESTRO_OWNER_H
#define CHUNKMAESTRO_OWNER_H
#include <stdint.h>
#include <stddef.h>
// ABI v2. Raw policy observations are internal diagnostics, never permission.
enum cm_owner_status
{
    CM_CONTEXT_CREATED = 0,
    CM_AVAILABLE = 1,
    CM_UNAVAILABLE = 2,
    CM_UNSUPPORTED = 3,
    CM_INVALID_INPUT = 4,
    CM_CANCELED = 5,
    CM_FAILURE = 6,
    CM_EVALUATION_QUEUED = 7,
    CM_WINDOW_CAPTURED = 8
};
uint32_t cm_owner_abi(void);
int32_t cm_owner_create(uint32_t abi, const uint8_t *nonce, size_t nonce_size,
    const uint8_t *session, size_t session_size, uint64_t generation, uint64_t *request);
int32_t cm_owner_probe(uint64_t request);
int32_t cm_owner_cancel(uint64_t request);
// Refusal-only validation. Never starts evaluation. window is compared as bits only.
int32_t cm_owner_prepare(uint64_t request, uintptr_t window, uint64_t generation);
int32_t cm_owner_release(uint64_t request);

// Fixed int32 observations; only a real OS success reply maps to 0 in production.
enum cm_owner_raw_result
{
    CM_RAW_POLICY_SATISFIED = 0,
    CM_RAW_DENIED = 1,
    CM_RAW_CANCELED = 2,
    CM_RAW_UNAVAILABLE = 3,
    CM_RAW_INVALID_WINDOW = 4,
    CM_RAW_UNKNOWN = 5
};
typedef int32_t (*cm_owner_reply)(uint64_t request, uint64_t generation, uintptr_t window,
    const uint8_t *nonce, size_t nonce_size, int32_t result, uint32_t phase, void *cookie);
// Main-thread no-prompt capture; 8 means retained actual owned window, not permission.
int32_t cm_owner_bind_window_explicit(uint64_t request, uintptr_t window, uint64_t generation);
// 7 transfers callback ownership; all other statuses promise no callback.
// phase 0 delivers terminal; ack 1 permits phase 1 drain after active removal.
// phase 1 ack 1 releases cookie; failure retains unknown ownership, never retried.
int32_t cm_owner_evaluate_explicit(uint64_t request, uintptr_t window, uint64_t generation,
    cm_owner_reply reply, void *cookie);
#endif
