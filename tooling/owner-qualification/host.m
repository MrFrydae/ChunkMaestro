#import <AppKit/AppKit.h>
#import <CoreGraphics/CoreGraphics.h>
#import "mac-owner.h"
#include <string.h>
#include <stdlib.h>
#include <unistd.h>

static BOOL interactive;
static BOOL initialPresentationReady;
static BOOL attempted;
static BOOL transferred;
static BOOL terminal;
static BOOL drained;
static BOOL invalidDelivery;
static int32_t terminalResult;
static uint64_t request;
static const uint64_t generation = 1;
static uint8_t nonce[32];
static uintptr_t windowBits;
static int failures;
static NSWindow *fixtureWindow;
static NSTextField *stateLabel;
static NSButton *verifyButton;

static void wipe(uint8_t *bytes, size_t size)
{
    volatile uint8_t *target = bytes;
    for (size_t index = 0; index < size; index++)
    {
        target[index] = 0;
    }
}

static void event(const char *name, const char *state)
{
    printf("{\"event\":\"%s\",\"state\":\"%s\"}\n", name, state);
    fflush(stdout);
}

static void check(BOOL condition, const char *name)
{
    event(name, condition ? "PASS" : "FAIL");
    if (!condition)
    {
        failures++;
    }
}

static uint64_t createRequest(void)
{
    uint8_t session[16];
    arc4random_buf(nonce, sizeof(nonce));
    arc4random_buf(session, sizeof(session));
    uint64_t identifier = 0;
    int32_t status = cm_owner_create(2, nonce, sizeof(nonce), session, sizeof(session),
        generation, &identifier);
    wipe(session, sizeof(session));
    check(status == CM_CONTEXT_CREATED && identifier != 0, "abi2-create");
    return identifier;
}

static BOOL localSessionAvailable(void)
{
    NSDictionary *session = CFBridgingRelease(CGSessionCopyCurrentDictionary());
    return [session[(__bridge NSString *)kCGSessionOnConsoleKey] boolValue] &&
        [session[(__bridge NSString *)kCGSessionLoginDoneKey] boolValue] &&
        [session[(__bridge NSString *)kCGSessionUserIDKey] unsignedIntValue] == getuid();
}

enum
{
    PresentationAppInactive = 1u << 0,
    PresentationNotKey = 1u << 1,
    PresentationOffSpace = 1u << 2,
    PresentationNotVisible = 1u << 3,
    PresentationMiniaturized = 1u << 4,
    PresentationNoWindowNumber = 1u << 5,
    PresentationOccluded = 1u << 6,
    PresentationNotOwned = 1u << 7,
    PresentationOutsideScreen = 1u << 8,
    PresentationMissingObject = 1u << 9,
    PresentationOffMain = 1u << 10,
    PresentationNoSession = 1u << 11
};

static uint32_t presentationFailures(NSWindow *window, NSApplication *application, NSArray<NSScreen *> *screens)
{
    if (window == nil || application == nil)
    {
        return PresentationMissingObject;
    }
    uint32_t failed = 0;
    if (!application.isActive)
    {
        failed |= PresentationAppInactive;
    }
    if (!window.isKeyWindow)
    {
        failed |= PresentationNotKey;
    }
    if (!window.isOnActiveSpace)
    {
        failed |= PresentationOffSpace;
    }
    if (!window.isVisible)
    {
        failed |= PresentationNotVisible;
    }
    if (window.isMiniaturized)
    {
        failed |= PresentationMiniaturized;
    }
    if (window.windowNumber <= 0)
    {
        failed |= PresentationNoWindowNumber;
    }
    if (!(window.occlusionState & NSWindowOcclusionStateVisible))
    {
        failed |= PresentationOccluded;
    }
    if (![application.windows containsObject:window])
    {
        failed |= PresentationNotOwned;
    }
    BOOL contained = NO;
    for (NSScreen *screen in screens)
    {
        if (!NSIsEmptyRect(window.frame) && NSContainsRect(screen.visibleFrame, window.frame))
        {
            contained = YES;
            break;
        }
    }
    return failed | (contained ? 0 : PresentationOutsideScreen);
}

