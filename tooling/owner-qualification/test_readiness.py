"""Source-bound Foundation-only action probes: no AppKit app/window or OS evaluation."""
from pathlib import Path
import platform
import subprocess
import tempfile
import unittest

HOST = Path(__file__).with_name('host.m')
HEADER = Path(__file__).resolve().parents[2] / 'src/ChunkMaestro.OwnerVerification/Native'


def probe_source(host):
    verify = host[host.index('- (void)verify:'):host.index('- (void)cancel:')]
    cancel = host[host.index('- (void)cancel:'):host.index('- (void)windowWillClose:')]
    close = host[host.index('- (void)windowWillClose:'):host.index('- (void)applicationDidFinishLaunching:')]
    callback = host[host.index('static int32_t reply('):host.index('@interface Fixture')]
    refusal = host[host.index('        if (unavailable != 0)'):host.index('        event("window-evidence"')]
    ready_start = host.index('        if (interactive && !attempted)', host.index('        event("window-evidence"'))
    ready = host[ready_start:host.index('        if (!interactive)', ready_start)]
    return PREFIX + callback + INTERFACE + verify + cancel + close + '\n@end\n' + '''
static void reenter(void) { [fixture verify:verifyButton]; }
static void sample(BOOL valid)
{
    uint32_t unavailable = valid ? 0 : 1;
''' + refusal + ready + '}\n' + SCENARIOS


PREFIX = r'''
#import <Foundation/Foundation.h>
#include "mac-owner.h"
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#define NSNotification NSObject
@interface NSButton : NSObject
@property BOOL enabled;
@end
@implementation NSButton
@end
@interface ProbeLabel : NSObject
@property(copy) NSString *stringValue;
@end
@implementation ProbeLabel
@end
@class Fixture;
static Fixture *fixture;
static BOOL interactive = YES, initialPresentationReady, attempted, transferred, terminal;
static BOOL drained, invalidDelivery, visibility = YES, creating = YES, capturing = YES, admission = YES;
static uint64_t request, generation = 1;
static uintptr_t windowBits = 2;
static uint8_t nonce[32];
static int32_t terminalResult;
static int failures, creates, captures, evaluations, cancels, releases, visibleCalls, reentry;
static ProbeLabel *stateLabel;
static NSButton *verifyButton;
static void event(const char *name, const char *state)
{
    printf("{\"event\":\"%s\",\"state\":\"%s\"}\n", name, state);
    fflush(stdout);
}
static void reportPresentationFailures(uint32_t value) { (void)value; }
static void wipe(uint8_t *bytes, size_t count) { memset(bytes, 0, count); }
static BOOL visible(void) { visibleCalls++; return visibility; }
static void reenter(void);
static uint64_t createRequest(void)
{
    creates++;
    if (reentry == 1) reenter();
    return creating ? 9 : 0;
}
int32_t cm_owner_bind_window_explicit(uint64_t identifier, uintptr_t window, uint64_t epoch)
{
    (void)identifier; (void)window; (void)epoch; captures++;
    if (reentry == 2) reenter();
    return capturing ? CM_WINDOW_CAPTURED : CM_FAILURE;
}
int32_t cm_owner_evaluate_explicit(uint64_t identifier, uintptr_t window, uint64_t epoch,
    cm_owner_reply callback, void *cookie)
{
    (void)identifier; (void)window; (void)epoch; (void)callback; (void)cookie; evaluations++;
    if (reentry == 3) reenter();
    return admission ? CM_EVALUATION_QUEUED : CM_FAILURE;
}
int32_t cm_owner_cancel(uint64_t identifier) { (void)identifier; cancels++; return CM_CANCELED; }
int32_t cm_owner_release(uint64_t identifier) { (void)identifier; releases++; return CM_CANCELED; }
'''

INTERFACE = r'''
@interface Fixture : NSObject
- (void)verify:(id)sender;
- (void)cancel:(id)sender;
- (void)windowWillClose:(NSNotification *)notification;
@end
@implementation Fixture
'''

