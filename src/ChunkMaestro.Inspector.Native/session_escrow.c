#define _GNU_SOURCE
#include "session_escrow.h"
#if !defined(__linux__)
#error "session escrow requires qualified Linux pidfs and public libsystemd.so.0"
#endif
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/vfs.h>
#include <time.h>
#include <unistd.h>
#include <sys/types.h>

/* Public sd-daemon.h ABI only, independently declared, no LGPL implementation
 * copied. Link public SONAME; required symbol versions fail closed at load. */
extern int sd_pid_notify_with_fds(pid_t, int, const char *, const int *, unsigned);
extern int sd_notify_barrier(int, uint64_t);
extern int sd_listen_fds_with_names(int, char ***);
#ifndef CM_ESCROW_INTERPOSE
__asm__(".symver sd_pid_notify_with_fds,sd_pid_notify_with_fds@LIBSYSTEMD_219");
__asm__(".symver sd_notify_barrier,sd_notify_barrier@LIBSYSTEMD_246");
__asm__(".symver sd_listen_fds_with_names,sd_listen_fds_with_names@LIBSYSTEMD_227");
#endif
#if defined(__x86_64__)
#define CM_RAW_LARGEFILE 0100000
#elif defined(__aarch64__)
#define CM_RAW_LARGEFILE 0400000
#else
#error "session escrow supports Linux x64/arm64 only"
#endif
#define CM_PIDFS_MAGIC 0x50494446
#define CM_PIDFD_THREAD O_EXCL
#define CM_NAME_SIZE 48
static int handback_consumed; /* Serialized startup-only, one adoption per process. */
struct cm_escrow
{
    int fd[2];
    cm_escrow_identity identity[2];
    char name[2][CM_NAME_SIZE];
    cm_escrow_result first;
    int attempted;
};
static cm_escrow_result fail(cm_escrow *s, cm_escrow_result result)
{
    if (s->first == CM_ESCROW_OK)
    {
        s->first = result;
    }
    return s->first;
}
static int names(cm_escrow *s, const char *epoch)
{
    if (!epoch || strnlen(epoch, 33) != 32)
    {
        return 0;
    }
    for (unsigned i = 0; i < 32; ++i)
    {
        if (!((epoch[i] >= '0' && epoch[i] <= '9') || (epoch[i] >= 'a' && epoch[i] <= 'f')))
        {
            return 0;
        }
    }
    return snprintf(s->name[0], CM_NAME_SIZE, "cm-worker-%s", epoch) == 42 &&
        snprintf(s->name[1], CM_NAME_SIZE, "cm-agent-%s", epoch) == 41;
}
static cm_escrow_result identity(int fd, cm_escrow_identity *out)
{
    struct stat st;
    struct statfs fs;
    int flags = fcntl(fd, F_GETFL);
    if (flags < 0 || fstat(fd, &st) < 0 || fstatfs(fd, &fs) < 0)
    {
        return CM_ESCROW_IO;
    }
    if (fs.f_type != CM_PIDFS_MAGIC || (st.st_mode & S_IFMT) != 0 ||
        st.st_ino == 0 || (flags & CM_PIDFD_THREAD) != 0 ||
        (flags & O_ACCMODE) != O_RDWR)
    {
        return CM_ESCROW_IDENTITY;
    }
    out->device = (uint64_t)st.st_dev;
    out->inode = (uint64_t)st.st_ino;
    /* Match manager DumpFileDescriptorStore: O_LARGEFILE is masked there. */
    out->flags = (uint32_t)(flags & ~CM_RAW_LARGEFILE);
    return CM_ESCROW_OK;
}
/* Process identity excludes per-file-description status flags. Two original
 * pidfds for the same task must never occupy the two independent roles. */
