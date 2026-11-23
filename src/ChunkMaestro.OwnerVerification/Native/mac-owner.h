#ifndef CHUNKMAESTRO_OWNER_H
#define CHUNKMAESTRO_OWNER_H
#include <stdint.h>
#include <stddef.h>
// ABI v1. No success/permission value exists. Available is preflight only.
enum cm_owner_status
{
    CM_CONTEXT_CREATED = 0,
    CM_AVAILABLE = 1,
    CM_UNAVAILABLE = 2,
    CM_UNSUPPORTED = 3,
    CM_INVALID_INPUT = 4,
    CM_CANCELED = 5,
    CM_FAILURE = 6
};
uint32_t cm_owner_abi(void);
int32_t cm_owner_create(uint32_t abi, const uint8_t *nonce, size_t nonce_size,
    const uint8_t *session, size_t session_size, uint64_t generation, uint64_t *request);
int32_t cm_owner_probe(uint64_t request);
int32_t cm_owner_cancel(uint64_t request);
// Refusal-only validation. Never starts evaluation. window is compared as bits only.
int32_t cm_owner_prepare(uint64_t request, uintptr_t window, uint64_t generation);
int32_t cm_owner_release(uint64_t request);
#endif