SCENARIOS = r'''
#define REQUIRE(value) do { if (!(value)) { fprintf(stderr, "invariant:%s\n", #value); return 43; } } while (0)
static void *offMain(void *unused)
{
    (void)unused;
    @autoreleasepool { [fixture verify:verifyButton]; [fixture cancel:nil]; }
    return NULL;
}
int main(int argc, const char **argv)
{
    @autoreleasepool
    {
        if (argc != 2) return 64;
        fixture = [Fixture new]; stateLabel = [ProbeLabel new]; verifyButton = [NSButton new];
        verifyButton.enabled = NO;
        NSString *scenario = [NSString stringWithUTF8String:argv[1]];
        if ([scenario isEqualToString:@"early"] || [scenario isEqualToString:@"early-fail"])
        {
            [fixture verify:verifyButton];
            REQUIRE(!attempted && creates == 0 && captures == 0 && evaluations == 0 && visibleCalls == 0);
            if ([scenario isEqualToString:@"early-fail"]) sample(NO);
        }
        else if ([scenario isEqualToString:@"cancel-ready"])
        {
            [fixture cancel:nil]; sample(YES); [fixture verify:verifyButton];
            REQUIRE(attempted && !initialPresentationReady && !verifyButton.enabled && creates == 0 && evaluations == 0);
        }
        else if ([scenario isEqualToString:@"close-early"])
        {
            [fixture windowWillClose:nil]; return 44;
        }
        else if ([scenario isEqualToString:@"off-main"])
        {
            sample(YES); pthread_t thread;
            REQUIRE(pthread_create(&thread, NULL, offMain, NULL) == 0);
            REQUIRE(pthread_join(thread, NULL) == 0);
            REQUIRE(!attempted && creates == 0 && evaluations == 0 && visibleCalls == 0 && verifyButton.enabled);
        }
        else if ([scenario isEqualToString:@"noninteractive"])
        {
            interactive = NO; sample(YES); [fixture verify:verifyButton];
            REQUIRE(!attempted && !initialPresentationReady && creates == 0 && evaluations == 0);
        }
        else
        {
            sample(YES);
            REQUIRE(initialPresentationReady && verifyButton.enabled && creates == 0 && evaluations == 0);
            if ([scenario isEqualToString:@"sample-only"]) return 0;
            if ([scenario isEqualToString:@"deny-visible"]) visibility = NO;
            if ([scenario isEqualToString:@"deny-create"]) creating = NO;
            if ([scenario isEqualToString:@"deny-capture"]) capturing = NO;
            if ([scenario isEqualToString:@"deny-admission"]) admission = NO;
            if ([scenario isEqualToString:@"reenter-create"]) reentry = 1;
            if ([scenario isEqualToString:@"reenter-capture"]) reentry = 2;
            if ([scenario isEqualToString:@"reenter-evaluate"]) reentry = 3;
            [fixture verify:verifyButton]; [fixture verify:verifyButton];
            REQUIRE(attempted && !verifyButton.enabled && creates == (visibility ? 1 : 0));
            REQUIRE(captures == (visibility && creating ? 1 : 0));
            REQUIRE(evaluations == (visibility && creating && capturing ? 1 : 0));
            REQUIRE(transferred == (visibility && creating && capturing && admission));
            if ([scenario isEqualToString:@"cancel-pending"])
            {
                [fixture cancel:nil]; [fixture verify:verifyButton];
                REQUIRE(cancels == 1 && evaluations == 1 && transferred && !drained && !verifyButton.enabled);
            }
            if ([scenario isEqualToString:@"close-phase0"] || [scenario isEqualToString:@"close-phase1"] ||
                [scenario isEqualToString:@"close-duplicate"])
            {
                REQUIRE(reply(request, generation, windowBits, nonce, sizeof(nonce), CM_RAW_POLICY_SATISFIED, 0, &request) == 1);
                REQUIRE(terminal && !drained);
                if ([scenario isEqualToString:@"close-phase1"])
                {
                    REQUIRE(reply(request, generation, windowBits, nonce, sizeof(nonce), CM_RAW_POLICY_SATISFIED, 1, &request) == 1);
                    REQUIRE(drained);
                }
                if ([scenario isEqualToString:@"close-duplicate"])
                {
                    REQUIRE(reply(request, generation, windowBits, nonce, sizeof(nonce), CM_RAW_POLICY_SATISFIED, 0, &request) == 0);
                    REQUIRE(invalidDelivery && !drained);
                }
            }
            if ([scenario isEqualToString:@"close-wrong-id"])
            {
                REQUIRE(reply(request + 1, generation, windowBits, nonce, sizeof(nonce), CM_RAW_POLICY_SATISFIED, 0, &request) == 0);
                REQUIRE(invalidDelivery && !drained);
            }
            if ([scenario isEqualToString:@"close-phase1-first"])
            {
                REQUIRE(reply(request, generation, windowBits, nonce, sizeof(nonce), CM_RAW_POLICY_SATISFIED, 1, &request) == 0);
                REQUIRE(invalidDelivery && !drained);
            }
            if ([scenario hasPrefix:@"close-"]) { [fixture windowWillClose:nil]; return 44; }
        }
        puts("PASS: inert source-bound readiness scenario");
    }
    return 0;
}
'''