static int same_process(const cm_escrow_identity *a, const cm_escrow_identity *b)
{
    return a->device == b->device && a->inode == b->inode;
}
static int same_metadata(const cm_escrow_identity *a, const cm_escrow_identity *b)
{
    return same_process(a, b) && a->flags == b->flags;
}
static cm_escrow *allocate(const char *epoch)
{
    cm_escrow *s = calloc(1, sizeof(*s));
    if (s)
    {
        s->fd[0] = -1;
        s->fd[1] = -1;
        if (!names(s, epoch))
        {
            free(s);
            return NULL;
        }
    }
    return s;
}
cm_escrow_result cm_escrow_close(cm_escrow **session)
{
    if (!session || !*session)
    {
        return CM_ESCROW_INVALID;
    }
    cm_escrow *s = *session;
    *session = NULL;
    cm_escrow_result result = s->first;
    for (unsigned i = 0; i < 2; ++i)
    {
        /* Linux close consumes FD even on EINTR; NEVER retry a reused number. */
        if (s->fd[i] >= 0 && close(s->fd[i]) < 0 && result == CM_ESCROW_OK)
        {
            result = CM_ESCROW_IO;
        }
    }
    free(s);
    return result;
}
cm_escrow_result cm_escrow_bind(int worker, int agent, const char *epoch, cm_escrow **out)
{
    if (!out)
    {
        return CM_ESCROW_INVALID;
    }
    *out = NULL;
    cm_escrow *s = allocate(epoch);
    if (!s)
    {
        return CM_ESCROW_INVALID;
    }
    int borrowed[2] = { worker, agent };
    cm_escrow_result result = CM_ESCROW_OK;
    for (unsigned i = 0; i < 2; ++i)
    {
        s->fd[i] = fcntl(borrowed[i], F_DUPFD_CLOEXEC, 3);
        if (s->fd[i] < 0)
        {
            result = CM_ESCROW_IO;
            break;
        }
        result = identity(s->fd[i], &s->identity[i]);
        if (result != CM_ESCROW_OK)
        {
            break;
        }
    }
    if (result == CM_ESCROW_OK && same_process(&s->identity[0], &s->identity[1]))
    {
        result = CM_ESCROW_IDENTITY;
    }
    if (result != CM_ESCROW_OK)
    {
        fail(s, result);
        return cm_escrow_close(&s);
    }
    *out = s;
    return CM_ESCROW_OK;
}
cm_escrow_result cm_escrow_metadata(const cm_escrow *s, cm_escrow_identity out[2])
{
    if (!out)
    {
        return CM_ESCROW_INVALID;
    }
    memset(out, 0, sizeof(*out) * 2);
    if (!s)
    {
        return CM_ESCROW_INVALID;
    }
    if (s->first != CM_ESCROW_OK)
    {
        return s->first;
    }
    memcpy(out, s->identity, sizeof(s->identity));
    return CM_ESCROW_OK;
}
static cm_escrow_result remaining(uint64_t deadline, uint64_t *left)
{
    struct timespec now;
    if (clock_gettime(CLOCK_BOOTTIME, &now) < 0 || now.tv_sec < 0)
    {
        return CM_ESCROW_IO;
    }
    uint64_t current = (uint64_t)now.tv_sec * 1000000 + (uint64_t)now.tv_nsec / 1000;
    if (deadline <= current || deadline - current > 30000000)
    {
        return CM_ESCROW_DEADLINE;
    }
    *left = deadline - current;
    return CM_ESCROW_OK;
}
cm_escrow_result cm_escrow_upload(cm_escrow *s, uint64_t deadline)
{
    if (!s)
    {
        return CM_ESCROW_INVALID;
    }
    if (s->first != CM_ESCROW_OK)
    {
        return s->first;
    }
    if (s->attempted)
    {
        return fail(s, CM_ESCROW_REPLAY);
    }
    s->attempted = 1;
    uint64_t left;
    cm_escrow_result result;
    for (unsigned i = 0; i < 2; ++i)
    {
        result = remaining(deadline, &left);
        if (result != CM_ESCROW_OK)
        {
            return fail(s, result);
        }
        char message[96];
        int n = snprintf(message, sizeof(message), "FDSTORE=1\nFDPOLL=0\nFDNAME=%s", s->name[i]);
        if (n < 0 || (size_t)n >= sizeof(message))
        {
            return fail(s, CM_ESCROW_INVALID);
        }
        /* Retain original owned duplicate. Library sends SCM_RIGHTS copies. */
        if (sd_pid_notify_with_fds(0, 0, message, &s->fd[i], 1) <= 0)
        {
            return fail(s, CM_ESCROW_NOTIFY);
        }
    }
    result = remaining(deadline, &left);
    if (result != CM_ESCROW_OK)
    {
        return fail(s, result);
    }
    if (sd_notify_barrier(0, left) <= 0)
    {
        return fail(s, CM_ESCROW_NOTIFY);
    }
    result = remaining(deadline, &left);
    return result == CM_ESCROW_OK ? result : fail(s, result);
}
cm_escrow_result cm_escrow_observe(cm_escrow *s, unsigned role, cm_escrow_observation *out)
{
    if (out)
    {
        memset(out, 0, sizeof(*out));
    }
    if (!s || !out || role > 1)
    {
        return s ? fail(s, CM_ESCROW_INVALID) : CM_ESCROW_INVALID;
    }
    if (s->first != CM_ESCROW_OK)
    {
        return s->first;
    }
    struct pollfd p = { .fd = s->fd[role], .events = POLLIN };
    int n = poll(&p, 1, 0);
    if (n < 0 || (p.revents & (POLLERR | POLLNVAL)) != 0)
    {
        return fail(s, CM_ESCROW_IO);
    }
    out->readable = (p.revents & POLLIN) != 0;
    out->hup = (p.revents & POLLHUP) != 0;
    return CM_ESCROW_OK;
}
cm_escrow_result cm_escrow_handback(const char *epoch, const cm_escrow_identity expected[2], cm_escrow **out)
{
    if (!out)
    {
        return CM_ESCROW_INVALID;
    }
    *out = NULL;
    if (handback_consumed)
    {
        return CM_ESCROW_REPLAY;
    }
    handback_consumed = 1;
    const char *count = getenv("LISTEN_FDS");
    unsigned declared = 0;
    if (!count || strnlen(count, 3) == 0 || strnlen(count, 3) > 2 ||
        (count[0] == '0' && count[1] != '\0'))
    {
        return CM_ESCROW_INVALID;
    }
    for (unsigned i = 0; count[i]; ++i)
    {
        if (count[i] < '0' || count[i] > '9')
        {
            return CM_ESCROW_INVALID;
        }
        declared = declared * 10 + (unsigned)(count[i] - '0');
    }
    if (declared > 16)
    {
        return CM_ESCROW_INVALID;
    }
    cm_escrow *s = allocate(epoch);
    cm_escrow_result result = CM_ESCROW_INVALID;
    char **received = NULL;
    char pid[32];
    snprintf(pid, sizeof(pid), "%ld", (long)getpid());
    const char *listen_pid = getenv("LISTEN_PID");
    const char *labels = getenv("LISTEN_FDNAMES");
    if (!s || !expected || declared != 2 || !listen_pid ||
        strnlen(listen_pid, sizeof(pid)) >= sizeof(pid) || strcmp(pid, listen_pid) != 0 ||
        !labels || strnlen(labels, 96) >= 96)
    {
        goto done;
    }
    /* Prevalidate names before libsystemd's allocating splitter. */
    char forward[96];
    char reverse[96];
    snprintf(forward, sizeof(forward), "%s:%s", s->name[0], s->name[1]);
    snprintf(reverse, sizeof(reverse), "%s:%s", s->name[1], s->name[0]);
    if (strcmp(labels, forward) != 0 && strcmp(labels, reverse) != 0)
    {
        goto done;
    }
    int n = sd_listen_fds_with_names(1, &received);
    if (n != 2 || !received || !received[0] || !received[1] || received[2])
    {
        result = CM_ESCROW_IO;
        goto done;
    }
    for (unsigned i = 0; i < 2; ++i)
    {
        result = CM_ESCROW_INVALID;
        unsigned role;
        if (strcmp(received[i], s->name[0]) == 0)
        {
            role = 0;
        }
        else if (strcmp(received[i], s->name[1]) == 0)
        {
            role = 1;
        }
        else
        {
            goto done;
        }
        if (s->fd[role] >= 0)
        {
            goto done;
        }
        s->fd[role] = (int)i + 3;
        if (fcntl(s->fd[role], F_SETFD, FD_CLOEXEC) < 0)
        {
            result = CM_ESCROW_IO;
            goto done;
        }
        result = identity(s->fd[role], &s->identity[role]);
        if (result != CM_ESCROW_OK)
        {
            goto done;
        }
        if (!same_metadata(&s->identity[role], &expected[role]))
        {
            result = CM_ESCROW_IDENTITY;
            goto done;
        }
    }
    if (same_process(&s->identity[0], &s->identity[1]))
    {
        result = CM_ESCROW_IDENTITY;
        goto done;
    }
    /* Recovery must never upload or resume this generation. */
    s->attempted = 1;
    *out = s;
    s = NULL;
    result = CM_ESCROW_OK;
done:
    unsetenv("LISTEN_PID");
    unsetenv("LISTEN_FDS");
    unsetenv("LISTEN_FDNAMES");
    if (received)
    {
        /* Public API guarantees NULL-terminated vector on success. Bounded
         * environment limits allocated names to two, even on library errors. */
        for (unsigned i = 0; i < 2 && received[i]; ++i)
        {
            free(received[i]);
        }
        free(received);
    }
    if (result != CM_ESCROW_OK)
    {
        for (unsigned i = 0; i < declared; ++i)
        {
            /* Preserve the first refusal; no close retry on Linux EINTR. */
            (void)close((int)i + 3);
        }
    }
    if (s)
    {
        /* Already closed the entire inherited range, including assigned FDs. */
        s->fd[0] = -1;
        s->fd[1] = -1;
        free(s);
    }
    return result;
}
const char *cm_escrow_error(cm_escrow_result result)
{
    switch (result)
    {
        case CM_ESCROW_OK: return "escrow-facts-only";
        case CM_ESCROW_INVALID: return "escrow-invalid";
        case CM_ESCROW_UNAVAILABLE: return "escrow-unavailable";
        case CM_ESCROW_IDENTITY: return "escrow-identity-mismatch";
        case CM_ESCROW_IO: return "escrow-io-failed";
        case CM_ESCROW_NOTIFY: return "escrow-notify-unconfirmed";
        case CM_ESCROW_DEADLINE: return "escrow-deadline";
        case CM_ESCROW_REPLAY: return "escrow-attempt-consumed";
        default: return "escrow-unknown";
    }
}
