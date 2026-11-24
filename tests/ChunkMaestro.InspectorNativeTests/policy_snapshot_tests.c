#define _GNU_SOURCE
#include "policy_snapshot.h"
#include "policy_snapshot_syscall_controls.h"
#include <assert.h>
#include <dirent.h>
#include <errno.h>
#include <stdarg.h>
#include <stdio.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/syscall.h>
#include <sys/wait.h>
#include <sys/sysmacros.h>
#ifdef CM_POLICY_BRIDGE
#include <pthread.h>
static pthread_mutex_t bridge_mutex = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t bridge_condition = PTHREAD_COND_INITIALIZER;
static int bridge_block, bridge_entered, bridge_released;
static void bridge_pause(int stage);
static int bridge_root;
static char bridge_path[128];
#endif

/* Deterministic controls belong only to separately compiled test objects. */
enum { NONE, SHORT, INTERRUPT, EXHAUST, EARLY, EXTRA, DIFFERENT, OVERSIZE,
    ACL_ZERO, ACL_RANGE, ACL_DENIED, STAT_ERROR, FLAGS_ERROR, OPEN_ERROR, ALLOC_ERROR,
    CLOSE_ERROR, BARRIER, TYPE_MASK, READ_ERROR, ACL_INTERRUPTED, ABI_VECTOR, STAT_DRIFT };
static int fault, barrier_in = -1, barrier_out = -1;
static _Atomic int calls, reads, closes;
static _Atomic int pass;
static int fault_at, stat_calls, acl_calls, open_calls, drift_field;
ssize_t cm_policy_pread(int fd, void *b, size_t n, off_t offset)
{
#ifdef CM_POLICY_BRIDGE
    bridge_pause(1);
#endif
    reads++;
    assert(n <= 4096);
    if (fault == EXHAUST || (fault == INTERRUPT && reads == 1))
    {
        errno = EINTR;
        return -1;
    }
    if (fault == READ_ERROR)
    {
        errno = EIO;
        return -1;
    }
    if (fault == EARLY)
    {
        return 0;
    }
    if (fault == OVERSIZE)
    {
        return (ssize_t)n + 1;
    }
    if (fault == SHORT && n > 1)
    {
        n = 1;
    }
    ssize_t result = pread(fd, b, n, offset);
    if (result == 0)
    {
        pass++;
        if (fault == EXTRA)
        {
            return 1;
        }
    }
    if (fault == DIFFERENT && pass && result > 0)
    {
        ((unsigned char *)b)[0] ^= 1;
    }
    return result;
}
int cm_policy_fstat(int fd, struct stat *s)
{
    if (fault == STAT_ERROR && (!fault_at || ++stat_calls == fault_at))
    {
        errno = EIO;
        return -1;
    }
    int result = fstat(fd, s);
    if (!result && fault == TYPE_MASK && S_ISREG(s->st_mode))
    {
        s->st_mode = S_IFIFO | 0600;
    }
    if (!result && fault == STAT_DRIFT && ++stat_calls == fault_at)
    {
        if (drift_field == 0)
        {
            s->st_ino ^= 1;
        }
        else if (drift_field == 1)
        {
            s->st_mode ^= 0040;
        }
        else if (drift_field == 2)
        {
            s->st_uid ^= 1;
        }
        else
        {
            s->st_mtim.tv_nsec = (s->st_mtim.tv_nsec + 1) % 1000000000;
        }
    }
    if (!result && fault == ABI_VECTOR)
    {
        int directory = S_ISDIR(s->st_mode);
        s->st_dev = makedev(0x1234, 0x56789);
        s->st_ino = directory ? UINT64_C(0x0123456789abcdef) : UINT64_C(0xfedcba9876543210);
        s->st_uid = UINT32_C(0x89abcdef);
        s->st_gid = UINT32_C(0x10203040);
        s->st_mode = directory ? S_IFDIR | 0750 : S_IFREG | 0640;
        s->st_nlink = directory ? 2 : 1;
        s->st_ctim.tv_sec = -2;
        s->st_mtim.tv_sec = INT64_C(0x0102030405060708);
        s->st_ctim.tv_nsec = 123456789;
        s->st_mtim.tv_nsec = 987654321;
    }
#ifdef CM_POLICY_BRIDGE
    if (!result && bridge_root)
    {
        s->st_uid = 0; /* SYNTHETIC root facts: test object only, never qualification. */
    }
#endif
    return result;
}
int cm_policy_fcntl(int fd, int op, ...)
{
    if (fault == FLAGS_ERROR)
    {
        errno = EBADF;
        return -1;
    }
    return fcntl(fd, op);
}
long cm_policy_openat2(long number, int fd, const char *path, const struct open_how *how, size_t size)
{
    assert(number == SYS_openat2 && strcmp(path, "policy.json") == 0);
    assert(how->flags == (O_PATH | O_CLOEXEC | O_NOFOLLOW));
    assert(how->resolve == (RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS | RESOLVE_NO_MAGICLINKS));
    if (fault == OPEN_ERROR && (!fault_at || ++open_calls == fault_at))
    {
        errno = ENOSYS;
        return -1;
    }
    if (fault == BARRIER && calls++ == 1)
    {
        char signal = 'x';
        assert(write(barrier_out, &signal, 1) == 1);
        assert(read(barrier_in, &signal, 1) == 1);
    }
    return syscall(number, fd, path, how, size);
}
ssize_t cm_policy_xattr(int fd, const char *name, void *b, size_t n)
{
    assert(n == 4096);
    assert(strcmp(name, "system.posix_acl_access") == 0 || strcmp(name, "system.posix_acl_default") == 0);
    if (fault == ACL_INTERRUPTED)
    {
        errno = EINTR;
        return -1;
    }
    if (fault == ACL_ZERO && (!fault_at || ++acl_calls == fault_at))
    {
        return 0;
    }
    if ((fault == ACL_RANGE || fault == ACL_DENIED) && (!fault_at || ++acl_calls == fault_at))
    {
        errno = fault == ACL_RANGE ? ERANGE : EACCES;
        return -1;
    }
    return fgetxattr(fd, name, b, n);
}
void *cm_policy_calloc(size_t n, size_t size)
{
    return fault == ALLOC_ERROR ? NULL : calloc(n, size);
}
int cm_policy_close(int fd)
{
    closes++;
    int result = close(fd);
    if (fault == CLOSE_ERROR)
    {
        errno = EINTR;
        return -1; /* Linux closed it: never retry. */
    }
    return result;
}

