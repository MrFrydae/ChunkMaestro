#define _GNU_SOURCE
#include "agent_connection_owner_unqualified.h"
#include <errno.h>
#include <fcntl.h>
#include <pthread.h>
#include <stdatomic.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <unistd.h>

struct cm_connection_owner
{
    pthread_mutex_t gate;
    atomic_int revoked;
    int closed;
    int destroy_failed;
    int directory;
    int listener;
    int accepted;
    cm_agent_candidate_v1 *candidate;
    cm_owner_diagnostics diagnostics;
};
static int separate(const void *a, size_t na, const void *b, size_t nb)
{
    uintptr_t x = (uintptr_t)a;
    uintptr_t y = (uintptr_t)b;
    return a && b && na <= UINTPTR_MAX - x && nb <= UINTPTR_MAX - y &&
        (x + na <= y || y + nb <= x);
}
static void fail(cm_connection_owner *s, cm_owner_stage stage, int error)
{
    if (!s->diagnostics.first)
    {
        s->diagnostics.first = stage;
        s->diagnostics.first_errno = error > 0 && error <= 4095 ? error : 0;
    }
}
static void close_slot(cm_connection_owner *s, int *slot, cm_owner_stage stage)
{
    int fd = *slot;
    *slot = -1;
    if (fd >= 0 && close(fd) < 0)
    {
        int error = errno;
        if (!s->diagnostics.cleanup)
        {
            s->diagnostics.cleanup = stage;
            s->diagnostics.cleanup_errno = error > 0 && error <= 4095 ? error : 0;
        }
        s->diagnostics.uncertain = 1;
    }
}
static void release(cm_connection_owner *s)
{
    if (s->candidate)
    {
        /* FINAL fixed32 copy, including secondary failure, survives candidate free. */
        cm_agent_candidate_close_v1(&s->candidate, &s->diagnostics.candidate);
    }
    if (s->diagnostics.candidate.ownership_flags & 6)
    {
        s->diagnostics.uncertain = 1;
    }
    close_slot(s, &s->accepted, CM_OWNER_CLOSE_ACCEPTED);
    close_slot(s, &s->listener, CM_OWNER_CLOSE_LISTENER);
    close_slot(s, &s->directory, CM_OWNER_CLOSE_DIRECTORY);
    /* Never unlink: the pathname might now designate a replacement. */
}
static int admit(cm_connection_owner *s)
{
    if (atomic_load(&s->revoked))
    {
        fail(s, CM_OWNER_REVOKED, 0);
    }
    return s->diagnostics.first == CM_OWNER_NONE && !s->diagnostics.uncertain;
}
static int enter(cm_connection_owner *s, cm_owner_diagnostics *d)
{
    if (!s || !d || !separate(s, sizeof(*s), d, sizeof(*d)))
    {
        return 0;
    }
    if (pthread_mutex_trylock(&s->gate) != 0)
    {
        atomic_store(&s->revoked, 1);
        /* Do not race the active operation's sticky diagnostics. */
        *d = (cm_owner_diagnostics){ .version = 1, .first = CM_OWNER_REVOKED };
        return 0;
    }
    return 1;
}
static int finish(cm_connection_owner *s, cm_owner_diagnostics *d)
{
    int ok = admit(s);
    *d = s->diagnostics;
    pthread_mutex_unlock(&s->gate);
    return ok;
}
static int flags(int fd)
{
    int descriptor = fcntl(fd, F_GETFD);
    int error = descriptor < 0 ? errno : 0;
    int status = fcntl(fd, F_GETFL);
    if (status < 0)
    {
        error = errno;
    }
    errno = error;
    return descriptor == FD_CLOEXEC && status >= 0 && (status & O_NONBLOCK);
}
static int same_directory(int fd, const char *path)
{
    struct stat held;
    struct stat named;
    return fstat(fd, &held) == 0 && lstat(path, &named) == 0 &&
        S_ISDIR(named.st_mode) && held.st_dev == named.st_dev &&
        held.st_ino == named.st_ino && named.st_uid == geteuid() &&
        (named.st_mode & 07777) == 0700;
}
int cm_connection_create(const char *directory, size_t length,
    cm_connection_owner **out, cm_connection_lifetime **lifetime,
    cm_owner_diagnostics *d)
{
    if (!d || !out || !lifetime || !directory || length == 0 || length > 95 ||
        !separate(out, sizeof(*out), d, sizeof(*d)) ||
        !separate(lifetime, sizeof(*lifetime), d, sizeof(*d)) ||
        !separate(out, sizeof(*out), lifetime, sizeof(*lifetime)) ||
        !separate(directory, length, d, sizeof(*d)) ||
        !separate(directory, length, lifetime, sizeof(*lifetime)) ||
        !separate(directory, length, out, sizeof(*out)))
    {
        return 0;
    }
    *out = NULL;
    *lifetime = NULL;
    *d = (cm_owner_diagnostics){ .version = 1 };
    if (memchr(directory, 0, length) || directory[0] != '/')
    {
        d->first = CM_OWNER_ARGUMENT;
        return 0;
    }
    cm_connection_owner *s = calloc(1, sizeof(*s));
    if (!s)
    {
        d->first = CM_OWNER_ALLOCATE;
        d->first_errno = ENOMEM;
        return 0;
    }
    s->directory = -1;
    s->listener = -1;
    s->accepted = -1;
    s->candidate = NULL;
    s->diagnostics.version = 1;
    atomic_init(&s->revoked, 0);
    pthread_mutexattr_t attr;
    int error = pthread_mutexattr_init(&attr);
    if (!error)
    {
        error = pthread_mutexattr_settype(&attr, PTHREAD_MUTEX_ERRORCHECK);
        if (!error)
        {
            error = pthread_mutex_init(&s->gate, &attr);
        }
        pthread_mutexattr_destroy(&attr);
    }
    if (error)
    {
        d->first = CM_OWNER_ALLOCATE;
        d->first_errno = error;
        free(s);
        return 0;
    }
    char path[108];
    char canonical[108];
    memcpy(path, directory, length);
    path[length] = 0;
    /* realpath writes PATH_MAX bytes in general; allocated form avoids overflow. */
    char *resolved = realpath(path, NULL);
    if (!resolved || strcmp(resolved, path) != 0)
    {
        fail(s, CM_OWNER_PATH, 0);
    }
    free(resolved);
    memcpy(canonical, path, length + 1);
    if (admit(s))
    {
        s->directory = open(path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
        if (s->directory < 0 || !same_directory(s->directory, path))
        {
            fail(s, CM_OWNER_DIRECTORY, errno);
        }
    }
    memcpy(path + length, "/" CM_OWNER_BASENAME, sizeof("/" CM_OWNER_BASENAME));
    struct stat existing;
    if (admit(s) && (lstat(path, &existing) == 0 || errno != ENOENT))
    {
        fail(s, CM_OWNER_PATH, EEXIST);
    }
    if (admit(s))
    {
        s->listener = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC | SOCK_NONBLOCK, 0);
        if (s->listener < 0)
        {
            fail(s, CM_OWNER_SOCKET, errno);
        }
    }
    if (admit(s) && !flags(s->listener))
    {
        fail(s, CM_OWNER_FLAGS, errno);
    }
    /* Resource bound only; Linux may double SO_RCVBUF. No identity significance. */
    int receive_bytes = 4096;
    if (admit(s) && setsockopt(s->listener, SOL_SOCKET, SO_RCVBUF,
        &receive_bytes, sizeof(receive_bytes)) < 0)
    {
        fail(s, CM_OWNER_OPTIONS, errno);
    }
    struct sockaddr_un address = { .sun_family = AF_UNIX };
    memcpy(address.sun_path, path, strlen(path) + 1);
    if (admit(s) && !same_directory(s->directory, canonical))
    {
        fail(s, CM_OWNER_PATH, 0);
    }
    if (admit(s) && bind(s->listener, (struct sockaddr *)&address, sizeof(address)) < 0)
    {
        fail(s, CM_OWNER_BIND, errno);
    }
    if (admit(s) && !same_directory(s->directory, canonical))
    {
        fail(s, CM_OWNER_PATH, 0);
    }
    if (admit(s) && listen(s->listener, 1) < 0)
    {
        fail(s, CM_OWNER_LISTEN, errno);
    }
    if (!admit(s))
    {
        release(s);
        *d = s->diagnostics;
        pthread_mutex_destroy(&s->gate);
        free(s);
        return 0;
    }
    *d = s->diagnostics;
    *out = s;
    *lifetime = (cm_connection_lifetime *)s;
    return 1;
}
int cm_connection_attempt(cm_connection_owner *s, cm_owner_diagnostics *d)
{
    if (!enter(s, d))
    {
        return 0;
    }
    if (admit(s) && s->diagnostics.attempted)
    {
        fail(s, CM_OWNER_REPLAY, 0);
    }
    if (admit(s))
    {
        s->diagnostics.attempted = 1;
        s->accepted = accept4(s->listener, NULL, NULL, SOCK_CLOEXEC | SOCK_NONBLOCK);
        if (s->accepted < 0)
        {
            fail(s, CM_OWNER_ACCEPT, errno);
        }
    }
    if (admit(s) && !flags(s->accepted))
    {
        fail(s, CM_OWNER_FLAGS, errno);
    }
    if (admit(s) && cm_agent_candidate_acquire_v1(s->accepted, &s->candidate,
        &s->diagnostics.candidate) != CM_ESCROW_OK)
    {
        fail(s, CM_OWNER_CANDIDATE, 0);
    }
    return finish(s, d);
}
int cm_connection_measure(cm_connection_owner *s, cm_original_measurement_v1 *out,
    cm_owner_diagnostics *d)
{
    if (!out || !d || !s || !separate(out, sizeof(*out), d, sizeof(*d)) ||
        !separate(out, sizeof(*out), s, sizeof(*s)))
    {
        return 0;
    }
    memset(out, 0, sizeof(*out));
    if (!enter(s, d))
    {
        return 0;
    }
    cm_original_measurement_v1 value = {0};
    if (admit(s) && !s->candidate)
    {
        fail(s, CM_OWNER_MEASURE, 0);
    }
    if (admit(s) && cm_agent_candidate_measure_unqualified_v1(s->candidate, &value,
        &s->diagnostics.candidate) != CM_ESCROW_OK)
    {
        fail(s, CM_OWNER_MEASURE, 0);
    }
    int ok = admit(s); /* Final admission after the synchronous borrow. */
    if (ok)
    {
        *out = value;
    }
    *d = s->diagnostics;
    pthread_mutex_unlock(&s->gate);
    return ok;
}
int cm_connection_close(cm_connection_owner **owner, cm_owner_diagnostics *d)
{
    if (!owner || !d || !separate(owner, sizeof(*owner), d, sizeof(*d)))
    {
        return 0;
    }
    cm_connection_owner *s = *owner;
    if (!s)
    {
        return 1;
    }
    if (!separate(s, sizeof(*s), d, sizeof(*d)) ||
        !separate(s, sizeof(*s), owner, sizeof(*owner)))
    {
        return 0;
    }
    atomic_store(&s->revoked, 1);
    int error = pthread_mutex_lock(&s->gate);
    if (error != 0)
    {
        *d = (cm_owner_diagnostics){ .version = 1, .first = CM_OWNER_REVOKED,
            .first_errno = error > 0 && error <= 4095 ? error : 0 };
        return 0;
    }
    *owner = NULL;
    if (!s->closed)
    {
        release(s);
        s->closed = 1;
    }
    *d = s->diagnostics;
    int ok = !d->first && !d->cleanup && !d->uncertain;
    pthread_mutex_unlock(&s->gate);
    /* Keep storage and the mutex alive for contenders which have not returned.
     * Only the separate quiescent lifetime token may destroy this allocation. */
    return ok;
}
int cm_connection_destroy(cm_connection_lifetime **lifetime, cm_owner_diagnostics *d)
{
    if (!lifetime || !d || !separate(lifetime, sizeof(*lifetime), d, sizeof(*d)))
    {
        return 0;
    }
    cm_connection_owner *s = (cm_connection_owner *)*lifetime;
    if (!s)
    {
        return 1;
    }
    if (!separate(s, sizeof(*s), d, sizeof(*d)) ||
        !separate(s, sizeof(*s), lifetime, sizeof(*lifetime)))
    {
        return 0;
    }
    /* The caller must already have joined all entrants. trylock is a refusal
     * check, not evidence that a paused entrant cannot exist. */
    if (s->destroy_failed)
    {
        *d = s->diagnostics;
        return 0;
    }
    int error = pthread_mutex_trylock(&s->gate);
    if (error != 0)
    {
        atomic_store(&s->revoked, 1);
        *d = (cm_owner_diagnostics){ .version = 1, .first = CM_OWNER_REVOKED,
            .first_errno = error > 0 && error <= 4095 ? error : 0 };
        return 0;
    }
    if (!s->closed)
    {
        atomic_store(&s->revoked, 1);
        fail(s, CM_OWNER_DESTROY, 0);
        *d = s->diagnostics;
        pthread_mutex_unlock(&s->gate);
        return 0;
    }
    pthread_mutex_unlock(&s->gate);
    error = pthread_mutex_destroy(&s->gate);
    if (error != 0)
    {
        fail(s, CM_OWNER_DESTROY, error);
        if (!s->diagnostics.cleanup)
        {
            s->diagnostics.cleanup = CM_OWNER_DESTROY;
            s->diagnostics.cleanup_errno = error > 0 && error <= 4095 ? error : 0;
        }
        s->diagnostics.uncertain = 1;
        s->destroy_failed = 1;
        *d = s->diagnostics;
        return 0;
    }
    *d = s->diagnostics;
    *lifetime = NULL;
    int ok = !d->first && !d->cleanup && !d->uncertain;
    free(s);
    return ok;
}
