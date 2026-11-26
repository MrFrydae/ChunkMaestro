#define _GNU_SOURCE
#include "session_escrow.h"
#if !defined(__linux__)
#error "session escrow requires qualified Linux pidfs and public libsystemd.so.0"
#endif
#include <errno.h>
#include <stddef.h>
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
#include <sys/socket.h>
#include <sys/un.h>
#include "peer.h"

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
#define CM_ARCHITECTURE 1
#elif defined(__aarch64__)
#define CM_RAW_LARGEFILE 0400000
#define CM_ARCHITECTURE 2
#else
#error "session escrow supports Linux x64/arm64 only"
#endif
_Static_assert(sizeof(void *) == 8 && sizeof(long) == 8, "LP64 required");
_Static_assert(sizeof(uint64_t) == 8 && sizeof(uint32_t) == 4, "fixed widths");
_Static_assert(sizeof(dev_t) <= 8 && sizeof(ino_t) <= 8, "identity widths");
_Static_assert(sizeof(cm_original_measurement_v1) == 24, "measurement size");
_Static_assert(_Alignof(cm_original_measurement_v1) == 8, "measurement alignment");
_Static_assert(offsetof(cm_original_measurement_v1, device) == 0, "device offset");
_Static_assert(offsetof(cm_original_measurement_v1, inode) == 8, "inode offset");
_Static_assert(offsetof(cm_original_measurement_v1, flags) == 16, "flags offset");
_Static_assert(offsetof(cm_original_measurement_v1, architecture) == 20, "architecture offset");
_Static_assert(offsetof(cm_original_measurement_v1, normalization) == 21, "normalization offset");
_Static_assert(offsetof(cm_original_measurement_v1, source) == 22, "source offset");
_Static_assert(offsetof(cm_original_measurement_v1, reserved) == 23, "reserved offset");
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
static cm_escrow_result identity_profile(int fd, cm_escrow_identity *out, int strict)
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
    if (strict && (st.st_dev == 0 || st.st_rdev != 0 ||
        ((flags & ~CM_RAW_LARGEFILE) != 2 && (flags & ~CM_RAW_LARGEFILE) != 0x802)))
    {
        return CM_ESCROW_IDENTITY;
    }
    out->device = (uint64_t)st.st_dev;
    out->inode = (uint64_t)st.st_ino;
    /* Match manager DumpFileDescriptorStore: O_LARGEFILE is masked there. */
    out->flags = (uint32_t)(flags & ~CM_RAW_LARGEFILE);
    return CM_ESCROW_OK;
}
static cm_escrow_result identity(int fd, cm_escrow_identity *out)
{
    return identity_profile(fd, out, 0);
}
cm_escrow_result cm_escrow_measure_original_v1(int borrowed, cm_original_measurement_v1 *out)
{
    if (!out)
    {
        return CM_ESCROW_INVALID;
    }
    memset(out, 0, sizeof(*out));
    if (borrowed < 0)
    {
        return CM_ESCROW_INVALID;
    }
    int owned = fcntl(borrowed, F_DUPFD_CLOEXEC, 3);
    if (owned < 0)
    {
        return CM_ESCROW_IO;
    }
    cm_escrow_identity measured = {0};
    cm_escrow_result result = identity_profile(owned, &measured, 1);
    /* Never retry close, including EINTR; preserve the first refusal. */
    if (close(owned) < 0 && result == CM_ESCROW_OK)
    {
        result = CM_ESCROW_IO;
    }
    if (result == CM_ESCROW_OK)
    {
        cm_original_measurement_v1 value = {0};
        value.device = measured.device;
        value.inode = measured.inode;
        value.flags = measured.flags;
        value.architecture = CM_ARCHITECTURE;
        value.normalization = 1;
        value.source = 1;
        *out = value;
    }
    return result;
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

/* Private equivalent of peer.c validation; its existing ABI remains untouched. */
static cm_peer_result candidate_peer_result(cm_peer_state state, int error)
{
    cm_peer_result value = {state, error};
    return value;
}

static cm_peer_result candidate_peer_failed(void)
{
    int error = errno;
    cm_peer_state state = CM_PEER_UNAVAILABLE;
    if (error == EBADF || error == ENOTSOCK || error == ENOTCONN || error == EINVAL)
    {
        state = CM_PEER_INVALID;
    }
    return candidate_peer_result(state, error);
}

static cm_peer_result candidate_peer_query(int fd, cm_peer_facts *out)
{
    if (out == NULL)
    {
        return candidate_peer_result(CM_PEER_INVALID, 0);
    }
    memset(out, 0, sizeof(*out));
    if (fd < 0)
    {
        return candidate_peer_result(CM_PEER_INVALID, 0);
    }
    int type = 0;
    socklen_t length = sizeof(type);
    if (getsockopt(fd, SOL_SOCKET, SO_TYPE, &type, &length) != 0)
    {
        return candidate_peer_failed();
    }
    if (length != sizeof(type) || type != SOCK_STREAM)
    {
        return candidate_peer_result(CM_PEER_INVALID, 0);
    }
    int listening = 0;
    length = sizeof(listening);
    if (getsockopt(fd, SOL_SOCKET, SO_ACCEPTCONN, &listening, &length) != 0)
    {
        return candidate_peer_failed();
    }
    if (length != sizeof(listening) || listening != 0)
    {
        return candidate_peer_result(CM_PEER_INVALID, 0);
    }
    struct sockaddr_storage address;
    memset(&address, 0, sizeof(address));
    length = sizeof(address);
    if (getsockname(fd, (struct sockaddr *)&address, &length) != 0)
    {
        return candidate_peer_failed();
    }
    if (length < sizeof(sa_family_t) || length > sizeof(struct sockaddr_un) ||
        address.ss_family != AF_UNIX)
    {
        return candidate_peer_result(CM_PEER_INVALID, 0);
    }
    memset(&address, 0, sizeof(address));
    length = sizeof(address);
    if (getpeername(fd, (struct sockaddr *)&address, &length) != 0)
    {
        return candidate_peer_failed();
    }
    if (length < sizeof(sa_family_t) || length > sizeof(struct sockaddr_un) ||
        address.ss_family != AF_UNIX)
    {
        return candidate_peer_result(CM_PEER_INVALID, 0);
    }
    struct ucred credentials;
    memset(&credentials, 0, sizeof(credentials));
    length = sizeof(credentials);
    if (getsockopt(fd, SOL_SOCKET, SO_PEERCRED, &credentials, &length) != 0)
    {
        return candidate_peer_failed();
    }
    if (length != sizeof(credentials) || credentials.pid <= 0 ||
        credentials.uid == (uid_t)-1 || credentials.gid == (gid_t)-1)
    {
        return candidate_peer_result(CM_PEER_INVALID, 0);
    }
    out->pid = credentials.pid;
    out->uid = credentials.uid;
    out->gid = credentials.gid;
    return candidate_peer_result(CM_PEER_FACTS, 0);
}

#ifndef SO_PEERPIDFD
#define SO_PEERPIDFD 77
#endif
_Static_assert(sizeof(cm_agent_candidate_diagnostics_v1) == 32, "diagnostics size");
_Static_assert(_Alignof(cm_agent_candidate_diagnostics_v1) == 4, "diagnostics alignment");
#define CM_DIAG_OFFSET(field, n) _Static_assert(offsetof(cm_agent_candidate_diagnostics_v1, field) == n, "diagnostics offset")
CM_DIAG_OFFSET(version, 0);
CM_DIAG_OFFSET(first_stage, 4);
CM_DIAG_OFFSET(first_code, 8);
CM_DIAG_OFFSET(first_errno, 12);
CM_DIAG_OFFSET(cleanup_stage, 16);
CM_DIAG_OFFSET(cleanup_code, 20);
CM_DIAG_OFFSET(cleanup_errno, 24);
CM_DIAG_OFFSET(ownership_flags, 28);
struct cm_agent_candidate_v1
{
    int owned;
    cm_agent_candidate_diagnostics_v1 diagnostics;
};
static void candidate_failure(cm_agent_candidate_diagnostics_v1 *d, unsigned stage,
    cm_escrow_result code, int error)
{
    if (d->first_code == 0)
    {
        d->first_stage = stage;
        d->first_code = code;
        d->first_errno = error > 0 && error <= 4095 ? error : 0;
    }
}
static void candidate_cleanup(cm_agent_candidate_diagnostics_v1 *d, int fd, unsigned stage)
{
    if (fd >= 0 && close(fd) < 0)
    {
        int error = errno;
        candidate_failure(d, stage, CM_ESCROW_IO, error);
        if (d->cleanup_code == 0)
        {
            d->cleanup_stage = stage;
            d->cleanup_code = CM_ESCROW_IO;
            d->cleanup_errno = error > 0 && error <= 4095 ? error : 0;
        }
        d->ownership_flags |= 2;
        if (error <= 0 || error > 4095)
        {
            d->ownership_flags |= 4;
        }
    }
}
static void candidate_profile(cm_agent_candidate_diagnostics_v1 *d, int fd,
    cm_escrow_identity *value, unsigned stage)
{
    cm_escrow_result code = identity_profile(fd, value, 1);
    int error = code == CM_ESCROW_IO ? errno : 0;
    if (code != CM_ESCROW_OK)
    {
        candidate_failure(d, stage, code, error);
    }
}
static void candidate_liveness(cm_agent_candidate_diagnostics_v1 *d, int fd)
{
    struct pollfd p = { .fd = fd, .events = POLLIN };
    int count = poll(&p, 1, 0);
    int error = count < 0 ? errno : 0;
    if (count != 0 || p.revents != 0)
    {
        candidate_failure(d, 8, count < 0 ? CM_ESCROW_IO : CM_ESCROW_IDENTITY, error);
    }
}
cm_escrow_result cm_agent_candidate_close_v1(cm_agent_candidate_v1 **candidate,
    cm_agent_candidate_diagnostics_v1 *diagnostics)
{
    if (!diagnostics)
    {
        return CM_ESCROW_INVALID;
    }
    cm_agent_candidate_diagnostics_v1 d = { .version = 1 };
    if (!candidate)
    {
        candidate_failure(&d, 1, CM_ESCROW_INVALID, 0);
    }
    else if (*candidate)
    {
        cm_agent_candidate_v1 *s = *candidate;
        d = s->diagnostics;
        *candidate = NULL;
        int owned = s->owned;
        s->owned = -1;
        d.ownership_flags &= ~1u;
        candidate_cleanup(&d, owned, 12);
        free(s);
    }
    *diagnostics = d;
    return (cm_escrow_result)d.first_code;
}
cm_escrow_result cm_agent_candidate_acquire_v1(int borrowed, cm_agent_candidate_v1 **out,
    cm_agent_candidate_diagnostics_v1 *diagnostics)
{
    if (!diagnostics)
    {
        return CM_ESCROW_INVALID;
    }
    cm_agent_candidate_diagnostics_v1 d = { .version = 1 };
    if (!out)
    {
        candidate_failure(&d, 1, CM_ESCROW_INVALID, 0);
        *diagnostics = d;
        return CM_ESCROW_INVALID;
    }
    *out = NULL;
    cm_peer_facts facts;
    cm_peer_result peer = candidate_peer_query(borrowed, &facts);
    if (peer.state != CM_PEER_FACTS)
    {
        candidate_failure(&d, 3, peer.state == CM_PEER_INVALID ? CM_ESCROW_INVALID : CM_ESCROW_UNAVAILABLE, peer.error);
        *diagnostics = d;
        return (cm_escrow_result)d.first_code;
    }
    cm_agent_candidate_v1 *s = calloc(1, sizeof(*s));
    if (!s)
    {
        int error = errno;
        candidate_failure(&d, 2, CM_ESCROW_IO, error);
        *diagnostics = d;
        return (cm_escrow_result)d.first_code;
    }
    s->owned = -1;
    int acquired = -1;
    socklen_t length = sizeof(acquired);
    int status = getsockopt(borrowed, SOL_SOCKET, SO_PEERPIDFD, &acquired, &length);
    int error = status != 0 ? errno : 0;
    if (status != 0)
    {
        candidate_failure(&d, 4, CM_ESCROW_UNAVAILABLE, error);
        if (error == EFAULT || error <= 0 || error > 4095 || acquired != -1)
        {
            d.ownership_flags |= 4;
        }
    }
    else
    {
        /* Claim known returned integer immediately, including descriptor zero. */
        s->owned = acquired;
        if (length != sizeof(acquired) || acquired < 0)
        {
            candidate_failure(&d, 5, CM_ESCROW_INVALID, 0);
            if (acquired < 0)
            {
                d.ownership_flags |= 4;
            }
        }
        else
        {
            int flags = fcntl(s->owned, F_GETFD);
            error = flags < 0 ? errno : 0;
            if (flags < 0 || flags != FD_CLOEXEC)
            {
                candidate_failure(&d, 6, flags < 0 ? CM_ESCROW_IO : CM_ESCROW_IDENTITY, error);
            }
            if (d.first_code == 0)
            {
                cm_escrow_identity value = {0};
                candidate_profile(&d, s->owned, &value, 7);
            }
            if (d.first_code == 0)
            {
                candidate_liveness(&d, s->owned);
            }
        }
    }
    if (d.first_code != 0)
    {
        int owned = s->owned;
        s->owned = -1;
        candidate_cleanup(&d, owned, 12);
        free(s);
    }
    else
    {
        d.ownership_flags |= 1;
        s->diagnostics = d;
        *out = s;
    }
    *diagnostics = d;
    return (cm_escrow_result)d.first_code;
}
cm_escrow_result cm_agent_candidate_measure_unqualified_v1(cm_agent_candidate_v1 *s,
    cm_original_measurement_v1 *out, cm_agent_candidate_diagnostics_v1 *diagnostics)
{
    if (!diagnostics)
    {
        return CM_ESCROW_INVALID;
    }
    cm_agent_candidate_diagnostics_v1 d = s ? s->diagnostics :
        (cm_agent_candidate_diagnostics_v1){ .version = 1 };
    if (out)
    {
        memset(out, 0, sizeof(*out));
    }
    if (!s || !out)
    {
        candidate_failure(&d, 1, CM_ESCROW_INVALID, 0);
    }
    if (d.first_code == 0)
    {
        candidate_liveness(&d, s->owned);
    }
    cm_escrow_identity value = {0};
    if (d.first_code == 0)
    {
        int owned = fcntl(s->owned, F_DUPFD_CLOEXEC, 3);
        int error = owned < 0 ? errno : 0;
        if (owned < 0)
        {
            candidate_failure(&d, 9, CM_ESCROW_IO, error);
            if (error <= 0 || error > 4095)
            {
                d.ownership_flags |= 4;
            }
        }
        else
        {
            candidate_profile(&d, owned, &value, 10);
            candidate_cleanup(&d, owned, 11);
        }
    }
    if (d.first_code == 0 && d.cleanup_code == 0 && (d.ownership_flags & 6) == 0)
    {
        out->device = value.device;
        out->inode = value.inode;
        out->flags = value.flags;
        out->architecture = CM_ARCHITECTURE;
        out->normalization = 1;
        out->source = 1;
    }
    if (s)
    {
        s->diagnostics = d;
    }
    *diagnostics = d;
    return (cm_escrow_result)d.first_code;
}