static BOOL presentationReady(NSWindow *window, NSApplication *application, NSArray<NSScreen *> *screens)
{
    return presentationFailures(window, application, screens) == 0;
}

static void reportPresentationFailures(uint32_t failed)
{
    static const struct
    {
        uint32_t bit;
        const char *reason;
    } reasons[] = {
        { PresentationAppInactive, "application-inactive" },
        { PresentationNotKey, "window-not-key" },
        { PresentationOffSpace, "window-off-active-space" },
        { PresentationNotVisible, "window-not-visible" },
        { PresentationMiniaturized, "window-miniaturized" },
        { PresentationNoWindowNumber, "window-number-unavailable" },
        { PresentationOccluded, "window-occluded" },
        { PresentationNotOwned, "window-not-owned" },
        { PresentationOutsideScreen, "window-outside-visible-screen" },
        { PresentationMissingObject, "presentation-object-unavailable" },
        { PresentationOffMain, "not-main-thread" },
        { PresentationNoSession, "local-session-unavailable" }
    };
    for (size_t index = 0; index < sizeof(reasons) / sizeof(reasons[0]); index++)
    {
        if (failed & reasons[index].bit)
        {
            event("presentation-prerequisite", reasons[index].reason);
        }
    }
}

static BOOL visible(void)
{
    return NSThread.isMainThread && localSessionAvailable() &&
        presentationReady(fixtureWindow, NSApp, NSScreen.screens);
}

static int32_t reply(uint64_t identifier, uint64_t receivedGeneration, uintptr_t window,
    const uint8_t *receivedNonce, size_t size, int32_t result, uint32_t phase, void *cookie)
{
    if (!NSThread.isMainThread || cookie != &request || identifier != request ||
        receivedGeneration != generation || window != windowBits || receivedNonce == NULL ||
        size != sizeof(nonce) || memcmp(receivedNonce, nonce, sizeof(nonce)) != 0 ||
        result < CM_RAW_POLICY_SATISFIED || result > CM_RAW_UNKNOWN || invalidDelivery)
    {
        invalidDelivery = YES;
        event("callback-validation", "UNKNOWN-retained-no-replacement");
        return 0;
    }
    if (phase == 0 && !terminal && !drained)
    {
        terminal = YES;
        terminalResult = result;
        // Fixed numeric category only; no OS error, identity, nonce or session data.
        printf("{\"event\":\"terminal\",\"rawDiagnosticCategory\":%d}\n", result);
        fflush(stdout);
        stateLabel.stringValue = @"Terminal received; drain pending. No owner authority.";
        return 1;
    }
    if (phase == 1 && terminal && !drained && result == terminalResult)
    {
        drained = YES;
        wipe(nonce, sizeof(nonce));
        event("phase1", "ACK-safe-cookie-drain");
        stateLabel.stringValue = @"Drain acknowledged. Diagnostic only. Close to exit.";
        return 1;
    }
    invalidDelivery = YES;
    event("callback-sequence", "UNKNOWN-retained-no-replacement");
    return 0;
}

