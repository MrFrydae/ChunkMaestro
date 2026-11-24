#define _GNU_SOURCE
#include "policy_snapshot.h"
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <linux/openat2.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/sysmacros.h>
#include <sys/xattr.h>
#include <unistd.h>

_Static_assert(CHAR_BIT == 8 && sizeof(int32_t) == 4 && sizeof(uint64_t) == 8, "ABI widths");
_Static_assert(sizeof(((struct stat *)0)->st_ino) <= 8 && sizeof(time_t) <= 8 &&
    (time_t)-1 < 0, "Linux stat widths and signed seconds");
static int fits32(uintmax_t value)
{
    return value <= UINT32_MAX;
}
enum { OK, INVALID, UNAVAILABLE, DRIFT, ACL_PRESENT, INCOMPLETE, LIMIT };
struct cm_policy_snapshot
{
    int parent;
    int file;
    int32_t invalid;
    struct stat parent_stat;
    struct stat file_stat;
    uint32_t length;
    uint8_t bytes[16384];
};

static int representable(const struct stat *s)
{
    return s->st_ino != 0 && fits32(s->st_nlink) && fits32(s->st_uid) && fits32(s->st_gid) &&
        fits32(s->st_mode) && fits32(major(s->st_dev)) && fits32(minor(s->st_dev)) &&
        s->st_ctim.tv_nsec >= 0 && s->st_ctim.tv_nsec < 1000000000 &&
        s->st_mtim.tv_nsec >= 0 && s->st_mtim.tv_nsec < 1000000000;
}

static int same(const struct stat *a, const struct stat *b, int leaf)
{
    return representable(b) && a->st_dev == b->st_dev && a->st_ino == b->st_ino &&
        a->st_mode == b->st_mode && a->st_uid == b->st_uid && a->st_gid == b->st_gid &&
        a->st_nlink == b->st_nlink && (!leaf || a->st_size == b->st_size) &&
        a->st_ctim.tv_sec == b->st_ctim.tv_sec && a->st_ctim.tv_nsec == b->st_ctim.tv_nsec &&
        a->st_mtim.tv_sec == b->st_mtim.tv_sec && a->st_mtim.tv_nsec == b->st_mtim.tv_nsec;
}

static int32_t metadata(cm_policy_snapshot *s)
{
    struct stat p, f;
    if (fstat(s->parent, &p) || fstat(s->file, &f))
    {
        return UNAVAILABLE;
    }
    return same(&s->parent_stat, &p, 0) && same(&s->file_stat, &f, 1) ? OK : DRIFT;
}

static int32_t identity(cm_policy_snapshot *s)
{
    struct open_how how = { .flags = O_PATH | O_CLOEXEC | O_NOFOLLOW,
        .resolve = RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS | RESOLVE_NO_MAGICLINKS };
    int fd = (int)syscall(SYS_openat2, s->parent, "policy.json", &how, sizeof(how));
    if (fd < 0)
    {
        return UNAVAILABLE;
    }
    struct stat f;
    int result = fstat(fd, &f);
    close(fd);
    if (result)
    {
        return UNAVAILABLE;
    }
    return same(&s->file_stat, &f, 1) ? OK : DRIFT;
}

static int32_t checkpoint(cm_policy_snapshot *s)
{
    int32_t status = metadata(s);
    if (status != OK)
    {
        return status;
    }
    status = identity(s);
    if (status != OK)
    {
        return status;
    }
    const char *names[] = { "system.posix_acl_access", "system.posix_acl_default" };
    uint8_t acl[4096];
    for (int sweep = 0; sweep < 2; sweep++)
    {
        for (int object = 0; object < 2; object++)
        {
            for (int name = 0; name < 2; name++)
            {
                ssize_t length = fgetxattr(object ? s->file : s->parent, names[name], acl, sizeof(acl));
                if (length >= 0)
                {
                    return ACL_PRESENT;
                }
                if (errno != ENODATA)
                {
                    return UNAVAILABLE;
                }
            }
            status = metadata(s);
            if (status != OK)
            {
                return status;
            }
        }
    }
    return identity(s);
}

static int32_t read_pass(cm_policy_snapshot *s, uint8_t *bytes)
{
    uint32_t offset = 0;
    uint8_t extra;
    for (uint32_t attempt = 0; attempt < 32768; attempt++)
    {
        uint32_t remaining = s->length - offset;
        size_t count = remaining ? (remaining > 4096 ? 4096 : remaining) : 1;
        ssize_t got = pread(s->file, remaining ? bytes + offset : &extra, count, (off_t)offset);
        if (got < 0)
        {
            if (errno == EINTR)
            {
                continue;
            }
            return UNAVAILABLE;
        }
        if ((size_t)got > count)
        {
            return INVALID;
        }
        if (!remaining)
        {
            return got == 0 ? OK : DRIFT;
        }
        if (!got)
        {
            return INCOMPLETE;
        }
        offset += (uint32_t)got;
    }
    return LIMIT;
}

static int flags_valid(int fd)
{
    int descriptor = fcntl(fd, F_GETFD);
    int flags = fcntl(fd, F_GETFL);
    return descriptor >= 0 && (descriptor & FD_CLOEXEC) && flags >= 0 &&
        (flags & O_ACCMODE) == O_RDONLY && !(flags & (O_PATH | O_DIRECT | O_ASYNC));
}

