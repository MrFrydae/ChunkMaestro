#define _GNU_SOURCE
#include "session_escrow.h"
#include <assert.h>
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/vfs.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>
#if defined(__aarch64__)
#define TEST_RAW_LARGEFILE 0400000
#else
#define TEST_RAW_LARGEFILE 0100000
#endif
static const char epoch[] = "0123456789abcdef0123456789abcdef";
#ifdef CM_ESCROW_CONTROLS
#include "session_escrow_controls.h"
static int live[64];
static int next_fd;
static unsigned closes, sends, barriers, listens, dup_calls, polls, clocks;
static int fail_dup, fail_stat, fail_fs, fail_close, notify_fail, barrier_fail;
static int thread_fd, legacy, alias, poll_error, poll_events, listen_count;
static int returned_names, clock_fail, nonblocking_origin;
static uint64_t now_usec;
static int origin[64];
static void reset(void)
{
    memset(live, 0, sizeof(live));
    memset(origin, 0, sizeof(origin));
    next_fd = 10;
    closes = sends = barriers = listens = dup_calls = polls = clocks = 0;
    fail_dup = fail_stat = fail_fs = fail_close = notify_fail = barrier_fail = 0;
    thread_fd = legacy = alias = poll_error = poll_events = returned_names = clock_fail = 0;
    nonblocking_origin = 0;
    listen_count = 2;
    now_usec = 1000000;
}
int cm_control_fcntl(int fd, int cmd, ...)
{
    if (cmd == F_DUPFD_CLOEXEC)
    {
        ++dup_calls;
        if ((int)dup_calls == fail_dup)
        {
            errno = EINTR;
            return -1;
        }
        int duplicate = next_fd++;
        assert(duplicate < 64);
        live[duplicate] = 1;
        origin[duplicate] = fd;
        return duplicate;
    }
    if (cmd == F_GETFL)
    {
        return O_RDWR | TEST_RAW_LARGEFILE | (thread_fd ? O_EXCL : 0) |
            ((origin[fd] ? origin[fd] : fd) == nonblocking_origin ? O_NONBLOCK : 0);
    }
    assert(cmd == F_SETFD);
    return 0;
}
int cm_control_fstat(int fd, struct stat *st)
{
    if (fail_stat)
    {
        errno = EINTR;
        return -1;
    }
    memset(st, 0, sizeof(*st));
    st->st_dev = 7;
    st->st_ino = (ino_t)(alias ? 42 : (origin[fd] ? origin[fd] : fd));
    return 0;
}
int cm_control_fstatfs(int fd, struct statfs *fs)
{
    (void)fd;
    if (fail_fs)
    {
        errno = EINTR;
        return -1;
    }
    memset(fs, 0, sizeof(*fs));
    fs->f_type = legacy ? 0x09041934 : 0x50494446;
    return 0;
}
int cm_control_close(int fd)
{
    assert(fd >= 0 && fd < 64 && live[fd]);
    live[fd] = 0;
    ++closes;
    if (fail_close)
    {
        errno = EINTR;
        return -1;
    }
    return 0;
}
int cm_control_poll(struct pollfd *p, nfds_t n, int timeout)
{
    assert(n == 1 && timeout == 0 && live[p->fd]);
    ++polls;
    if (poll_error)
    {
        errno = EINTR;
        return -1;
    }
    p->revents = (short)poll_events;
    return poll_events ? 1 : 0;
}
int cm_control_clock(clockid_t id, struct timespec *out)
{
    assert(id == CLOCK_BOOTTIME);
    ++clocks;
    if (clock_fail)
    {
        errno = EINTR;
        return -1;
    }
    out->tv_sec = (time_t)(now_usec / 1000000);
    out->tv_nsec = (long)(now_usec % 1000000) * 1000;
    return 0;
}
int sd_pid_notify_with_fds(pid_t pid, int unset, const char *state, const int *fd, unsigned count)
{
    assert(pid == 0 && unset == 0 && count == 1 && live[*fd]);
    assert(strstr(state, "FDSTORE=1\nFDPOLL=0\nFDNAME=cm-") == state);
    assert(strstr(state, sends == 0 ? "cm-worker-" : "cm-agent-"));
    ++sends;
    return (int)sends == notify_fail ? -EINTR : 1;
}
int sd_notify_barrier(int unset, uint64_t timeout)
{
    assert(unset == 0 && timeout > 0 && timeout <= 30000000);
    ++barriers;
    return barrier_fail ? -ETIMEDOUT : 1;
}
int sd_listen_fds_with_names(int unset, char ***out)
{
    assert(unset == 1);
    ++listens;
    if (listen_count < 0)
    {
        return listen_count;
    }
    *out = calloc(3, sizeof(char *));
    assert(*out);
    char a[48], b[48];
    snprintf(a, sizeof(a), "cm-worker-%s", epoch);
    snprintf(b, sizeof(b), "cm-agent-%s", epoch);
    (*out)[0] = strdup(returned_names == 1 ? b : a);
    (*out)[1] = strdup(returned_names == 2 ? a : (returned_names == 1 ? a : b));
    assert((*out)[0] && (*out)[1]);
    return listen_count;
}
static void empty(void)
{
    for (unsigned i = 0; i < 64; ++i)
    {
        assert(!live[i]);
    }
}
static cm_escrow *bound(void)
{
    cm_escrow *s = NULL;
    assert(cm_escrow_bind(40, 41, epoch, &s) == CM_ESCROW_OK && s);
    return s;
}
static void controls(void)
{
    unsigned cases = 0;
    for (int fault = 1; fault <= 6; ++fault)
    {
        reset();
        if (fault <= 2) fail_dup = fault;
        if (fault == 3) fail_stat = 1;
        if (fault == 4) fail_fs = 1;
        if (fault == 5) thread_fd = 1;
        if (fault == 6) legacy = 1;
        cm_escrow *s = NULL;
        assert(cm_escrow_bind(40, 41, epoch, &s) != CM_ESCROW_OK && !s);
        assert(dup_calls <= 2);
        empty();
        ++cases;
    }
    reset();
    alias = 1;
    cm_escrow *s = NULL;
    assert(cm_escrow_bind(40, 41, epoch, &s) == CM_ESCROW_IDENTITY && !s);
    empty();
    ++cases;
    /* Independent open descriptions of one task may have different flags. */
    reset();
    alias = 1;
    nonblocking_origin = 41;
    assert(cm_escrow_bind(40, 41, epoch, &s) == CM_ESCROW_IDENTITY && !s);
    assert(closes == 2);
    empty();
    ++cases;
    reset();
    nonblocking_origin = 41;
    s = bound();
    cm_escrow_identity mixed_flags[2];
    assert(cm_escrow_metadata(s, mixed_flags) == CM_ESCROW_OK);
    assert(mixed_flags[0].flags == O_RDWR);
    assert(mixed_flags[1].flags == (O_RDWR | O_NONBLOCK));
    assert(cm_escrow_close(&s) == CM_ESCROW_OK && !s);
    empty();
    ++cases;
    for (int fault = 0; fault <= 6; ++fault)
    {
        reset();
        s = bound();
        if (fault <= 2) notify_fail = fault;
        if (fault == 3) barrier_fail = 1;
        if (fault == 4) now_usec = 2000000;
        if (fault == 5) clock_fail = 1;
        cm_escrow_result result = cm_escrow_upload(s, 2000000);
        if (fault == 0 || fault == 6)
        {
            assert(result == CM_ESCROW_OK && sends == 2 && barriers == 1);
            result = cm_escrow_upload(s, 2000000);
            assert(result == CM_ESCROW_REPLAY);
        }
        else
        {
            assert(result != CM_ESCROW_OK);
            unsigned prior = sends;
            assert(cm_escrow_upload(s, 2000000) == result && sends == prior);
        }
        assert(cm_escrow_close(&s) == result && !s);
        empty();
        ++cases;
    }
    for (int event = 0; event < 5; ++event)
    {
        reset();
        s = bound();
        poll_events = event == 0 ? 0 : event == 1 ? POLLIN : event == 2 ? POLLHUP : POLLERR;
        poll_error = event == 4;
        cm_escrow_observation observation;
        cm_escrow_result result = cm_escrow_observe(s, 0, &observation);
        if (event < 3)
        {
            assert(result == CM_ESCROW_OK);
            assert(observation.readable == (event == 1));
            assert(observation.hup == (event == 2));
        }
        else
        {
            assert(result == CM_ESCROW_IO && !observation.hup && !observation.readable);
            assert(cm_escrow_observe(s, 0, &observation) == result && polls == 1);
        }
        assert(cm_escrow_close(&s) == result);
        empty();
        ++cases;
    }
    reset();
    s = bound();
    fail_close = 1;
    assert(cm_escrow_close(&s) == CM_ESCROW_IO && closes == 2 && !s);
    empty();
    ++cases;
    /* Test handback only with injected close, never real inherited host FDs. */
    for (int fault = 0; fault < 14; ++fault)
    {
        pid_t test_child = fork();
        assert(test_child >= 0);
        if (test_child != 0)
        {
            int status;
            assert(waitpid(test_child, &status, 0) == test_child);
            assert(WIFEXITED(status) && WEXITSTATUS(status) == 0);
            ++cases;
            continue;
        }
        reset();
        live[3] = live[4] = 1;
        origin[3] = 40;
        origin[4] = 41;
        char pid[32], labels[96];
        snprintf(pid, sizeof(pid), "%ld", (long)getpid());
        snprintf(labels, sizeof(labels), "cm-worker-%s:cm-agent-%s", epoch, epoch);
        assert(setenv("LISTEN_PID", pid, 1) == 0);
        assert(setenv("LISTEN_FDS", "2", 1) == 0);
        assert(setenv("LISTEN_FDNAMES", labels, 1) == 0);
        cm_escrow_identity expected[2] = { {7, 40, O_RDWR}, {7, 41, O_RDWR} };
        if (fault == 1) expected[1].inode++;
        if (fault == 2) returned_names = 2;
        if (fault == 3) listen_count = 1;
        if (fault == 4) listen_count = 3;
        if (fault == 5) listen_count = -EINTR;
        if (fault == 6) assert(setenv("LISTEN_PID", "0", 1) == 0);
        if (fault == 7) assert(setenv("LISTEN_FDNAMES", "truncated", 1) == 0);
        if (fault == 8) thread_fd = 1;
        if (fault == 9) legacy = 1;
        if (fault == 10)
        {
            returned_names = 1;
            origin[3] = 41;
            origin[4] = 40;
        }
        if (fault == 11)
        {
            alias = 1;
            nonblocking_origin = 41;
            expected[0].inode = expected[1].inode = 42;
            expected[1].flags |= O_NONBLOCK;
        }
        if (fault == 12) expected[1].flags |= O_NONBLOCK;
        if (fault == 13)
        {
            nonblocking_origin = 41;
            expected[1].flags |= O_NONBLOCK;
        }
        cm_escrow_result result = cm_escrow_handback(epoch, expected, &s);
        if (fault == 0 || fault == 10 || fault == 13)
        {
            assert(result == CM_ESCROW_OK && s);
            cm_escrow_identity actual[2];
            assert(cm_escrow_metadata(s, actual) == CM_ESCROW_OK);
            assert(actual[0].inode == 40 && actual[1].inode == 41);
            assert(actual[0].flags == expected[0].flags && actual[1].flags == expected[1].flags);
            assert(cm_escrow_close(&s) == CM_ESCROW_OK);
        }
        else
        {
            assert(result != CM_ESCROW_OK && !s);
        }
        assert(closes == 2 && listens <= 1);
        assert(cm_escrow_handback(epoch, expected, &s) == CM_ESCROW_REPLAY && !s);
        assert(closes == 2);
        empty();
        exit(0);
    }
    unsetenv("LISTEN_PID");
    unsetenv("LISTEN_FDS");
    unsetenv("LISTEN_FDNAMES");
    printf("PASS: %u deterministic escrow cases (not manager/kernel acceptance)\n", cases);
}
#else
static void kernel(void)
{
    /* No notify calls: never contact the host manager. */
    int gate[2];
    assert(pipe2(gate, O_CLOEXEC) == 0);
    pid_t child = fork();
    assert(child >= 0);
    if (child == 0)
    {
        close(gate[1]);
        char byte;
        ssize_t n = read(gate[0], &byte, 1);
        _exit(n == 0 ? 0 : 1);
    }
    close(gate[0]);
    int worker = (int)syscall(SYS_pidfd_open, child, 0);
    int agent = (int)syscall(SYS_pidfd_open, getpid(), 0);
    if (worker < 0 || agent < 0)
    {
        close(gate[1]);
        waitpid(child, NULL, 0);
        if (worker >= 0) close(worker);
        if (agent >= 0) close(agent);
        fputs("NOT RUN: pidfd_open prerequisite unavailable\n", stderr);
        exit(77);
    }
    cm_escrow *s = NULL;
    cm_escrow_result result = cm_escrow_bind(worker, agent, epoch, &s);
    if (result == CM_ESCROW_IDENTITY)
    {
        close(gate[1]);
        waitpid(child, NULL, 0);
        close(worker);
        close(agent);
        fputs("NOT RUN: qualified pidfs/non-thread identity unavailable\n", stderr);
        exit(77);
    }
    assert(result == CM_ESCROW_OK && s);
    cm_escrow_identity before[2], after[2];
    assert(cm_escrow_metadata(s, before) == CM_ESCROW_OK);
    close(worker);
    close(agent);
    cm_escrow_observation observed;
    assert(cm_escrow_observe(s, 0, &observed) == CM_ESCROW_OK);
    assert(!observed.readable && !observed.hup);
    close(gate[1]);
    /* waitid WNOWAIT waits for exit but retains the zombie/original task. */
    siginfo_t info;
    assert(waitid(P_PID, (id_t)child, &info, WEXITED | WNOWAIT) == 0);
    assert(cm_escrow_observe(s, 0, &observed) == CM_ESCROW_OK);
    assert(observed.readable && !observed.hup);
    assert(waitpid(child, NULL, 0) == child);
    assert(cm_escrow_observe(s, 0, &observed) == CM_ESCROW_OK);
    assert(observed.readable && observed.hup);
    assert(cm_escrow_metadata(s, after) == CM_ESCROW_OK);
    assert(memcmp(before, after, sizeof(before)) == 0);
    assert(cm_escrow_close(&s) == CM_ESCROW_OK && !s);
    int self = (int)syscall(SYS_pidfd_open, getpid(), 0);
    assert(self >= 0);
    assert(cm_escrow_bind(self, self, epoch, &s) == CM_ESCROW_IDENTITY && !s);
    int nonblocking_self = (int)syscall(SYS_pidfd_open, getpid(), O_NONBLOCK);
    assert(nonblocking_self >= 0);
    assert(cm_escrow_bind(self, nonblocking_self, epoch, &s) == CM_ESCROW_IDENTITY && !s);
    close(nonblocking_self);
    int thread = (int)syscall(SYS_pidfd_open, getpid(), O_EXCL);
    assert(thread >= 0);
    assert(cm_escrow_bind(thread, self, epoch, &s) == CM_ESCROW_IDENTITY && !s);
    close(thread);
    close(self);
    int regular = open("/dev/null", O_RDWR | O_CLOEXEC);
    assert(regular >= 0);
    assert(cm_escrow_bind(regular, regular, epoch, &s) == CM_ESCROW_IDENTITY && !s);
    close(regular);
    puts("PASS: original pidfs ownership, live/zombie/reaped observations, duplicate/thread/type refusal");
}
#endif
int main(void)
{
    alarm(15);
#ifdef CM_ESCROW_CONTROLS
    controls();
#else
    kernel();
#endif
    return 0;
}