#ifndef CM_POLICY_BRIDGE
static unsigned char oracle[16384];
static void fixture(size_t length)
{
    int fd = open("policy.json", O_CREAT | O_TRUNC | O_WRONLY | O_CLOEXEC, 0600);
    assert(fd >= 0);
    for (size_t i = 0; i < sizeof(oracle); i++)
    {
        oracle[i] = (unsigned char)(i * 31);
    }
    if (length <= sizeof(oracle))
    {
        assert(write(fd, oracle, length) == (ssize_t)length);
    }
    else
    {
        assert(ftruncate(fd, (off_t)length) == 0);
    }
    assert(close(fd) == 0);
}
static int take(cm_policy_snapshot **s)
{
    int32_t p = open(".", O_RDONLY | O_DIRECTORY | O_CLOEXEC);
    int32_t f = open("policy.json", O_RDONLY | O_CLOEXEC | O_NONBLOCK);
    assert(p >= 0 && f >= 0);
    int status = cm_policy_snapshot_v1_take(&p, &f, s);
    assert(p == -1 && f == -1);
    return status;
}
static void fdset(unsigned char *set)
{
    memset(set, 0, 65536);
    DIR *directory = opendir("/proc/self/fd");
    assert(directory);
    int own = dirfd(directory);
    struct dirent *entry;
    while ((entry = readdir(directory)) != NULL)
    {
        char *end;
        long fd = strtol(entry->d_name, &end, 10);
        if (*end == 0 && fd != own)
        {
            assert(fd >= 0 && fd < 65536);
            set[fd] = 1;
        }
    }
    closedir(directory);
}
static uint64_t get_le(const uint8_t *b, int width)
{
    uint64_t value = 0;
    for (int i = width - 1; i >= 0; i--)
    {
        value = (value << 8) | b[i];
    }
    return value;
}
static void assert_tuple(const uint8_t *b, const struct stat *st)
{
    assert(get_le(b, 4) == major(st->st_dev));
    assert(get_le(b + 4, 4) == minor(st->st_dev));
    assert(get_le(b + 8, 8) == st->st_ino);
    assert(get_le(b + 16, 4) == st->st_uid);
    assert(get_le(b + 20, 4) == st->st_gid);
    assert(get_le(b + 24, 4) == st->st_mode);
    assert(get_le(b + 28, 4) == st->st_nlink);
    assert(get_le(b + 32, 8) == (uint64_t)st->st_ctim.tv_sec);
    assert(get_le(b + 40, 8) == (uint64_t)st->st_mtim.tv_sec);
    assert(get_le(b + 48, 4) == (uint64_t)st->st_ctim.tv_nsec);
    assert(get_le(b + 52, 4) == (uint64_t)st->st_mtim.tv_nsec);
}
static void positive(size_t length)
{
    fixture(length);
    cm_policy_snapshot *s = NULL;
    assert(take(&s) == 0 && s);
    uint8_t bytes[16384], facts[128];
    uint32_t written = 999;
    assert(cm_policy_snapshot_v1_copy(s, bytes, (uint32_t)length - 1, &written) == 1 && written == 0);
    assert(cm_policy_snapshot_v1_copy(s, bytes, (uint32_t)length, &written) == 0 && written == length);
    assert(memcmp(bytes, oracle, length) == 0);
    assert(cm_policy_snapshot_v1_facts(s, facts, 127, &written) == 1 && written == 0);
    assert(cm_policy_snapshot_v1_facts(s, facts, 128, &written) == 0 && written == 128);
    assert(facts[0] == 1 && facts[4] == 128 && facts[124] == 0);
    assert(((uint32_t)facts[120] | ((uint32_t)facts[121] << 8)) == length);
    struct stat st;
    assert(stat("policy.json", &st) == 0);
    assert_tuple(facts + 64, &st);
    assert(stat(".", &st) == 0);
    assert_tuple(facts + 8, &st);
    cm_policy_snapshot_v1_destroy(&s);
    assert(s == NULL);
    cm_policy_snapshot_v1_destroy(&s);
}
#ifdef CM_POLICY_CONTROLS
/* Fixed independent bytes cover signed time, high-bit IDs, widths and endianness.
   Only the test fstat object supplies these synthetic values, never root trust. */
