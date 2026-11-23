#define _GNU_SOURCE
#include "metadata.h"
#ifndef __linux__
#error "Linux-only metadata boundary; no platform fallback"
#endif
#include <errno.h>
#include <fcntl.h>
#include <linux/openat2.h>
#include <stdlib.h>
#include <string.h>
#include <sys/syscall.h>
#include <sys/xattr.h>
#include <unistd.h>

#ifdef CM_METADATA_TEST
#include "syscall_controls.h"
#define query_acl cm_test_fgetxattr
#define resolve_directory cm_test_openat2
#else
#define query_acl fgetxattr
static int resolve_directory(int parent, const char *name, const struct open_how *how)
{
    return (int)syscall(SYS_openat2, parent, name, how, sizeof(*how));
}
#endif

struct cm_directory
{
    int fd;
    int parent_fd;
    struct stat initial;
    struct stat parent_initial;
    char name[65];
};

static cm_result result(cm_state state, int error)
{
    cm_result value = {state, error};
    return value;
}

static int same(const struct stat *a, const struct stat *b)
{
    return a->st_dev == b->st_dev && a->st_ino == b->st_ino &&
        a->st_mode == b->st_mode && a->st_uid == b->st_uid &&
        a->st_gid == b->st_gid && a->st_nlink == b->st_nlink &&
        a->st_ctim.tv_sec == b->st_ctim.tv_sec &&
        a->st_ctim.tv_nsec == b->st_ctim.tv_nsec &&
        a->st_mtim.tv_sec == b->st_mtim.tv_sec &&
        a->st_mtim.tv_nsec == b->st_mtim.tv_nsec;
}

static cm_result unchanged(int fd, const struct stat *initial)
{
    struct stat current;
    if (fstat(fd, &current) != 0)
    {
        return result(CM_UNAVAILABLE, errno);
    }
    return result(same(initial, &current) ? CM_FACTS : CM_DRIFT, 0);
}

void cm_directory_destroy(cm_directory **directory)
{
    if (directory == NULL || *directory == NULL)
    {
        return;
    }
    cm_directory *owned = *directory;
    *directory = NULL;
    /* Never retry close: Linux releases the FD even on an interrupted close. */
    close(owned->fd);
    if (owned->parent_fd >= 0)
    {
        close(owned->parent_fd);
    }
    free(owned);
}

cm_result cm_directory_take(int *owned_fd, const struct stat *expected,
    cm_directory **out)
{
    if (owned_fd == NULL)
    {
        return result(CM_INVALID, EINVAL);
    }
    int fd = *owned_fd;
    *owned_fd = -1;
    if (out != NULL)
    {
        *out = NULL;
    }
    if (expected == NULL || out == NULL)
    {
        if (fd >= 0)
        {
            close(fd);
        }
        return result(CM_INVALID, EINVAL);
    }
    cm_directory *owned = calloc(1, sizeof(*owned));
    if (owned == NULL)
    {
        int error = errno;
        if (fd >= 0)
        {
            close(fd);
        }
        return result(CM_UNAVAILABLE, error);
    }
    owned->fd = fd;
    owned->parent_fd = -1;
    cm_result outcome = result(CM_UNAVAILABLE, 0);
    int flags = fcntl(fd, F_GETFL);
    int descriptor_flags = fcntl(fd, F_GETFD);
    if (flags < 0 || descriptor_flags < 0 || fstat(fd, &owned->initial) != 0)
    {
        outcome.error = errno;
    }
    else if ((flags & O_PATH) != 0 || (flags & O_ACCMODE) != O_RDONLY ||
        (descriptor_flags & FD_CLOEXEC) == 0 || !S_ISDIR(owned->initial.st_mode))
    {
        outcome = result(CM_INVALID, EBADF);
    }
    else if (!same(&owned->initial, expected))
    {
        outcome = result(CM_DRIFT, 0);
    }
    else
    {
        *out = owned;
        return result(CM_FACTS, 0);
    }
    cm_directory_destroy(&owned);
    return outcome;
}

cm_result cm_directory_snapshot(const cm_directory *directory, struct stat *out)
{
    if (directory == NULL || out == NULL)
    {
        return result(CM_INVALID, EINVAL);
    }
    cm_result outcome = unchanged(directory->fd, &directory->initial);
    if (outcome.state == CM_FACTS)
    {
        *out = directory->initial;
    }
    return outcome;
}