class ReadinessTests(unittest.TestCase):
    def test_initial_control_and_single_timer_are_wired_to_actual_source(self):
        host = HOST.read_text()
        button = host[host.index('verifyButton = [NSButton buttonWithTitle:'):host.index('[fixtureWindow makeKeyAndOrderFront:')]
        self.assertLess(button.index('verifyButton.enabled = NO;'), button.index('[fixtureWindow.contentView addSubview:verifyButton]'))
        self.assertEqual(host.count('dispatch_after('), 1)
        sample = host[host.index('dispatch_after('):host.index('- (void)noPrompt')]
        self.assertLess(sample.index('if (unavailable != 0)'), sample.index('initialPresentationReady = YES;'))
        self.assertEqual(host.count('initialPresentationReady = YES;'), 1)
        self.assertEqual(host.count('cm_owner_evaluate_explicit('), 1)
        self.assertNotIn('[self verify:', sample)
        self.assertNotIn('attempted = NO', host)

    @unittest.skipUnless(platform.system() == 'Darwin', 'Inert Foundation probe requires macOS compiler')
    def test_source_bound_handler_ordering_and_callback_classification(self):
        host = HOST.read_text()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); source = root / 'readiness.m'; binary = root / 'readiness'
            source.write_text(probe_source(host))
            build = subprocess.run(['/usr/bin/clang', '-fobjc-arc', '-Wall', '-Wextra', '-Werror',
                                    '-I', str(HEADER), str(source), '-framework', 'Foundation', '-o', str(binary)],
                                   capture_output=True, text=True, timeout=60)
            self.assertEqual(build.returncode, 0, build.stderr)
            self.assertNotIn('AppKit/', source.read_text())
            cases = {'early': 0, 'early-fail': 77, 'cancel-ready': 0, 'sample-only': 0, 'once': 0,
                     'off-main': 0, 'noninteractive': 0, 'reenter-create': 0, 'reenter-capture': 0,
                     'reenter-evaluate': 0, 'deny-visible': 0, 'deny-create': 0, 'deny-capture': 0,
                     'deny-admission': 0, 'cancel-pending': 0, 'close-early': 0, 'close-pending': 2,
                     'close-phase0': 2, 'close-phase1': 0, 'close-duplicate': 2,
                     'close-wrong-id': 2, 'close-phase1-first': 2}
            for case, expected in cases.items():
                with self.subTest(case=case):
                    result = subprocess.run([str(binary), case], capture_output=True, text=True, timeout=5)
                    self.assertEqual(result.returncode, expected, result.stderr)
                    if case in ('close-pending', 'close-phase0', 'close-duplicate', 'close-wrong-id', 'close-phase1-first'):
                        self.assertIn('PENDING-UNKNOWN-process-exit-not-drain', result.stdout)
                    if case == 'close-phase1': self.assertIn('ACK-safe-cookie-drain', result.stdout)
                    if case == 'early-fail': self.assertNotIn('manual-verify', result.stdout)


if __name__ == '__main__':
    unittest.main()
