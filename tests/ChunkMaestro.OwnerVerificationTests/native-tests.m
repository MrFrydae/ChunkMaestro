#import <Foundation/Foundation.h>
#import "../../src/ChunkMaestro.OwnerVerification/Native/mac-owner.h"
#include <stdio.h>
#define CHECK(expression) do { if (!(expression)) { fprintf(stderr, "failed line %d\n", __LINE__); return 1; } count++; } while (0)
int main(void)
{
    @autoreleasepool
    {
        unsigned count = 0;
        uint8_t nonce[32] = {1};
        uint8_t session[16] = {1};
        uint8_t empty[32] = {0};
        uint64_t request = 99;
        CHECK(cm_owner_abi() == 1);
        CHECK(cm_owner_create(2, nonce, 32, session, 16, 1, &request) == CM_INVALID_INPUT && request == 0);
        CHECK(cm_owner_create(1, nonce, 31, session, 16, 1, &request) == CM_INVALID_INPUT);
        CHECK(cm_owner_create(1, empty, 32, session, 16, 1, &request) == CM_INVALID_INPUT);
        CHECK(cm_owner_create(1, nonce, 32, empty, 16, 1, &request) == CM_INVALID_INPUT);
        CHECK(cm_owner_create(1, nonce, 32, session, 16, 0, &request) == CM_INVALID_INPUT);
        CHECK(cm_owner_create(1, nonce, 32, session, 16, 1, NULL) == CM_INVALID_INPUT);
        CHECK(cm_owner_create(1, nonce, 32, session, 16, 1, &request) == CM_CONTEXT_CREATED && request != 0);
        CHECK(cm_owner_prepare(request, 0, 1) == CM_INVALID_INPUT);
        CHECK(cm_owner_prepare(request, 1, 2) == CM_INVALID_INPUT);
        // main thread, no NSApp and arbitrary input pointer: must never dereference it.
        CHECK(cm_owner_prepare(request, 1, 1) == CM_INVALID_INPUT);
        CHECK(cm_owner_prepare(request, UINTPTR_MAX, 1) == CM_INVALID_INPUT);
        uint64_t other = 0;
        CHECK(cm_owner_create(1, nonce, 32, session, 16, 1, &other) == CM_CONTEXT_CREATED && other != request);
        CHECK(cm_owner_release(other) == CM_CANCELED);
        CHECK(cm_owner_cancel(request) == CM_CANCELED);
        CHECK(cm_owner_cancel(request) == CM_CANCELED);
        CHECK(cm_owner_probe(request) == CM_CANCELED);
        CHECK(cm_owner_release(request) == CM_CANCELED);
        CHECK(cm_owner_release(request) == CM_INVALID_INPUT);
        CHECK(cm_owner_probe(request) == CM_INVALID_INPUT);
        // Exercise raw ABI lookup/release races without the managed serialization gate.
        for (unsigned iteration = 0; iteration < 100; iteration++)
        {
            uint64_t racing = 0;
            CHECK(cm_owner_create(1, nonce, 32, session, 16, 1, &racing) == CM_CONTEXT_CREATED);
            dispatch_group_t group = dispatch_group_create();
            dispatch_queue_t queue = dispatch_get_global_queue(QOS_CLASS_DEFAULT, 0);
            dispatch_group_async(group, queue, ^
            {
                cm_owner_probe(racing);
            });
            dispatch_group_async(group, queue, ^
            {
                cm_owner_cancel(racing);
            });
            dispatch_group_async(group, queue, ^
            {
                cm_owner_release(racing);
            });
            CHECK(dispatch_group_wait(group, dispatch_time(DISPATCH_TIME_NOW, 5 * NSEC_PER_SEC)) == 0);
            CHECK(cm_owner_probe(racing) == CM_INVALID_INPUT);
        }
        printf("native assertions: %u\n", count);
    }
    return 0;
}