static int basename_valid(const char *name)
{
    if (name == NULL)
    {
        return 0;
    }
    size_t length = strnlen(name, 65);
    if (length == 0 || length > 64 || strcmp(name, ".") == 0 || strcmp(name, "..") == 0)
    {
        return 0;
    }
    for (size_t i = 0; i < length; ++i)
    {
        unsigned char c = (unsigned char)name[i];
        if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
            (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.'))
        {
            return 0;
        }
    }
    return 1;
}

static int reopen(int parent, const char *name)
{
    const struct open_how how =
    {
        .flags = O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW,
        .resolve = RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS | RESOLVE_NO_MAGICLINKS
    };
    return resolve_directory(parent, name, &how);
}

cm_result cm_directory_open(const cm_directory *parent, const char *basename,
    const struct stat *expected, cm_directory **out)
{
    if (out != NULL)
    {
        *out = NULL;
    }
    if (parent == NULL || expected == NULL || out == NULL || !basename_valid(basename))
    {
        return result(CM_INVALID, EINVAL);
    }
    cm_result outcome = unchanged(parent->fd, &parent->initial);
    if (outcome.state != CM_FACTS)
    {
        return outcome;
    }
    int parent_fd = fcntl(parent->fd, F_DUPFD_CLOEXEC, 0);
    if (parent_fd < 0)
    {
        return result(CM_UNAVAILABLE, errno);
    }
    int child = reopen(parent_fd, basename);
    if (child < 0)
    {
        int error = errno;
        close(parent_fd);
        return result(CM_UNAVAILABLE, error);
    }
    outcome = cm_directory_take(&child, expected, out);
    if (outcome.state != CM_FACTS)
    {
        close(parent_fd);
        return outcome;
    }
    (*out)->parent_fd = parent_fd;
    (*out)->parent_initial = parent->initial;
    memcpy((*out)->name, basename, strlen(basename) + 1);
    outcome = unchanged(parent_fd, &parent->initial);
    if (outcome.state == CM_FACTS && (*out)->initial.st_dev != parent->initial.st_dev)
    {
        outcome = result(CM_DRIFT, 0);
    }
    if (outcome.state != CM_FACTS)
    {
        cm_directory_destroy(out);
    }
    return outcome;
}

static cm_result acls(int fd)
{
    static const char *const names[] =
    {
        "system.posix_acl_access",
        "system.posix_acl_default"
    };
    unsigned char buffer[4096];
    for (size_t i = 0; i < sizeof(names) / sizeof(names[0]); ++i)
    {
        ssize_t size = query_acl(fd, names[i], buffer, sizeof(buffer));
        if (size >= 0)
        {
            return result(CM_ACL_PRESENT, 0);
        }
        int error = errno;
        if (error != ENODATA)
        {
            return result(CM_UNAVAILABLE, error);
        }
    }
    return result(CM_NO_DATA_UNQUALIFIED, ENODATA);
}

static cm_result inputs(const cm_directory *directory, int reopened)
{
    cm_result outcome = unchanged(directory->parent_fd, &directory->parent_initial);
    if (outcome.state == CM_FACTS)
    {
        outcome = unchanged(directory->fd, &directory->initial);
    }
    if (outcome.state == CM_FACTS)
    {
        outcome = unchanged(reopened, &directory->initial);
    }
    return outcome;
}

cm_result cm_directory_check(const cm_directory *directory)
{
    if (directory == NULL || directory->parent_fd < 0)
    {
        return result(CM_INVALID, EINVAL);
    }
    int reopened = reopen(directory->parent_fd, directory->name);
    if (reopened < 0)
    {
        return result(CM_UNAVAILABLE, errno);
    }
    cm_result outcome = inputs(directory, reopened);
    /* Bounded two sweeps on all three FDs; never retry EINTR/ERANGE. */
    for (int sweep = 0; sweep < 2 && outcome.state == CM_FACTS; ++sweep)
    {
        const int fds[] = {directory->parent_fd, directory->fd, reopened};
        for (size_t i = 0; i < sizeof(fds) / sizeof(fds[0]); ++i)
        {
            outcome = acls(fds[i]);
            if (outcome.state != CM_NO_DATA_UNQUALIFIED)
            {
                break;
            }
            outcome = inputs(directory, reopened);
            if (outcome.state != CM_FACTS)
            {
                break;
            }
        }
    }
    close(reopened);
    if (outcome.state == CM_FACTS)
    {
        return result(CM_NO_DATA_UNQUALIFIED, ENODATA);
    }
    return outcome;
}
