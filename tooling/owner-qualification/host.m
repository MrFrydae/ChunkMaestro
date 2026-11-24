#import <AppKit/AppKit.h>
#import <CoreGraphics/CoreGraphics.h>
#import "mac-owner.h"
#include <string.h>
#include <stdlib.h>
#include <unistd.h>

static BOOL interactive;
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

static BOOL visible(void)
{
    return NSThread.isMainThread && localSessionAvailable() && fixtureWindow.isVisible &&
        !fixtureWindow.isMiniaturized && fixtureWindow.windowNumber > 0 &&
        [NSApp.windows containsObject:fixtureWindow];
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
    if (!interactive || attempted)
    {
        return;
    }
    // Consume the process's sole attempt even if any subsequent precondition refuses.
    attempted = YES;
    [(NSButton *)sender setEnabled:NO];
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
    fixtureWindow = [[NSWindow alloc] initWithContentRect:NSMakeRect(160, 160, 760, 220)
        styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskClosable
        backing:NSBackingStoreBuffered defer:NO];
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
        verifyButton.frame = NSMakeRect(20, 70, 440, 40);
        [fixtureWindow.contentView addSubview:verifyButton];
        NSButton *cancelButton = [NSButton buttonWithTitle:@"Cancel / revoke" target:self action:@selector(cancel:)];
        cancelButton.frame = NSMakeRect(480, 70, 240, 40);
        [fixtureWindow.contentView addSubview:cancelButton];
    }
    [fixtureWindow makeKeyAndOrderFront:nil];
    [NSApp activateIgnoringOtherApps:YES];
    windowBits = (uintptr_t)(__bridge void *)fixtureWindow;
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, NSEC_PER_SEC), dispatch_get_main_queue(), ^
    {
        if (!visible())
        {
            event("actual-owned-visible-window", "UNAVAILABLE");
            exit(77);
        }
        event("window-evidence", "NSWindow-metadata-only-screen-NOT-OBSERVED");
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