static void fixed_facts_vector(void)
{
    static const uint8_t expected[128] = {
        0x01, 0x00, 0x00, 0x00, 0x80, 0x00, 0x00, 0x00, 0x34, 0x12, 0x00, 0x00, 0x89, 0x67, 0x05, 0x00,
        0xef, 0xcd, 0xab, 0x89, 0x67, 0x45, 0x23, 0x01, 0xef, 0xcd, 0xab, 0x89, 0x40, 0x30, 0x20, 0x10,
        0xe8, 0x41, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0xfe, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff,
        0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01, 0x15, 0xcd, 0x5b, 0x07, 0xb1, 0x68, 0xde, 0x3a,
        0x34, 0x12, 0x00, 0x00, 0x89, 0x67, 0x05, 0x00, 0x10, 0x32, 0x54, 0x76, 0x98, 0xba, 0xdc, 0xfe,
        0xef, 0xcd, 0xab, 0x89, 0x40, 0x30, 0x20, 0x10, 0xa0, 0x81, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
        0xfe, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,
        0x15, 0xcd, 0x5b, 0x07, 0xb1, 0x68, 0xde, 0x3a, 0x09, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    };
    fixture(9);
    fault = ABI_VECTOR;
    cm_policy_snapshot *s = NULL;
    assert(take(&s) == 0 && s);
    uint8_t actual[128];
    uint32_t written = 0;
    assert(cm_policy_snapshot_v1_facts(s, actual, sizeof(actual), &written) == 0);
    assert(written == sizeof(actual) && memcmp(actual, expected, sizeof(actual)) == 0);
    cm_policy_snapshot_v1_destroy(&s);
    fault = NONE;
    puts("PASS: independent 128-byte native facts vector (synthetic metadata)");
}
#endif
int main(void)
{
    char path[] = "/tmp/cm-policy-XXXXXX";
    assert(mkdtemp(path));
    int previous = open(".", O_RDONLY | O_DIRECTORY | O_CLOEXEC);
    assert(previous >= 0 && chdir(path) == 0);
    int sentinel = open("sentinel", O_CREAT | O_RDWR | O_CLOEXEC, 0600);
    assert(sentinel >= 0 && write(sentinel, "untouched", 9) == 9);
    int high = fcntl(sentinel, F_DUPFD_CLOEXEC, 1024);
    assert(high >= 1024);
    unsigned char before[65536], after[65536];
    fdset(before);
    positive(1);
    positive(4096);
    positive(16384);
    cm_policy_snapshot *s = NULL;
    size_t invalid_sizes[] = { 0, 16385, 1073741824 };
    for (size_t i = 0; i < 3; i++)
    {
        fixture(invalid_sizes[i]);
        assert(take(&s) == 1 && !s);
    }
    fixture(9);
    assert(link("policy.json", "alias") == 0);
    assert(take(&s) == 1 && !s);
    assert(unlink("alias") == 0);
    assert(take(&s) == 0);
    int writer = open("policy.json", O_WRONLY | O_CLOEXEC);
    assert(writer >= 0 && pwrite(writer, "different", 9, 0) == 9);
    assert(close(writer) == 0);
    assert(cm_policy_snapshot_v1_check(s) == 3);
    fixture(9);
    assert(cm_policy_snapshot_v1_check(s) == 3);
    cm_policy_snapshot_v1_destroy(&s);
    int32_t p = open(".", O_RDONLY | O_DIRECTORY | O_CLOEXEC), f = p;
    assert(cm_policy_snapshot_v1_take(&p, &f, &s) == 1 && p == -1 && f == -1 && !s);
    assert(unlink("policy.json") == 0 && mkfifo("policy.json", 0600) == 0);
    reads = 0;
    assert(take(&s) == 1 && !s && reads == 0);
    assert(unlink("policy.json") == 0);
    fixture(9);
    /* Wrong flags/types and identity never authorize a readable reopen. */
    int32_t bad_parent = open(".", O_RDONLY | O_DIRECTORY | O_CLOEXEC);
    int32_t bad_file = open("policy.json", O_RDWR | O_CLOEXEC);
    assert(cm_policy_snapshot_v1_take(&bad_parent, &bad_file, &s) == 1 && !s);
    bad_parent = open(".", O_RDONLY | O_DIRECTORY | O_CLOEXEC);
    bad_file = open("policy.json", O_PATH | O_CLOEXEC);
    assert(cm_policy_snapshot_v1_take(&bad_parent, &bad_file, &s) == 1 && !s);
    bad_parent = open(".", O_RDONLY | O_DIRECTORY | O_CLOEXEC);
    bad_file = open("policy.json", O_RDONLY);
    assert(cm_policy_snapshot_v1_take(&bad_parent, &bad_file, &s) == 1 && !s);
    bad_parent = open("policy.json", O_RDONLY | O_CLOEXEC);
    bad_file = open(".", O_RDONLY | O_DIRECTORY | O_CLOEXEC);
    assert(cm_policy_snapshot_v1_take(&bad_parent, &bad_file, &s) == 1 && !s);
    bad_parent = -1;
    bad_file = -1;
    assert(cm_policy_snapshot_v1_take(&bad_parent, &bad_file, &s) == 1 && !s);
    bad_parent = open(".", O_RDONLY | O_DIRECTORY | O_CLOEXEC);
    bad_file = open("policy.json", O_RDONLY | O_CLOEXEC);
    assert(close(bad_file) == 0);
    assert(cm_policy_snapshot_v1_take(&bad_parent, &bad_file, &s) == 1 && !s);
    bad_parent = open(".", O_RDONLY | O_DIRECTORY | O_CLOEXEC);
    assert(cm_policy_snapshot_v1_take(&bad_parent, &bad_parent, &s) == 1 && bad_parent >= 0);
    assert(close(bad_parent) == 0);
    /* Aliased output/input and partially overlapping slots refuse before transfer. */
    union { cm_policy_snapshot *output; int32_t inputs[2]; } overlap;
    overlap.inputs[0] = 123;
    bad_file = open("policy.json", O_RDONLY | O_CLOEXEC);
    assert(cm_policy_snapshot_v1_take(&overlap.inputs[0], &bad_file, &overlap.output) == 1);
    assert(overlap.inputs[0] == 123 && fcntl(bad_file, F_GETFD) >= 0);
    assert(close(bad_file) == 0);
    bad_parent = open(".", O_RDONLY | O_DIRECTORY | O_CLOEXEC);
    assert(cm_policy_snapshot_v1_take(&bad_parent, &overlap.inputs[0], &overlap.output) == 1);
    assert(overlap.inputs[0] == 123 && fcntl(bad_parent, F_GETFD) >= 0);
    assert(close(bad_parent) == 0);
    _Alignas(int32_t) unsigned char slots[8];
    memset(slots, 0x5a, sizeof(slots));
    assert(cm_policy_snapshot_v1_take((int32_t *)slots, (int32_t *)(slots + 1), &s) == 1);
    for (size_t i = 0; i < sizeof(slots); i++)
    {
        assert(slots[i] == 0x5a);
    }
    bad_parent = open(".", O_RDONLY | O_DIRECTORY | O_CLOEXEC);
    bad_file = open("policy.json", O_WRONLY | O_CLOEXEC);
    assert(cm_policy_snapshot_v1_take(&bad_parent, &bad_file, &s) == 1 && !s);
    bad_file = open("policy.json", O_RDONLY | O_DIRECT | O_CLOEXEC);
    if (bad_file >= 0)
    {
        bad_parent = open(".", O_RDONLY | O_DIRECTORY | O_CLOEXEC);
        assert(cm_policy_snapshot_v1_take(&bad_parent, &bad_file, &s) == 1 && !s);
        puts("O_DIRECT supported and refused");
    }
    else
    {
        puts("NOT RUN: backend does not provide an O_DIRECT descriptor");
    }
    assert(chmod("policy.json", 0000) == 0);
    errno = 0;
    assert(open("policy.json", O_RDONLY | O_CLOEXEC) == -1 && errno == EACCES);
    assert(chmod("policy.json", 0600) == 0);
    int pair[2];
    assert(socketpair(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0, pair) == 0);
    bad_parent = open(".", O_RDONLY | O_DIRECTORY | O_CLOEXEC);
    bad_file = pair[0];
    reads = 0;
    assert(cm_policy_snapshot_v1_take(&bad_parent, &bad_file, &s) == 1 && !s && reads == 0);
    close(pair[1]);
    assert(rename("policy.json", "old") == 0 && symlink("old", "policy.json") == 0);
    assert(take(&s) != 0 && !s);
    assert(unlink("policy.json") == 0 && rename("old", "policy.json") == 0);
    /* A valid extended access ACL encoded directly, no external setfacl tool. */
    unsigned char acl[] = { 2,0,0,0, 1,0,6,0,255,255,255,255,
        2,0,4,0, 254,255,0,0, 4,0,0,0,255,255,255,255,
        16,0,4,0,255,255,255,255, 32,0,0,0,255,255,255,255 };
    if (setxattr("policy.json", "system.posix_acl_access", acl, sizeof(acl), 0) == 0)
    {
        assert(take(&s) == 4 && !s);
        assert(removexattr("policy.json", "system.posix_acl_access") == 0);
        puts("real file access ACL refused");
    }
    else
    {
        fprintf(stderr, "NOT RUN: real ACL creation unavailable; raw collection remains unqualified\n");
    }
    acl[6] = 7; /* parent owner must retain search */
    const char *parent_acl_names[] = { "system.posix_acl_access", "system.posix_acl_default" };
    for (size_t name = 0; name < 2; name++)
    {
        if (setxattr(".", parent_acl_names[name], acl, sizeof(acl), 0) == 0)
        {
            assert(take(&s) == 4 && !s);
            assert(removexattr(".", parent_acl_names[name]) == 0);
            printf("real parent ACL %zu refused\n", name);
        }
        else
        {
            puts("NOT RUN: parent ACL backend unavailable");
        }
    }
#ifdef CM_POLICY_CONTROLS
    fixed_facts_vector();
    int faults[] = { EARLY, EXTRA, DIFFERENT, OVERSIZE, ACL_ZERO, ACL_RANGE, ACL_DENIED,
        STAT_ERROR, FLAGS_ERROR, OPEN_ERROR, ALLOC_ERROR, EXHAUST, TYPE_MASK, READ_ERROR, ACL_INTERRUPTED };
    int expected[] = { 5, 3, 3, 1, 4, 2, 2, 2, 1, 2, 2, 6, 1, 2, 2 };
    _Static_assert(sizeof(faults) / sizeof(faults[0]) == sizeof(expected) / sizeof(expected[0]),
        "Every refusal fault must have an expected result");
    for (size_t i = 0; i < sizeof(faults) / sizeof(faults[0]); i++)
    {
        fault = faults[i];
        reads = pass = calls = closes = 0;
        int actual = take(&s);
        if (fault == DIFFERENT)
        {
            assert(actual == 3);
        }
        if (fault == TYPE_MASK)
        {
            assert(actual == 1);
        }
        assert(actual == expected[i] && !s);
        if (fault == EXHAUST)
        {
            assert(reads == 32768);
        }
        printf("control %d refused %d, reads %d closes %d\n", fault, expected[i], reads, closes);
    }
    /* Error at every normally reached syscall checkpoint, not just the first. */
    int stage_faults[] = { STAT_ERROR, ACL_DENIED, OPEN_ERROR, ACL_ZERO };
    for (size_t kind = 0; kind < 4; kind++)
    {
        int reached_success = 0;
        for (int stage = 1; stage < 256; stage++)
        {
            fault = stage_faults[kind];
            fault_at = stage;
            stat_calls = acl_calls = open_calls = 0;
            int status = take(&s);
            if (status == 0)
            {
                cm_policy_snapshot_v1_destroy(&s);
                reached_success = 1;
                break;
            }
            assert(status == (fault == ACL_ZERO ? 4 : 2) && !s);
            printf("checkpoint fault %d occurrence %d refused\n", fault, stage);
        }
        assert(reached_success);
    }
    /* Change one sampled identity/permission/time field at every reachable
       fstat occurrence, including each ACL sweep's metadata checkpoint. */
    for (drift_field = 0; drift_field < 4; drift_field++)
    {
        int reached_success = 0;
        for (int stage = 1; stage < 256; stage++)
        {
            fault = STAT_DRIFT;
            fault_at = stage;
            stat_calls = 0;
            int status = take(&s);
            if (status == 0)
            {
                cm_policy_snapshot_v1_destroy(&s);
                reached_success = 1;
                break;
            }
            assert(status == 3 && !s);
            printf("metadata drift field %d occurrence %d refused\n", drift_field, stage);
        }
        assert(reached_success);
    }
    fault_at = 0;
    fault = SHORT;
    reads = pass = 0;
    positive(4096);
    fault = INTERRUPT;
    reads = pass = 0;
    positive(9);
    fault = CLOSE_ERROR;
    positive(9);
    fault = NONE;
    /* Actor replaces entry at a precisely acknowledged identity checkpoint. */
    for (int change = 0; change < 9; change++)
    {
        fixture(9);
        int ready[2], resume[2];
        assert(pipe(ready) == 0 && pipe(resume) == 0);
        pid_t actor = fork();
        assert(actor >= 0);
        if (!actor)
        {
            close(ready[1]);
            close(resume[0]);
            char signal;
            assert(read(ready[0], &signal, 1) == 1);
            if (change <= 2)
            {
                assert(rename("policy.json", "old") == 0);
                if (change == 0)
                {
                    assert(symlink("old", "policy.json") == 0);
                }
                else if (change == 1)
                {
                    assert(mkfifo("policy.json", 0600) == 0);
                }
            }
            else if (change == 3)
            {
                assert(chmod("policy.json", 0640) == 0);
            }
            else if (change == 4)
            {
                assert(chmod(".", 0750) == 0);
            }
            else if (change == 5)
            {
                assert(link("policy.json", "alias") == 0);
            }
            else
            {
                int writer = open("policy.json", O_WRONLY | O_CLOEXEC);
                assert(writer >= 0);
                if (change == 6)
                {
                    assert(ftruncate(writer, 0) == 0);
                }
                else if (change == 7)
                {
                    assert(ftruncate(writer, 10) == 0);
                }
                else
                {
                    assert(pwrite(writer, "different", 9, 0) == 9);
                }
                assert(close(writer) == 0);
            }
            assert(write(resume[1], "x", 1) == 1);
            _exit(0);
        }
        close(ready[0]);
        close(resume[1]);
        barrier_out = ready[1];
        barrier_in = resume[0];
        fault = BARRIER;
        calls = 0;
        assert(take(&s) != 0 && !s);
        int child_status;
        assert(waitpid(actor, &child_status, 0) == actor && WIFEXITED(child_status) && WEXITSTATUS(child_status) == 0);
        close(ready[1]);
        close(resume[0]);
        fault = NONE;
        if (change <= 2)
        {
            if (change < 2)
            {
                assert(unlink("policy.json") == 0);
            }
            assert(rename("old", "policy.json") == 0);
        }
        if (change == 4)
        {
            assert(chmod(".", 0700) == 0);
        }
        if (change == 5)
        {
            assert(unlink("alias") == 0);
        }
        printf("barrier change %d refused, actor joined\n", change);
    }
#endif
    for (int i = 0; i < 100; i++)
    {
        positive(9);
    }
    fdset(after);
    assert(memcmp(before, after, sizeof(before)) == 0);
    char unchanged[9];
    assert(pread(sentinel, unchanged, 9, 0) == 9 && memcmp(unchanged, "untouched", 9) == 0);
    assert(close(sentinel) == 0 && close(high) == 0 && unlink("sentinel") == 0);
    assert(unlink("policy.json") == 0);
    assert(fchdir(previous) == 0 && close(previous) == 0 && rmdir(path) == 0);
    puts("PASS: raw current-user UNQUALIFIED policy snapshots; no root qualification");
    return 0;
}

