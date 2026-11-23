#define _GNU_SOURCE
#include "metadata.h"
#include <errno.h>
#include <fcntl.h>
#include <linux/openat2.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/syscall.h>
#include <sys/xattr.h>
#include <unistd.h>
#ifdef CM_METADATA_TEST
#include "syscall_controls.h"
static int acl_error;
static int open_error;
static int calls;
static int positive_call;
static int error_call;
static int chmod_call;
static int mutate_fd = -1;
ssize_t cm_test_fgetxattr(int fd, const char *name, void *buffer, size_t size)
{
    ++calls;
    if ((strcmp(name, "system.posix_acl_access") != 0 &&
        strcmp(name, "system.posix_acl_default") != 0) || size != 4096)
    {
        abort();
    }
    if (calls == chmod_call && fchmod(mutate_fd, 0700) != 0)
    {
        abort();
    }
    if (calls == positive_call)
    {
        return 0; /* Even a present zero-byte attribute refuses. */
    }
    if (acl_error != 0 && (error_call == 0 || calls == error_call))
    {
        errno = acl_error;
        return -1;
    }
    return fgetxattr(fd, name, buffer, size);
}
int cm_test_openat2(int fd, const char *name, const struct open_how *how)
{
    if (how->flags != (O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW) ||
        how->resolve != (RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS | RESOLVE_NO_MAGICLINKS))
    {
        abort();
    }
    if (open_error != 0)
    {
        errno = open_error;
        return -1;
    }
    return (int)syscall(SYS_openat2, fd, name, how, sizeof(*how));
}
#endif