@interface Fixture : NSObject <NSApplicationDelegate, NSWindowDelegate>
@end
@implementation Fixture
- (void)verify:(id)sender
{
    (void)sender;
    if (!NSThread.isMainThread || !interactive || attempted || !initialPresentationReady)
    {
        return;
    }
    // Consume the process's sole attempt even if any subsequent precondition refuses.
    attempted = YES;
    [(NSButton *)sender setEnabled:NO];
    event("manual-verify", "ATTEMPT-consumed");
    if (!visible())
    {
        event("verify-window", "UNAVAILABLE");
        return;
    }
    request = createRequest();
    if (request == 0 || cm_owner_bind_window_explicit(request, windowBits, generation) != CM_WINDOW_CAPTURED)
    {
        stateLabel.stringValue = @"Capture refused. Attempt consumed; close to exit.";
        event("interactive-capture", "REFUSED");
        return;
    }
    // The only evaluation entry in this host. Never reached by no-prompt launch.
    int32_t status = cm_owner_evaluate_explicit(request, windowBits, generation, reply, &request);
    transferred = status == CM_EVALUATION_QUEUED;
    stateLabel.stringValue = transferred ? @"Evaluation pending. Cancel revokes; drain may remain unknown."
        : @"Evaluation refused; no callback transferred. Attempt consumed.";
    event("interactive-admission", transferred ? "QUEUED-pending" : "REFUSED");
}
- (void)cancel:(id)sender
{
    (void)sender;
    if (!NSThread.isMainThread)
    {
        return;
    }
    // Cancel revokes the prepared attempt too, before any native request exists.
    attempted = YES;
    verifyButton.enabled = NO;
    if (request != 0)
    {
        cm_owner_cancel(request);
    }
    stateLabel.stringValue = transferred && !drained
        ? @"Canceled locally; disposal pending/unknown until phase1. No retry."
        : @"Canceled locally. No authentication or owner authority implied.";
    event("manual-cancel", transferred && !drained ? "PENDING-UNKNOWN" : "LOCAL-revocation");
}
- (void)windowWillClose:(NSNotification *)notification
{
    (void)notification;
    if (interactive)
    {
        if (request != 0)
        {
            cm_owner_release(request);
        }
        event("close-disposal", transferred && !drained ? "PENDING-UNKNOWN-process-exit-not-drain" : "ingress-released");
        exit(failures || invalidDelivery || (transferred && !drained) ? 2 : 0);
    }
}
- (void)applicationDidFinishLaunching:(NSNotification *)notification
{
    (void)notification;
    NSScreen *targetScreen = NSScreen.mainScreen;
    for (NSScreen *screen in NSScreen.screens)
    {
        if (NSPointInRect(NSEvent.mouseLocation, screen.frame))
        {
            targetScreen = screen;
            break;
        }
    }
    if (targetScreen == nil)
    {
        event("presentation", "UNAVAILABLE-screen");
        exit(77);
    }
    fixtureWindow = [[NSWindow alloc] initWithContentRect:NSMakeRect(0, 0, 760, 220)
        styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskClosable
        backing:NSBackingStoreBuffered defer:NO screen:targetScreen];
    NSRect available = targetScreen.visibleFrame;
    NSRect frame = fixtureWindow.frame;
    if (NSIsEmptyRect(available) || frame.size.width > available.size.width ||
        frame.size.height > available.size.height)
    {
        event("presentation", "UNAVAILABLE-screen-geometry");
        exit(77);
    }
    [fixtureWindow setFrameOrigin:NSMakePoint(NSMidX(available) - frame.size.width / 2,
        NSMidY(available) - frame.size.height / 2)];
    fixtureWindow.collectionBehavior = NSWindowCollectionBehaviorMoveToActiveSpace;
    fixtureWindow.releasedWhenClosed = NO;
    fixtureWindow.title = @"ChunkMaestro disposable native qualification — diagnostic only";
    fixtureWindow.delegate = self;
    stateLabel = [NSTextField labelWithString:interactive
        ? @"LATER OWNER-APPROVED TEST ONLY. Verify permits one OS prompt. No retries."
        : @"NO PROMPT: disposable native capture/lifecycle checks. No authentication." ];
    stateLabel.frame = NSMakeRect(20, 140, 720, 60);
    stateLabel.maximumNumberOfLines = 3;
    [fixtureWindow.contentView addSubview:stateLabel];
    if (interactive)
    {
        verifyButton = [NSButton buttonWithTitle:@"Verify — one OS authentication attempt"
            target:self action:@selector(verify:)];
        verifyButton.enabled = NO;
        verifyButton.frame = NSMakeRect(20, 70, 440, 40);
        [fixtureWindow.contentView addSubview:verifyButton];
        NSButton *cancelButton = [NSButton buttonWithTitle:@"Cancel / revoke" target:self action:@selector(cancel:)];
        cancelButton.frame = NSMakeRect(480, 70, 240, 40);
        [fixtureWindow.contentView addSubview:cancelButton];
    }
    [fixtureWindow makeKeyAndOrderFront:nil];
    // A cooperative request, not a guarantee or a focus-stealing workaround.
    [NSApp activate];
    windowBits = (uintptr_t)(__bridge void *)fixtureWindow;
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, NSEC_PER_SEC), dispatch_get_main_queue(), ^
    {
        // Preserve context refusal before querying AppKit window metadata.
        uint32_t unavailable = 0;
        if (!NSThread.isMainThread)
        {
            unavailable = PresentationOffMain;
        }
        else if (!localSessionAvailable())
        {
            unavailable = PresentationNoSession;
        }
        else
        {
            unavailable = presentationFailures(fixtureWindow, NSApp, NSScreen.screens);
        }
        if (unavailable != 0)
        {
            reportPresentationFailures(unavailable);
            event("actual-owned-visible-window", "UNAVAILABLE-presentation");
            exit(77);
        }
        event("window-evidence", "NSWindow-metadata-only-screen-NOT-OBSERVED");
        if (interactive && !attempted)
        {
            initialPresentationReady = YES;
            verifyButton.enabled = YES;
        }
        if (!interactive)
        {
            [self noPrompt];
        }
    });
}
- (void)noPrompt
{
    check(cm_owner_abi() == 2, "production-abi2");
    request = createRequest();
    check(cm_owner_bind_window_explicit(request, 1, generation) == CM_INVALID_INPUT, "foreign-bits-refused");
    check(cm_owner_bind_window_explicit(request, windowBits, generation + 1) == CM_INVALID_INPUT, "stale-generation-refused");
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED, 0), ^
    {
        int32_t status = cm_owner_bind_window_explicit(request, windowBits, generation);
        dispatch_async(dispatch_get_main_queue(), ^
        {
            check(status == CM_UNSUPPORTED, "off-main-capture-refused");
            check(cm_owner_bind_window_explicit(request, windowBits, generation) == CM_WINDOW_CAPTURED, "actual-positive-capture");
            check(cm_owner_bind_window_explicit(request, windowBits, generation) == CM_CANCELED, "single-use-binding");
            int32_t probe = cm_owner_probe(request);
            check(probe == CM_AVAILABLE || probe == CM_UNAVAILABLE, "probe-no-authentication");
            check(cm_owner_cancel(request) == CM_CANCELED, "cancel");
            check(cm_owner_release(request) == CM_CANCELED, "release");
            check(cm_owner_probe(request) == CM_INVALID_INPUT, "released-ingress-refused");
            // FIFO main-queue fence executes after production's queued observer removal.
            dispatch_async(dispatch_get_main_queue(), ^
            {
                event("release-observer-cleanup", "main-queue-fence-observed-no-internal-introspection");
                request = createRequest();
                check(cm_owner_bind_window_explicit(request, windowBits, generation) == CM_WINDOW_CAPTURED, "fresh-request-capture");
                [fixtureWindow close];
                check(cm_owner_probe(request) == CM_CANCELED, "real-will-close-revocation");
                check(cm_owner_release(request) == CM_CANCELED, "close-release");
                dispatch_async(dispatch_get_main_queue(), ^
                {
                    event("close-observer-cleanup", "main-queue-fence-observed");
                    wipe(nonce, sizeof(nonce));
                    event("evaluation-dispatch", "NOT-CALLED-by-no-prompt-control-flow");
                    event("no-prompt-complete", failures == 0 ? "PASS" : "FAIL");
                    exit(failures == 0 ? 0 : 1);
                });
            });
        });
    });
}
@end

int main(int argc, const char *argv[])
{
    if (argc != 2 || (strcmp(argv[1], "--no-prompt") != 0 && strcmp(argv[1], "--interactive") != 0) ||
        getenv("GITHUB_ACTIONS") != NULL)
    {
        event("launch", "REFUSED-explicit-local-mode-required");
        return 64;
    }
    interactive = strcmp(argv[1], "--interactive") == 0;
    @autoreleasepool
    {
        // Public session snapshot is a prerequisite, not ongoing owner qualification.
        if (!localSessionAvailable())
        {
            event("session", "UNAVAILABLE");
            return 77;
        }
        [NSApplication sharedApplication];
        NSApp.activationPolicy = NSApplicationActivationPolicyRegular;
        Fixture *fixture = [Fixture new];
        NSApp.delegate = fixture;
        [NSApp run];
    }
    return 1;
}
