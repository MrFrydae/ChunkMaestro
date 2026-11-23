// Separately compiled simulation. No fake completion symbol exists in the dylib.
#import <AppKit/AppKit.h>
#import <LocalAuthentication/LocalAuthentication.h>
#include <stdio.h>
#include <string.h>
#define CM_OWNER_TESTING 1
static NSObject *fakeWindow;
static NSObject *replacementWindow;
static BOOL windowOpen = YES;
static unsigned starts;
static void (^osReply)(BOOL, NSError *);
static NSWindow *ownedWindow(uintptr_t bits)
{
    if (bits == 43)
    {
        return (NSWindow *)replacementWindow;
    }
    return windowOpen && bits == 42 ? (NSWindow *)fakeWindow : nil;
}
static void startPolicy(LAContext *context, void (^reply)(BOOL, NSError *))
{
    (void)context;
    starts++;
    osReply = [reply copy];
}
#import "../../src/ChunkMaestro.OwnerVerification/Native/mac-owner.m"
#define CHECK(expression) do { if (!(expression)) { fprintf(stderr, "callback failed line %d\n", __LINE__); exit(1); } count++; } while (0)
static unsigned count;
static unsigned deliveries;
static int32_t observed;
static BOOL acknowledge = YES;
static BOOL acknowledgeDrain = YES;
static int32_t receive(uint64_t request, uint64_t generation, uintptr_t window,
    const uint8_t *nonce, size_t size, int32_t result, uint32_t phase, void *cookie)
{
    CHECK(request != 0 && generation == 1 && window != 0);
    CHECK(nonce != NULL && size == 32 && nonce[0] == 1 && cookie == (void *)1);
    if (phase == 1)
    {
        CHECK(active.count == 1);
        uint64_t replacement = 0;
        uint8_t replacementNonce[32] = {1};
        uint8_t replacementSession[16] = {1};
        CHECK(cm_owner_create(2, replacementNonce, 32, replacementSession, 16, 1, &replacement) == CM_CONTEXT_CREATED);
        CHECK(cm_owner_bind_window_explicit(replacement, 43, 1) == CM_WINDOW_CAPTURED);
        CHECK(cm_owner_evaluate_explicit(replacement, 43, 1, receive, (void *)1) == CM_FAILURE);
        CHECK(cm_owner_release(replacement) == CM_CANCELED);
        return acknowledgeDrain ? 1 : 0;
    }
    deliveries++;
    observed = result;
    // Re-entering native code proves no state/registry gate surrounds external delivery.
    CHECK(cm_owner_cancel(request) == CM_CANCELED || lookup(request) == nil);
    return acknowledge ? 1 : 0;
}
static void pump(void)
{
    __block BOOL done = NO;
    dispatch_async(dispatch_get_main_queue(), ^
    {
        done = YES;
    });
    while (!done)
    {
        [NSRunLoop.currentRunLoop runUntilDate:[NSDate dateWithTimeIntervalSinceNow:0.001]];
    }
}
static uint64_t create(void)
{
    uint8_t nonce[32] = {1};
    uint8_t session[16] = {1};
    uint64_t request = 0;
    CHECK(cm_owner_create(2, nonce, 32, session, 16, 1, &request) == CM_CONTEXT_CREATED);
    return request;
}
static void queue(uint64_t request, uintptr_t window)
{
    CHECK(cm_owner_bind_window_explicit(request, window, 1) == CM_WINDOW_CAPTURED);
    CHECK(cm_owner_evaluate_explicit(request, window, 1, receive, (void *)1) == CM_EVALUATION_QUEUED);
}
int main(int argc, const char **argv)
{
    @autoreleasepool
    {
        fakeWindow = [NSObject new];
        replacementWindow = [NSObject new];
        if (argc == 2 && strcmp(argv[1], "--refuse-drain") == 0)
        {
            acknowledgeDrain = NO;
            uint64_t refused = create();
            queue(refused, 42);
            pump();
            CHECK(starts == 1 && active.count == 1);
            osReply(YES, nil);
            pump();
            CHECK(deliveries == 1 && active.count == 1);
            CHECK(cm_owner_release(refused) == CM_CANCELED);
            CHECK(active.count == 1);
            uint64_t blocked = create();
            CHECK(cm_owner_bind_window_explicit(blocked, 42, 1) == CM_WINDOW_CAPTURED);
            CHECK(cm_owner_evaluate_explicit(blocked, 42, 1, receive, (void *)1) == CM_FAILURE);
            cm_owner_release(blocked);
            osReply(YES, nil);
            pump();
            CHECK(deliveries == 1 && active.count == 1 && starts == 1);
            osReply = nil;
            printf("refused drain assertions: %u\n", count);
            return 0;
        }
        CHECK(argc == 1);
        CHECK(cm_owner_abi() == 2 && sizeof(int32_t) == 4 && sizeof(uint64_t) == 8);
        uint64_t request = create();
        queue(request, 42);
        CHECK(cm_owner_cancel(request) == CM_CANCELED);
        pump();
        CHECK(starts == 0 && deliveries == 1 && observed == CM_RAW_CANCELED && active.count == 0);
        cm_owner_release(request);

        request = create();
        queue(request, 42);
        pump();
        CHECK(starts == 1 && deliveries == 1 && active.count == 1);
        CHECK(cm_owner_release(request) == CM_CANCELED);
        osReply(YES, nil);
        osReply(YES, nil);
        pump();
        CHECK(deliveries == 2 && observed == CM_RAW_CANCELED && active.count == 0);
        osReply = nil;

        request = create();
        CHECK(cm_owner_evaluate_explicit(request, 42, 2, receive, (void *)1) == CM_INVALID_INPUT);
        CHECK(cm_owner_bind_window_explicit(request, UINTPTR_MAX, 1) == CM_INVALID_INPUT);
        CHECK(starts == 1 && active.count == 0);
        cm_owner_release(request);

        request = create();
        queue(request, 42);
        pump();
        windowOpen = NO;
        osReply(YES, nil);
        pump();
        CHECK(observed == CM_RAW_INVALID_WINDOW && active.count == 0);
        cm_owner_release(request);
        osReply = nil;
        windowOpen = YES;

        request = create();
        queue(request, 42);
        pump();
        osReply(YES, nil);
        pump();
        CHECK(observed == CM_RAW_POLICY_SATISFIED && active.count == 0);
        cm_owner_release(request);
        osReply = nil;

        request = create();
        queue(request, 42);
        pump();
        [NSNotificationCenter.defaultCenter postNotificationName:NSWindowWillCloseNotification object:fakeWindow];
        osReply(YES, nil);
        pump();
        CHECK(observed == CM_RAW_CANCELED && active.count == 0);
        cm_owner_release(request);
        osReply = nil;

        request = create();
        acknowledge = NO;
        queue(request, 42);
        pump();
        osReply(NO, [NSError errorWithDomain:@"foreign" code:1 userInfo:nil]);
        pump();
        CHECK(observed == CM_RAW_UNKNOWN && active.count == 1);
        unsigned before = deliveries;
        osReply(YES, nil);
        pump();
        CHECK(deliveries == before && active.count == 1);
        cm_owner_release(request);
        CHECK(active.count == 1); // Failed delivery never becomes safe cleanup.
        uint64_t replacement = create();
        CHECK(cm_owner_bind_window_explicit(replacement, 42, 1) == CM_WINDOW_CAPTURED);
        CHECK(cm_owner_evaluate_explicit(replacement, 42, 1, receive, (void *)1) == CM_FAILURE);
        cm_owner_release(replacement);
        pump();
        osReply = nil;
        CHECK(policyResult(NO, [NSError errorWithDomain:LAErrorDomain code:LAErrorUserCancel userInfo:nil]) == CM_RAW_CANCELED);
        CHECK(policyResult(NO, [NSError errorWithDomain:LAErrorDomain code:12345 userInfo:nil]) == CM_RAW_UNKNOWN);
        printf("callback simulation assertions: %u\n", count);
    }
    return 0;
}
