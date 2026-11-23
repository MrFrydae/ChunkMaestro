#import <Foundation/Foundation.h>
#import <LocalAuthentication/LocalAuthentication.h>
#import <AppKit/AppKit.h>
#import "mac-owner.h"

@interface CMOwnerRequest : NSObject
@property(nonatomic, strong) LAContext *context;
@property(nonatomic, copy) NSData *nonce;
@property(nonatomic, copy) NSData *session;
@property(nonatomic) uint64_t generation;
@property(nonatomic) BOOL canceled;
@end
@implementation CMOwnerRequest
@end

static NSMutableDictionary<NSNumber *, CMOwnerRequest *> *requests;
static uint64_t nextRequest;

static NSObject *registryGate(void)
{
    static NSObject *gate;
    static dispatch_once_t once;
    dispatch_once(&once, ^
    {
        gate = [NSObject new];
        requests = [NSMutableDictionary new];
    });
    return gate;
}

static CMOwnerRequest *lookup(uint64_t request)
{
    @synchronized(registryGate())
    {
        return requests[@(request)];
    }
}

static BOOL nonzero(const uint8_t *bytes, size_t size)
{
    uint8_t combined = 0;
    for (size_t index = 0; index < size; index++)
    {
        combined |= bytes[index];
    }
    return combined != 0;
}

uint32_t cm_owner_abi(void)
{
    return 1;
}

int32_t cm_owner_create(uint32_t abi, const uint8_t *nonce, size_t nonce_size,
    const uint8_t *session, size_t session_size, uint64_t generation, uint64_t *request)
{
    @autoreleasepool
    {
        if (request == NULL)
        {
            return CM_INVALID_INPUT;
        }
        *request = 0;
        if (abi != 1 || nonce == NULL || nonce_size != 32 || session == NULL ||
            session_size != 16 || generation == 0 || !nonzero(nonce, 32) || !nonzero(session, 16))
        {
            return CM_INVALID_INPUT;
        }
        @try
        {
            CMOwnerRequest *value = [CMOwnerRequest new];
            value.context = [LAContext new];
            if (value.context == nil)
            {
                return CM_FAILURE;
            }
            value.context.touchIDAuthenticationAllowableReuseDuration = 0;
            // Defense in depth: this partial bridge cannot display an authentication UI.
            value.context.interactionNotAllowed = YES;
            value.nonce = [NSData dataWithBytes:nonce length:32];
            value.session = [NSData dataWithBytes:session length:16];
            value.generation = generation;
            @synchronized(registryGate())
            {
                if (nextRequest == UINT64_MAX)
                {
                    [value.context invalidate];
                    return CM_FAILURE;
                }
                *request = ++nextRequest;
                requests[@(*request)] = value;
            }
            return CM_CONTEXT_CREATED;
        }
        @catch (NSException *exception)
        {
            (void)exception;
            return CM_FAILURE;
        }
    }
}

int32_t cm_owner_probe(uint64_t request)
{
    @autoreleasepool
    {
        CMOwnerRequest *value = lookup(request);
        if (value == nil)
        {
            return CM_INVALID_INPUT;
        }
        @synchronized(value)
        {
            if (value.canceled)
            {
                return CM_CANCELED;
            }
            @try
            {
                return [value.context canEvaluatePolicy:LAPolicyDeviceOwnerAuthentication error:nil]
                    ? CM_AVAILABLE : CM_UNAVAILABLE;
            }
            @catch (NSException *exception)
            {
                (void)exception;
                return CM_FAILURE;
            }
        }
    }
}

static int32_t cancel(CMOwnerRequest *value)
{
    @synchronized(value)
    {
        // Revoke before invalidation; repeated cancellation never revives a request.
        value.canceled = YES;
        @try
        {
            [value.context invalidate];
            return CM_CANCELED;
        }
        @catch (NSException *exception)
        {
            (void)exception;
            return CM_FAILURE;
        }
    }
}

int32_t cm_owner_cancel(uint64_t request)
{
    @autoreleasepool
    {
        CMOwnerRequest *value = lookup(request);
        return value == nil ? CM_INVALID_INPUT : cancel(value);
    }
}

int32_t cm_owner_prepare(uint64_t request, uintptr_t window, uint64_t generation)
{
    @autoreleasepool
    {
        CMOwnerRequest *value = lookup(request);
        if (value == nil || window == 0 || generation == 0)
        {
            return CM_INVALID_INPUT;
        }
        @synchronized(value)
        {
            if (value.canceled)
            {
                return CM_CANCELED;
            }
            if (value.generation != generation)
            {
                return CM_INVALID_INPUT;
            }
        }
        // No synchronous dispatch: no lock or caller thread can wait on AppKit.
        // Future composition must invoke validation on its real UI dispatcher.
        if (![NSThread isMainThread])
        {
            return CM_UNSUPPORTED;
        }
        @try
        {
            // Do not convert the supplied integer into an ObjC object or message it.
            for (NSWindow *owned in NSApp.windows)
            {
                if ((uintptr_t)(__bridge void *)owned == window)
                {
                    // No qualified session/principal observers exist in this slice.
                    return CM_UNSUPPORTED;
                }
            }
            return CM_INVALID_INPUT;
        }
        @catch (NSException *exception)
        {
            (void)exception;
            return CM_FAILURE;
        }
    }
}

int32_t cm_owner_release(uint64_t request)
{
    @autoreleasepool
    {
        CMOwnerRequest *value;
        @synchronized(registryGate())
        {
            value = requests[@(request)];
            [requests removeObjectForKey:@(request)];
        }
        // A concurrent lookup owns a strong reference. No raw ObjC handle crosses ABI.
        // There is no evaluation/callback and therefore no detached callback to drain.
        return value == nil ? CM_INVALID_INPUT : cancel(value);
    }
}
