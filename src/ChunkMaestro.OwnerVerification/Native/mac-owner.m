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
@property(nonatomic) BOOL admitted;
@property(nonatomic) BOOL evaluating;
@property(nonatomic) BOOL terminal;
@property(nonatomic) uint64_t identifier;
@property(nonatomic) uintptr_t window;
@property(nonatomic) cm_owner_reply reply;
@property(nonatomic) void *cookie;
@property(nonatomic, strong) id closeObserver;
@property(nonatomic, strong) NSWindow *boundWindow;
@end
@implementation CMOwnerRequest
@end

static NSMutableDictionary<NSNumber *, CMOwnerRequest *> *requests;
static uint64_t nextRequest;
static NSMutableDictionary<NSNumber *, CMOwnerRequest *> *active;

static NSObject *registryGate(void)
{
    static NSObject *gate;
    static dispatch_once_t once;
    dispatch_once(&once, ^
    {
        gate = [NSObject new];
        requests = [NSMutableDictionary new];
        active = [NSMutableDictionary new];
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
    return 2;
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
        if (abi != 2 || nonce == NULL || nonce_size != 32 || session == NULL ||
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
            // Default operations remain noninteractive; only explicit evaluation enables UI.
            value.context.interactionNotAllowed = YES;
            value.nonce = [NSData dataWithBytes:nonce length:32];
            value.session = [NSData dataWithBytes:session length:16];
            value.generation = generation;
            @synchronized(registryGate())
            {
                if (nextRequest == UINT64_MAX)
                {
                    return CM_FAILURE;
                }
                *request = ++nextRequest;
                value.identifier = *request;
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
        }
        @try
        {
            // Probe never touches the evaluation context and never permits interaction.
            LAContext *probe = [LAContext new];
            probe.interactionNotAllowed = YES;
            probe.touchIDAuthenticationAllowableReuseDuration = 0;
            return [probe canEvaluatePolicy:LAPolicyDeviceOwnerAuthentication error:nil]
                ? CM_AVAILABLE : CM_UNAVAILABLE;
        }
        @catch (NSException *exception)
        {
            (void)exception;
            return CM_FAILURE;
        }
    }
}

static int32_t cancel(CMOwnerRequest *value)
{
    @synchronized(value)
    {
        value.canceled = YES;
    }
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
        // Active ownership is separate and survives registry removal until acknowledged delivery.
        if (value == nil)
        {
            return CM_INVALID_INPUT;
        }
        int32_t result = cancel(value);
        dispatch_async(dispatch_get_main_queue(), ^
        {
            BOOL admitted;
            @synchronized(value)
            {
                admitted = value.admitted;
            }
            if (!admitted && value.closeObserver != nil)
            {
                [NSNotificationCenter.defaultCenter removeObserver:value.closeObserver];
                value.closeObserver = nil;
            }
        });
        return result;
    }
}


// Called on main only. Never reinterpret caller bits as an Objective-C receiver.
#ifndef CM_OWNER_TESTING
static NSWindow *ownedWindow(uintptr_t bits)
{
    for (NSWindow *window in NSApp.windows)
    {
        if ((uintptr_t)(__bridge void *)window == bits && window.isVisible &&
            !window.isMiniaturized && window.windowNumber > 0)
        {
            return window;
        }
    }
    return nil;
}

static void startPolicy(LAContext *context, void (^reply)(BOOL, NSError *))
{
    [context evaluatePolicy:LAPolicyDeviceOwnerAuthentication
        localizedReason:@"Verify the local macOS user for internal policy testing." reply:reply];
}
#endif

static int32_t policyResult(BOOL success, NSError *error)
{
    if (success && error == nil)
    {
        return CM_RAW_POLICY_SATISFIED;
    }
    if (![error.domain isEqualToString:LAErrorDomain])
    {
        return CM_RAW_UNKNOWN;
    }
    switch (error.code)
    {
        case LAErrorUserCancel:
        case LAErrorAppCancel:
        case LAErrorSystemCancel:
            return CM_RAW_CANCELED;
        case LAErrorAuthenticationFailed:
        case LAErrorUserFallback:
            return CM_RAW_DENIED;
        case LAErrorPasscodeNotSet:
        case LAErrorBiometryNotAvailable:
        case LAErrorBiometryNotEnrolled:
        case LAErrorBiometryLockout:
        case LAErrorNotInteractive:
            return CM_RAW_UNAVAILABLE;
        default:
            return CM_RAW_UNKNOWN;
    }
}

// Main queue is the delivery/drain queue; state gate orders revoke vs terminal.
static void finish(CMOwnerRequest *value, int32_t result)
{
    cm_owner_reply reply;
    void *cookie;
    @synchronized(value)
    {
        if (value.terminal)
        {
            return;
        }
        value.terminal = YES;
        result = value.canceled ? CM_RAW_CANCELED : result;
        reply = value.reply;
        cookie = value.cookie;
        value.reply = NULL;
        value.cookie = NULL;
    }
    if (value.closeObserver != nil)
    {
        [NSNotificationCenter.defaultCenter removeObserver:value.closeObserver];
        value.closeObserver = nil;
    }
    // Both acknowledgments must finish before replacement admission. Cookie drain
    // cannot temporarily remove the process-wide unknown-work barrier.
    int32_t acknowledged = reply(value.identifier, value.generation, value.window,
        value.nonce.bytes, value.nonce.length, result, 0, cookie);
    if (acknowledged == 1)
    {
        if (reply(value.identifier, value.generation, value.window,
            value.nonce.bytes, value.nonce.length, result, 1, cookie) == 1)
        {
            @synchronized(registryGate())
            {
                [active removeObjectForKey:@(value.identifier)];
            }
        }
    }
}

// Explicit no-prompt capture on AppKit. Retaining the actual owned object prevents
// address reuse between capture and queued evaluation. Close irreversibly revokes.
int32_t cm_owner_bind_window_explicit(uint64_t request, uintptr_t window, uint64_t generation)
{
    @autoreleasepool
    {
        if (![NSThread isMainThread])
        {
            return CM_UNSUPPORTED;
        }
        CMOwnerRequest *value = lookup(request);
        if (value == nil || window == 0)
        {
            return CM_INVALID_INPUT;
        }
        @try
        {
            NSWindow *owned = ownedWindow(window);
            if (owned == nil)
            {
                return CM_INVALID_INPUT;
            }
            @synchronized(value)
            {
                if (value.canceled || value.admitted || value.boundWindow != nil)
                {
                    return CM_CANCELED;
                }
                if (value.generation != generation)
                {
                    return CM_INVALID_INPUT;
                }
                value.boundWindow = owned;
                value.window = window;
            }
            value.closeObserver = [NSNotificationCenter.defaultCenter
                addObserverForName:NSWindowWillCloseNotification object:owned queue:nil
                usingBlock:^(NSNotification *notification)
                {
                    (void)notification;
                    cancel(value);
                }];
            return CM_WINDOW_CAPTURED;
        }
        @catch (NSException *exception)
        {
            (void)exception;
            cancel(value);
            return CM_FAILURE;
        }
    }
}

int32_t cm_owner_evaluate_explicit(uint64_t request, uintptr_t window, uint64_t generation,
    cm_owner_reply reply, void *cookie)
{
    @autoreleasepool
    {
        CMOwnerRequest *value = lookup(request);
        if (value == nil || window == 0 || reply == NULL || cookie == NULL)
        {
            return CM_INVALID_INPUT;
        }
        @synchronized(registryGate())
        {
            // A stalled/failed delivery blocks replacement evaluation process-wide.
            if (active.count != 0)
            {
                return CM_FAILURE;
            }
            @synchronized(value)
            {
                if (value.canceled || value.admitted)
                {
                    return CM_CANCELED;
                }
                if (generation != value.generation || value.boundWindow == nil || value.window != window)
                {
                    return CM_INVALID_INPUT;
                }
                value.admitted = YES;
                value.window = window;
                value.reply = reply;
                value.cookie = cookie;
            }
            active[@(request)] = value;
        }
        dispatch_async(dispatch_get_main_queue(), ^
        {
            @try
            {
                NSWindow *owned = ownedWindow(window);
                if (owned == nil || owned != value.boundWindow)
                {
                    finish(value, CM_RAW_INVALID_WINDOW);
                    return;
                }
                BOOL start;
                @synchronized(value)
                {
                    start = !value.canceled;
                    value.evaluating = start;
                }
                if (!start)
                {
                    finish(value, CM_RAW_CANCELED);
                    return;
                }
                // Start admission won. A concurrent revoke may invalidate before this
                // call; it cannot retract admission, and suppresses every late success.
                value.context.interactionNotAllowed = NO;
                startPolicy(value.context, ^(BOOL success, NSError *error)
                    {
                        int32_t result = policyResult(success, error);
                        dispatch_async(dispatch_get_main_queue(), ^
                        {
                            finish(value, ownedWindow(window) == owned ? result : CM_RAW_INVALID_WINDOW);
                        });
                    });
            }
            @catch (NSException *exception)
            {
                (void)exception;
                // An exception after OS admission cannot prove no callback survives.
                // Keep active/cookie owned, unknown, without starting replacement work.
                if (!value.evaluating)
                {
                    finish(value, CM_RAW_UNKNOWN);
                }
            }
        });
        return CM_EVALUATION_QUEUED;
    }
}