#define REQUIRE(expression) do \
{ \
    if (!(expression)) \
    { \
        fprintf(stderr, "FAIL line %d: %s (errno=%d)\n", __LINE__, #expression, errno); \
        exit(1); \
    } \
} while (0)

static struct stat snapshot(int fd)
{
    struct stat value;
    REQUIRE(fstat(fd, &value) == 0);
    return value;
}

/* Little-endian Linux POSIX ACL xattr: version + owner/named-user/group/mask/other.
 * Named UID is purely fixture data, no account creation or ownership changes. */
static const unsigned char acl[] =
{
    2,0,0,0,
    1,0,7,0,255,255,255,255,
    2,0,4,0,254,255,0,0,
    4,0,5,0,255,255,255,255,
    16,0,5,0,255,255,255,255,
    32,0,0,0,255,255,255,255
};

static cm_directory *take(int fd)
{
    struct stat expected = snapshot(fd);
    cm_directory *out = NULL;
    int original = fd;
    REQUIRE(cm_directory_take(&fd, &expected, &out).state == CM_FACTS);
    REQUIRE(fd == -1);
    REQUIRE((fcntl(original, F_GETFD) & FD_CLOEXEC) != 0);
    return out;
}

static cm_directory *bound(int root, int target_fd)
{
    int duplicate = fcntl(root, F_DUPFD_CLOEXEC, 0);
    REQUIRE(duplicate >= 0);
    cm_directory *parent = take(duplicate);
    cm_directory *child = NULL;
    struct stat expected = snapshot(target_fd);
    cm_result outcome = cm_directory_open(parent, "target", &expected, &child);
    REQUIRE(outcome.state == CM_FACTS);
    cm_directory_destroy(&parent); /* Child must retain its own parent FD. */
    REQUIRE(parent == NULL);
    return child;
}

int main(void)
{
    char fixture[] = "/tmp/cm-metadata-XXXXXX";
    REQUIRE(mkdtemp(fixture) != NULL);
    int root = open(fixture, O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW);
    REQUIRE(root >= 0);
    REQUIRE(mkdirat(root, "target", 0750) == 0);
    REQUIRE(mkdirat(root, "foreign", 0750) == 0);
    REQUIRE(symlinkat("target", root, "link") == 0);
    REQUIRE(symlinkat("/", root, "escape") == 0);
    int file = openat(root, "sentinel", O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC, 0600);
    REQUIRE(file >= 0);
    const char sentinel[] = "owned fixture sentinel";
    REQUIRE(write(file, sentinel, sizeof(sentinel)) == (ssize_t)sizeof(sentinel));
    close(file);
    int target_fd = openat(root, "target", O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW);
    REQUIRE(target_fd >= 0);
    struct stat target = snapshot(target_fd);
    int path = openat(root, "target", O_PATH | O_DIRECTORY | O_CLOEXEC);
    REQUIRE(path >= 0);
    unsigned char buffer[4096];
    errno = 0;
    REQUIRE(fgetxattr(path, "system.posix_acl_access", buffer, sizeof(buffer)) == -1);
    REQUIRE(errno == EBADF);
    struct stat path_stat = snapshot(path);
    REQUIRE(path_stat.st_ino == target.st_ino && path_stat.st_dev == target.st_dev);
    cm_directory *invalid = NULL;
    REQUIRE(cm_directory_take(&path, &target, &invalid).state == CM_INVALID);
    REQUIRE(path == -1 && invalid == NULL);
    errno = 0;
    REQUIRE(fgetxattr(target_fd, "system.posix_acl_access", buffer, sizeof(buffer)) == -1);
    REQUIRE(errno == ENODATA); /* Raw unqualified negative only. */
    int closed = dup(target_fd);
    REQUIRE(closed >= 0);
    close(closed);
    cm_result outcome = cm_directory_take(&closed, &target, &invalid);
    REQUIRE(outcome.state == CM_UNAVAILABLE && outcome.error == EBADF && closed == -1);
    int foreign = openat(root, "foreign", O_RDONLY | O_DIRECTORY | O_CLOEXEC);
    REQUIRE(foreign >= 0);
    REQUIRE(cm_directory_take(&foreign, &target, &invalid).state == CM_DRIFT);
    file = openat(root, "sentinel", O_RDONLY | O_CLOEXEC);
    REQUIRE(file >= 0);
    struct stat file_stat = snapshot(file);
    REQUIRE(cm_directory_take(&file, &file_stat, &invalid).state == CM_INVALID);
    int no_cloexec = dup(target_fd);
    REQUIRE(no_cloexec >= 0);
    REQUIRE(cm_directory_take(&no_cloexec, &target, &invalid).state == CM_INVALID);
    int duplicate = fcntl(root, F_DUPFD_CLOEXEC, 0);
    REQUIRE(duplicate >= 0);
    cm_directory *parent = take(duplicate);
    const char *bad[] = {"", ".", "..", "../target", "target/..", "/target", "target/", "a\\b", "a b"};
    for (size_t i = 0; i < sizeof(bad) / sizeof(bad[0]); ++i)
    {
        REQUIRE(cm_directory_open(parent, bad[i], &target, &invalid).state == CM_INVALID);
    }
    char long_name[66];
    memset(long_name, 'a', sizeof(long_name));
    long_name[65] = 0;
    REQUIRE(cm_directory_open(parent, long_name, &target, &invalid).state == CM_INVALID);
    REQUIRE(cm_directory_open(parent, "link", &target, &invalid).state == CM_UNAVAILABLE);
    REQUIRE(cm_directory_open(parent, "escape", &target, &invalid).state == CM_UNAVAILABLE);
    REQUIRE(cm_directory_open(parent, "sentinel", &target, &invalid).state == CM_UNAVAILABLE);
    REQUIRE(cm_directory_open(parent, "missing", &target, &invalid).state == CM_UNAVAILABLE);
    REQUIRE(cm_directory_open(parent, "foreign", &target, &invalid).state == CM_DRIFT);
    for (int field = 0; field < 8; ++field)
    {
        struct stat wrong = target;
        switch (field)
        {
            case 0:
                ++wrong.st_dev;
                break;
            case 1:
                ++wrong.st_ino;
                break;
            case 2:
                ++wrong.st_uid;
                break;
            case 3:
                ++wrong.st_gid;
                break;
            case 4:
                wrong.st_mode ^= S_IWOTH;
                break;
            case 5:
                ++wrong.st_nlink;
                break;
            case 6:
                wrong.st_ctim.tv_nsec ^= 1;
                break;
            default:
                wrong.st_mtim.tv_nsec ^= 1;
                break;
        }
        REQUIRE(cm_directory_open(parent, "target", &wrong, &invalid).state == CM_DRIFT);
    }
    REQUIRE(cm_directory_check(parent).state == CM_INVALID); /* Anchor lacks binding. */
    cm_directory_destroy(&parent);
    REQUIRE(fcntl(duplicate, F_GETFD) == -1 && errno == EBADF);
    int reused = open("/dev/null", O_RDONLY | O_CLOEXEC);
    REQUIRE(reused >= 0);
    cm_directory_destroy(&parent);
    REQUIRE(fcntl(reused, F_GETFD) >= 0); /* No repeated disposal/reuse close. */
    close(reused);
    cm_directory *child = bound(root, target_fd);
    REQUIRE(cm_directory_check(child).state == CM_NO_DATA_UNQUALIFIED);
    struct stat actual;
    REQUIRE(cm_directory_snapshot(child, &actual).state == CM_FACTS);
    REQUIRE(actual.st_ino == target.st_ino && actual.st_uid == getuid());
#ifdef CM_METADATA_TEST
    const int errors[] = {EBADF, EACCES, EPERM, ERANGE, EOPNOTSUPP, EINTR, EIO, ENOMEM};
    for (size_t i = 0; i < sizeof(errors) / sizeof(errors[0]); ++i)
    {
        for (int position = 1; position <= 12; ++position)
        {
            calls = 0;
            acl_error = errors[i];
            error_call = position;
            outcome = cm_directory_check(child);
            REQUIRE(outcome.state == CM_UNAVAILABLE && outcome.error == errors[i]);
            REQUIRE(calls == position); /* No fallback/retry, including interruption. */
        }
    }
    acl_error = 0;
    error_call = 0;
    for (int position = 1; position <= 12; ++position)
    {
        calls = 0;
        positive_call = position;
        REQUIRE(cm_directory_check(child).state == CM_ACL_PRESENT);
        REQUIRE(calls == position);
    }
    positive_call = 0;
    const int open_errors[] = {ENOSYS, EPERM, EACCES, EINTR, EINVAL};
    for (size_t i = 0; i < sizeof(open_errors) / sizeof(open_errors[0]); ++i)
    {
        open_error = open_errors[i];
        outcome = cm_directory_check(child);
        REQUIRE(outcome.state == CM_UNAVAILABLE && outcome.error == open_error);
        int root_copy = fcntl(root, F_DUPFD_CLOEXEC, 0);
        REQUIRE(root_copy >= 0);
        parent = take(root_copy);
        outcome = cm_directory_open(parent, "target", &target, &invalid);
        REQUIRE(outcome.state == CM_UNAVAILABLE && outcome.error == open_error);
        cm_directory_destroy(&parent);
    }
    open_error = 0;
    calls = 0;
    chmod_call = 5;
    mutate_fd = target_fd;
    REQUIRE(cm_directory_check(child).state == CM_DRIFT);
    REQUIRE(calls == 6); /* default query completes, then snapshot rejects. */
    chmod_call = 0;
    REQUIRE(fchmod(target_fd, 0750) == 0);
    puts("PASS injected syscall controls (separate test build; not kernel error proof)");
#endif
    cm_directory_destroy(&child);
    /* Sequential resource test without /proc enumeration: lowest free FD must
     * be unchanged after repeated acquisition/check/disposal, including refusal. */
    int lowest = fcntl(root, F_DUPFD_CLOEXEC, 0);
    REQUIRE(lowest >= 0);
    close(lowest);
    for (int iteration = 0; iteration < 128; ++iteration)
    {
        child = bound(root, target_fd);
        REQUIRE(cm_directory_check(child).state == CM_NO_DATA_UNQUALIFIED);
        cm_directory_destroy(&child);
    }
    int after = fcntl(root, F_DUPFD_CLOEXEC, 0);
    REQUIRE(after == lowest);
    close(after);
    /* Actual kernel ACL positive controls, no libacl/setfacl dependency. */
    const char *names[] = {"system.posix_acl_access", "system.posix_acl_default"};
    for (size_t i = 0; i < sizeof(names) / sizeof(names[0]); ++i)
    {
        child = bound(root, target_fd);
        REQUIRE(fsetxattr(target_fd, names[i], acl, sizeof(acl), 0) == 0);
        REQUIRE(cm_directory_check(child).state == CM_DRIFT);
        cm_directory_destroy(&child);
        child = bound(root, target_fd);
        REQUIRE(cm_directory_check(child).state == CM_ACL_PRESENT);
        cm_directory_destroy(&child);
        REQUIRE(fremovexattr(target_fd, names[i]) == 0);
    }
    /* Parent ACL must also refuse, even when child has no ACL. */
    REQUIRE(fsetxattr(root, names[1], acl, sizeof(acl), 0) == 0);
    child = bound(root, target_fd);
    REQUIRE(cm_directory_check(child).state == CM_ACL_PRESENT);
    cm_directory_destroy(&child);
    REQUIRE(fremovexattr(root, names[1]) == 0);
    if (geteuid() != 0)
    {
        REQUIRE(fchmod(target_fd, 0000) == 0);
        int root_copy = fcntl(root, F_DUPFD_CLOEXEC, 0);
        REQUIRE(root_copy >= 0);
        parent = take(root_copy);
        struct stat denied = snapshot(target_fd);
        outcome = cm_directory_open(parent, "target", &denied, &invalid);
        REQUIRE(outcome.state == CM_UNAVAILABLE && outcome.error == EACCES);
        cm_directory_destroy(&parent);
        REQUIRE(fchmod(target_fd, 0750) == 0);
    }
    else
    {
        puts("NOT RUN real DAC denial: root bypasses ordinary directory DAC");
        return 77;
    }
    child = bound(root, target_fd);
    REQUIRE(fchmod(target_fd, 0700) == 0);
    REQUIRE(cm_directory_check(child).state == CM_DRIFT);
    REQUIRE(cm_directory_snapshot(child, &actual).state == CM_DRIFT);
    cm_directory_destroy(&child);
    REQUIRE(fchmod(target_fd, 0750) == 0);
    child = bound(root, target_fd);
    REQUIRE(renameat(root, "target", root, "old") == 0);
    REQUIRE(mkdirat(root, "target", 0750) == 0);
    REQUIRE(cm_directory_check(child).state == CM_DRIFT);
    struct stat retained = snapshot(target_fd);
    REQUIRE(retained.st_ino == actual.st_ino); /* Actual retained inode, no adoption. */
    cm_directory_destroy(&child);
    /* Test harness reads ONLY its owned sentinel to verify absence of effects. */
    file = openat(root, "sentinel", O_RDONLY | O_CLOEXEC);
    REQUIRE(file >= 0);
    char bytes[sizeof(sentinel)];
    REQUIRE(read(file, bytes, sizeof(bytes)) == (ssize_t)sizeof(bytes));
    REQUIRE(memcmp(bytes, sentinel, sizeof(bytes)) == 0);
    close(file);
    close(target_fd);
    REQUIRE(unlinkat(root, "target", AT_REMOVEDIR) == 0);
    REQUIRE(unlinkat(root, "old", AT_REMOVEDIR) == 0);
    REQUIRE(unlinkat(root, "foreign", AT_REMOVEDIR) == 0);
    REQUIRE(unlinkat(root, "link", 0) == 0);
    REQUIRE(unlinkat(root, "escape", 0) == 0);
    REQUIRE(unlinkat(root, "sentinel", 0) == 0);
    close(root);
    REQUIRE(rmdir(fixture) == 0);
    puts("PASS Linux kernel directory/ACL/ownership/drift fixtures; ENODATA remains unqualified");
    return 0;
}