void cm_policy_snapshot_v1_destroy(cm_policy_snapshot **slot)
{
    if (!slot || !*slot)
    {
        return;
    }
    cm_policy_snapshot *s = *slot;
    *slot = NULL;
    if (s->parent >= 0)
    {
        close(s->parent);
    }
    if (s->file >= 0 && s->file != s->parent)
    {
        close(s->file);
    }
    free(s);
}

static int overlaps(const void *a, size_t a_size, const void *b, size_t b_size)
{
    uintptr_t x = (uintptr_t)a, y = (uintptr_t)b;
    return x <= y ? y - x < a_size : x - y < b_size;
}

int32_t cm_policy_snapshot_v1_take(int32_t *parent, int32_t *file, cm_policy_snapshot **output)
{
    if (!parent || !file || !output || overlaps(parent, sizeof(*parent), file, sizeof(*file)) ||
        overlaps(output, sizeof(*output), parent, sizeof(*parent)) || overlaps(output, sizeof(*output), file, sizeof(*file)))
    {
        return INVALID;
    }
    *output = NULL;
    int p = *parent, f = *file;
    *parent = -1;
    *file = -1;
    cm_policy_snapshot *s = calloc(1, sizeof(*s));
    if (!s)
    {
        if (p >= 0)
        {
            close(p);
        }
        if (f >= 0 && f != p)
        {
            close(f);
        }
        return UNAVAILABLE;
    }
    s->parent = p;
    s->file = f;
    int32_t status = INVALID;
    if (p < 0 || f < 0 || p == f || !flags_valid(p) || !flags_valid(f))
    {
        goto failure;
    }
    if (fstat(p, &s->parent_stat) || fstat(f, &s->file_stat))
    {
        status = UNAVAILABLE;
        goto failure;
    }
    if (!representable(&s->parent_stat) || !representable(&s->file_stat) ||
        !S_ISDIR(s->parent_stat.st_mode) || !S_ISREG(s->file_stat.st_mode) ||
        s->file_stat.st_nlink != 1 || s->file_stat.st_size < 1 || s->file_stat.st_size > 16384 ||
        s->file_stat.st_dev != s->parent_stat.st_dev)
    {
        goto failure;
    }
    s->length = (uint32_t)s->file_stat.st_size;
    status = checkpoint(s);
    if (status != OK)
    {
        goto failure;
    }
    status = read_pass(s, s->bytes);
    if (status != OK)
    {
        goto failure;
    }
    status = cm_policy_snapshot_v1_check(s);
    if (status != OK)
    {
        goto failure;
    }
    *output = s;
    return OK;
failure:
    cm_policy_snapshot_v1_destroy(&s);
    return status;
}

int32_t cm_policy_snapshot_v1_check(cm_policy_snapshot *s)
{
    if (!s)
    {
        return INVALID;
    }
    if (s->invalid)
    {
        return s->invalid;
    }
    uint8_t second[16384];
    int32_t status = checkpoint(s);
    if (status == OK)
    {
        status = read_pass(s, second);
    }
    if (status == OK && memcmp(s->bytes, second, s->length) != 0)
    {
        status = DRIFT;
    }
    if (status == OK)
    {
        status = checkpoint(s);
    }
    s->invalid = status;
    return status;
}

int32_t cm_policy_snapshot_v1_copy(cm_policy_snapshot *s, uint8_t *buffer, uint32_t capacity, uint32_t *written)
{
    if (!written)
    {
        return INVALID;
    }
    *written = 0;
    if (!s || !buffer || capacity != s->length)
    {
        return INVALID;
    }
    int32_t status = cm_policy_snapshot_v1_check(s);
    if (status != OK)
    {
        return status;
    }
    memcpy(buffer, s->bytes, s->length);
    *written = s->length;
    return OK;
}

static void put(uint8_t *b, uint64_t value, unsigned width)
{
    for (unsigned i = 0; i < width; i++)
    {
        b[i] = (uint8_t)(value >> (i * 8));
    }
}

static void tuple(uint8_t *b, const struct stat *s)
{
    put(b, major(s->st_dev), 4);
    put(b + 4, minor(s->st_dev), 4);
    put(b + 8, s->st_ino, 8);
    put(b + 16, s->st_uid, 4);
    put(b + 20, s->st_gid, 4);
    put(b + 24, s->st_mode, 4);
    put(b + 28, s->st_nlink, 4);
    put(b + 32, (uint64_t)s->st_ctim.tv_sec, 8);
    put(b + 40, (uint64_t)s->st_mtim.tv_sec, 8);
    put(b + 48, (uint64_t)s->st_ctim.tv_nsec, 4);
    put(b + 52, (uint64_t)s->st_mtim.tv_nsec, 4);
}

int32_t cm_policy_snapshot_v1_facts(cm_policy_snapshot *s, uint8_t *buffer, uint32_t capacity, uint32_t *written)
{
    if (!written)
    {
        return INVALID;
    }
    *written = 0;
    if (!s || !buffer || capacity != 128)
    {
        return INVALID;
    }
    int32_t status = cm_policy_snapshot_v1_check(s);
    if (status != OK)
    {
        return status;
    }
    put(buffer, 1, 4);
    put(buffer + 4, 128, 4);
    tuple(buffer + 8, &s->parent_stat);
    tuple(buffer + 64, &s->file_stat);
    put(buffer + 120, s->length, 4);
    put(buffer + 124, 0, 4);
    *written = 128;
    return OK;
}