#else
static void bridge_pause(int stage)
{
    pthread_mutex_lock(&bridge_mutex);
    if (bridge_block == stage)
    {
        bridge_entered = 1;
        pthread_cond_broadcast(&bridge_condition);
        while (!bridge_released)
        {
            pthread_cond_wait(&bridge_condition, &bridge_mutex);
        }
        bridge_block = 0;
    }
    pthread_mutex_unlock(&bridge_mutex);
}
int32_t cm_policy_inner_take(int32_t *, int32_t *, cm_policy_snapshot **);
int32_t cm_policy_inner_copy(cm_policy_snapshot *, uint8_t *, uint32_t, uint32_t *);
int32_t cm_policy_inner_facts(cm_policy_snapshot *, uint8_t *, uint32_t, uint32_t *);
int32_t cm_policy_inner_check(cm_policy_snapshot *);
void cm_policy_inner_destroy(cm_policy_snapshot **);
int32_t cm_policy_snapshot_v1_take(int32_t *p, int32_t *f, cm_policy_snapshot **s)
{
    return cm_policy_inner_take(p, f, s);
}
int32_t cm_policy_snapshot_v1_facts(cm_policy_snapshot *s, uint8_t *b, uint32_t n, uint32_t *w)
{
    bridge_pause(2);
    int32_t status = cm_policy_inner_facts(s, b, n, w);
    if (!status && fault == 101)
    {
        *w = 127;
    }
    if (!status && fault == 102)
    {
        b[0] = 2;
    }
    return status;
}
int32_t cm_policy_snapshot_v1_copy(cm_policy_snapshot *s, uint8_t *b, uint32_t n, uint32_t *w)
{
    bridge_pause(3);
    int32_t status = cm_policy_inner_copy(s, b, n, w);
    if (!status && fault == 103)
    {
        *w = n - 1;
    }
    if (!status && fault == 105)
    {
        return 2;
    }
    if (!status && fault == 106)
    {
        memset(b, 0, n);
    }
    return status;
}
int32_t cm_policy_snapshot_v1_check(cm_policy_snapshot *s)
{
    bridge_pause(4);
    return fault == 104 ? 3 : cm_policy_inner_check(s);
}
void cm_policy_snapshot_v1_destroy(cm_policy_snapshot **s)
{
    cm_policy_inner_destroy(s);
}
/* This object is only linked into the isolated synthetic managed test shim. */
int cm_policy_test_prepare(const uint8_t *bytes, uint32_t length, int32_t *parent, int32_t *file)
{
    if (getuid() == 0)
    {
        return -1; /* Ordinary-user runner prerequisite, never genuine root testing. */
    }
    strcpy(bridge_path, "/tmp/cm-policy-bridge-XXXXXX");
    if (!mkdtemp(bridge_path))
    {
        return -1;
    }
    *parent = open(bridge_path, O_RDONLY | O_DIRECTORY | O_CLOEXEC);
    if (*parent < 0 || fchmod(*parent, 0755))
    {
        return -1;
    }
    int writer = openat(*parent, "policy.json", O_CREAT | O_WRONLY | O_CLOEXEC, 0644);
    if (writer < 0 || fchmod(writer, 0644) || write(writer, bytes, length) != length)
    {
        return -1;
    }
    close(writer);
    *file = openat(*parent, "policy.json", O_RDONLY | O_CLOEXEC);
    return *file >= 0 ? 0 : -1;
}
void cm_policy_test_configure(int root, int mode, int block)
{
    bridge_root = root;
    fault = mode;
    reads = pass = calls = closes = 0;
    pthread_mutex_lock(&bridge_mutex);
    bridge_block = block;
    bridge_entered = bridge_released = 0;
    pthread_mutex_unlock(&bridge_mutex);
}
void cm_policy_test_wait(void)
{
    pthread_mutex_lock(&bridge_mutex);
    while (!bridge_entered)
    {
        pthread_cond_wait(&bridge_condition, &bridge_mutex);
    }
    pthread_mutex_unlock(&bridge_mutex);
}
void cm_policy_test_release(void)
{
    pthread_mutex_lock(&bridge_mutex);
    bridge_released = 1;
    pthread_cond_broadcast(&bridge_condition);
    pthread_mutex_unlock(&bridge_mutex);
}
int cm_policy_test_close_count(void)
{
    return closes;
}
void cm_policy_test_cleanup(void)
{
    char leaf[160];
    snprintf(leaf, sizeof(leaf), "%s/policy.json", bridge_path);
    assert(unlink(leaf) == 0);
    assert(rmdir(bridge_path) == 0);
}
#endif

#ifdef CM_POLICY_BRIDGE
int cm_policy_test_duplicate(int32_t parent, int32_t file, int32_t *p, int32_t *f)
{
    *p = fcntl(parent, F_DUPFD_CLOEXEC, 0);
    *f = fcntl(file, F_DUPFD_CLOEXEC, 0);
    return *p >= 0 && *f >= 0 ? 0 : -1;
}
void cm_policy_test_close_inputs(int32_t parent, int32_t file)
{
    close(parent);
    close(file);
}
int cm_policy_test_fdset(uint8_t *set, uint32_t capacity)
{
    if (capacity != 65536)
    {
        return -1;
    }
    memset(set, 0, capacity);
    DIR *directory = opendir("/proc/self/fd");
    if (!directory)
    {
        return -1;
    }
    int own = dirfd(directory);
    struct dirent *entry;
    while ((entry = readdir(directory)) != NULL)
    {
        char *end;
        long fd = strtol(entry->d_name, &end, 10);
        if (*end == 0 && fd != own)
        {
            assert(fd >= 0 && (uint64_t)fd < capacity);
            set[fd] = 1;
        }
    }
    return closedir(directory);
}
#endif
