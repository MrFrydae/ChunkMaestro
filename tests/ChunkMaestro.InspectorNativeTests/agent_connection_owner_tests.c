#define _GNU_SOURCE
#include "agent_connection_owner_unqualified.h"
#include <assert.h>
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <pthread.h>
#include <signal.h>
#include <sched.h>
#include <stdarg.h>
#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <sys/wait.h>
#include <unistd.h>

/* Compile-time fixture interposition ONLY; no hooks in production source/API. */
static int fault, secondary, accept_calls, candidate_calls, close_calls;
static int action, hook_stage, flags_calls, slot_zero, reuse_fd = -1;
static cm_connection_owner *live;
static cm_connection_lifetime *lifetime;
static void *tracked_owner;
static int owner_free_calls;
static unsigned cases;
static void owner_free(void *value)
{
    if (value && value == tracked_owner)
    {
        owner_free_calls++;
    }
    free(value);
}
static char moved_directory[108];
static void completion(int stage);
static void *owner_alloc(size_t n, size_t size)
{
    if (fault == 1)
    {
        errno = ENOMEM;
        return NULL;
    }
    return calloc(n, size);
}
static int owner_socket(int domain, int type, int protocol)
{
    if (fault == 3)
    {
        errno = EMFILE;
        return -1;
    }
    if (slot_zero == 2)
    {
        assert(close(0) == 0);
    }
    return socket(domain, type, protocol);
}
static int owner_open(const char *path, int mode)
{
    if (fault == 2)
    {
        errno = EACCES;
        return -1;
    }
    if (slot_zero == 1)
    {
        assert(close(0) == 0);
    }
    return open(path, mode);
}
static int owner_flags(int fd, int command)
{
    flags_calls++;
    completion(4);
    if (fault == 20 && command == F_GETFD && flags_calls > 2)
    {
        return 0;
    }
    if ((fault == 4 && flags_calls <= 2) || (fault == 9 && flags_calls > 2))
    {
        errno = EIO;
        return -1;
    }
    return fcntl(fd, command);
}
static int owner_options(int fd, int level, int option, const void *value, socklen_t size)
{
    if (fault == 5)
    {
        errno = ENOBUFS;
        return -1;
    }
    return setsockopt(fd, level, option, value, size);
}
static int owner_bind(int fd, const struct sockaddr *address, socklen_t size)
{
    if (fault == 6)
    {
        errno = EADDRINUSE;
        return -1;
    }
    if (fault == 21)
    {
        const struct sockaddr_un *a = (const struct sockaddr_un *)address;
        char parent[108];
        strcpy(parent, a->sun_path);
        char *slash = strrchr(parent, '/');
        assert(slash);
        *slash = 0;
        assert(snprintf(moved_directory, sizeof(moved_directory), "%s-old", parent) > 0);
        assert(rename(parent, moved_directory) == 0);
        assert(mkdir(parent, 0700) == 0);
    }
    return bind(fd, address, size);
}
static int owner_listen(int fd, int count)
{
    if (fault == 7)
    {
        errno = EIO;
        return -1;
    }
    return listen(fd, count);
}
static int owner_accept(int fd, struct sockaddr *address, socklen_t *size, int mode)
{
    accept_calls++;
    completion(1);
    if (fault == 8 || fault == 18 || fault == 19)
    {
        errno = fault == 8 ? EINTR : fault == 18 ? EAGAIN : ENOMEM;
        return -1;
    }
    if (slot_zero == 3)
    {
        assert(close(0) == 0);
    }
#ifdef CM_OWNER_CONTROLS
    (void)address;
    (void)size;
    (void)mode;
    return fcntl(fd, F_DUPFD_CLOEXEC, slot_zero == 3 ? 0 : 3);
#else
    return accept4(fd, address, size, mode);
#endif
}
static int owner_close(int fd)
{
    close_calls++;
    int status = close(fd);
    if (secondary)
    {
        /* Linux consumed-close model plus immediate controlled descriptor reuse. */
        if (reuse_fd < 0)
        {
            reuse_fd = open("/dev/null", O_RDONLY | O_CLOEXEC);
            assert(reuse_fd == fd);
        }
        errno = EINTR;
        return -1;
    }
    return status;
}
static cm_escrow_result owner_acquire(int fd, cm_agent_candidate_v1 **out,
    cm_agent_candidate_diagnostics_v1 *d);
static cm_escrow_result owner_measure(cm_agent_candidate_v1 *c,
    cm_original_measurement_v1 *out, cm_agent_candidate_diagnostics_v1 *d);
static cm_escrow_result owner_candidate_close(cm_agent_candidate_v1 **c,
    cm_agent_candidate_diagnostics_v1 *d);
#ifdef CM_OWNER_CONTROLS
static atomic_int pause_busy, busy_observed, resume_busy;
static int owner_trylock(pthread_mutex_t *mutex)
{
    int result = pthread_mutex_trylock(mutex);
    if (result == EBUSY && atomic_load(&pause_busy))
    {
        atomic_store(&busy_observed, 1);
        while (!atomic_load(&resume_busy))
        {
            sched_yield();
        }
    }
    return result;
}
#define pthread_mutex_trylock owner_trylock
#endif
#define free owner_free
#define calloc owner_alloc
#define open owner_open
#define socket owner_socket
#define fcntl owner_flags
#define setsockopt owner_options
#define bind owner_bind
#define listen owner_listen
#define accept4 owner_accept
#define close owner_close
#define cm_agent_candidate_acquire_v1 owner_acquire
#define cm_agent_candidate_measure_unqualified_v1 owner_measure
#define cm_agent_candidate_close_v1 owner_candidate_close
#include "agent_connection_owner_unqualified.c"
#ifdef CM_OWNER_CONTROLS
#undef pthread_mutex_trylock
#endif
#undef free
#undef calloc
#undef open
#undef socket
#undef fcntl
#undef setsockopt
#undef bind
#undef listen
#undef accept4
#undef close
#undef cm_agent_candidate_acquire_v1
#undef cm_agent_candidate_measure_unqualified_v1
#undef cm_agent_candidate_close_v1

static cm_escrow_result owner_acquire(int fd, cm_agent_candidate_v1 **out,
    cm_agent_candidate_diagnostics_v1 *d)
{
    candidate_calls++;
    completion(2);
    assert(live && fd == live->accepted && fd != live->listener); /* ENDPOINT */
#ifdef CM_OWNER_CONTROLS
    *d = (cm_agent_candidate_diagnostics_v1){ .version = 1 };
    if (fault == 10 || fault == 11)
    {
        *out = NULL;
        d->first_stage = fault == 10 ? 2 : 5;
        d->first_code = CM_ESCROW_IO;
        d->ownership_flags = fault == 11 ? 6 : 0;
        return CM_ESCROW_IO;
    }
    *out = (cm_agent_candidate_v1 *)live; /* fixture-only token, never dereferenced */
    d->ownership_flags = 1;
    return CM_ESCROW_OK;
#else
    return cm_agent_candidate_acquire_v1(fd, out, d);
#endif
}
static void completion(int stage)
{
    if (!live || !action || (hook_stage && hook_stage != stage))
    {
        return;
    }
    int saved = action;
    action = 0;
    if (saved == 1)
    {
        cm_owner_diagnostics inner;
        assert(!cm_connection_attempt(live, &inner));
        assert(inner.first == CM_OWNER_REVOKED);
    }
    else if (saved == 2)
    {
        cm_owner_diagnostics inner;
        cm_connection_owner *token = live;
        assert(!cm_connection_close(&token, &inner));
        assert(token == live);
    }
    else
    {
        atomic_store(&live->revoked, 1);
    }
}
static cm_escrow_result owner_measure(cm_agent_candidate_v1 *c,
    cm_original_measurement_v1 *out, cm_agent_candidate_diagnostics_v1 *d)
{
    completion(3);
#ifdef CM_OWNER_CONTROLS
    (void)c;
    out->device = 4;
    out->inode = 5;
    out->flags = 2;
    if (fault == 12)
    {
        d->first_stage = 9;
        d->first_code = CM_ESCROW_IO;
        d->first_errno = EMFILE;
    }
    if (fault == 13)
    {
        d->first_stage = 11;
        d->first_code = CM_ESCROW_IO;
        d->cleanup_stage = 11;
        d->cleanup_code = CM_ESCROW_IO;
        d->cleanup_errno = EINTR;
        d->ownership_flags |= 2;
    }
    return (cm_escrow_result)d->first_code;
#else
    return cm_agent_candidate_measure_unqualified_v1(c, out, d);
#endif
}
static cm_escrow_result owner_candidate_close(cm_agent_candidate_v1 **c,
    cm_agent_candidate_diagnostics_v1 *d)
{
#ifdef CM_OWNER_CONTROLS
    *c = NULL;
    d->ownership_flags &= ~1u;
    if (fault == 14)
    {
        d->cleanup_stage = 12;
        d->cleanup_code = CM_ESCROW_IO;
        d->cleanup_errno = EINTR;
        d->ownership_flags |= 2;
    }
    return (cm_escrow_result)d->first_code;
#else
    return cm_agent_candidate_close_v1(c, d);
#endif
}
typedef struct
{
    int flags;
    dev_t device;
    ino_t inode;
    mode_t mode;
} fd_fact;
static fd_fact snapshots[2][4096];
static void snapshot(int index)
{
    /* Complete bounded local FD set, including flags; out-of-budget refuses. */
    memset(snapshots[index], 0, sizeof(snapshots[index]));
    DIR *directory = opendir("/proc/self/fd");
    assert(directory);
    int own = dirfd(directory);
    struct dirent *entry;
    while ((entry = readdir(directory)))
    {
        char *end;
        long fd = strtol(entry->d_name, &end, 10);
        if (!*end && fd != own)
        {
            assert(fd >= 0 && fd < 4096);
            struct stat st;
            assert(fstat((int)fd, &st) == 0);
            snapshots[index][fd].flags = fcntl((int)fd, F_GETFD) + 1;
            snapshots[index][fd].device = st.st_dev;
            snapshots[index][fd].inode = st.st_ino;
            snapshots[index][fd].mode = st.st_mode;
        }
    }
    assert(closedir(directory) == 0);
}
static char directory[80];
static char endpoint[108];
static struct stat fixture_identity;
static void fixture(void)
{
    strcpy(directory, "/tmp/cm-n1-XXXXXX");
    assert(mkdtemp(directory));
    assert(lstat(directory, &fixture_identity) == 0);
    assert(snprintf(endpoint, sizeof(endpoint), "%s/%s", directory, CM_OWNER_BASENAME) > 0);
    fault = secondary = accept_calls = candidate_calls = close_calls = action = hook_stage = flags_calls = slot_zero = 0;
    reuse_fd = -1;
    live = NULL;
    lifetime = NULL;
    tracked_owner = NULL;
    owner_free_calls = 0;
    cases++;
}
static void teardown(void)
{
    if (lifetime)
    {
        /* No access to the retained owner is needed before this event check. */
        assert(owner_free_calls == 0); /* LIFETIME_RETAINED */
        assert(((cm_connection_owner *)lifetime)->closed);
        cm_owner_diagnostics final;
        assert(!cm_connection_destroy(&lifetime, NULL) && lifetime);
        cm_connection_destroy(&lifetime, &final);
        assert(!lifetime && owner_free_calls == 1);
        cm_owner_diagnostics saved = final;
        assert(cm_connection_destroy(&lifetime, &final));
        assert(!memcmp(&saved, &final, sizeof(final)));
    }
    struct stat current;
    assert(lstat(directory, &current) == 0);
    assert(current.st_ino == fixture_identity.st_ino && current.st_dev == fixture_identity.st_dev);
    /* Only the enclosing fixture may remove its known endpoint. Adversarial
     * replacements are explicitly inspected and removed by their own test. */
    if (lstat(endpoint, &current) == 0)
    {
        assert(S_ISSOCK(current.st_mode));
        assert(unlink(endpoint) == 0);
    }
    assert(rmdir(directory) == 0);
}
static void creation_failures(void)
{
    for (int f = 1; f <= 7; f++)
    {
        fixture();
        snapshot(0);
        fault = f;
        secondary = f == 7;
        cm_owner_diagnostics d;
        assert(!cm_connection_create(directory, strlen(directory), &live, &lifetime, &d));
        assert(!live && d.first && candidate_calls == 0);
        if (secondary)
        {
            assert(d.cleanup == CM_OWNER_CLOSE_LISTENER && d.uncertain);
            assert(fcntl(reuse_fd, F_GETFD) >= 0); /* CLOSE_RETRY */
            assert(close(reuse_fd) == 0);
        }
        snapshot(1);
        assert(!memcmp(snapshots[0], snapshots[1], sizeof(snapshots[0])));
        teardown();
    }
}
static void path_cases(void)
{
    for (int kind = 0; kind < 3; kind++)
    {
        fixture();
        int fd = -1;
        if (kind == 0)
        {
            fd = open(endpoint, O_CREAT | O_EXCL | O_WRONLY, 0600);
            assert(fd >= 0 && write(fd, "keep", 4) == 4 && close(fd) == 0);
        }
        else if (kind == 1)
        {
            assert(symlink("absent", endpoint) == 0);
        }
        else
        {
            fd = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
            struct sockaddr_un a = { .sun_family = AF_UNIX };
            strcpy(a.sun_path, endpoint);
            assert(bind(fd, (struct sockaddr *)&a, sizeof(a)) == 0);
            assert(listen(fd, 1) == 0);
        }
        struct stat before;
        struct stat after;
        assert(lstat(endpoint, &before) == 0);
        cm_owner_diagnostics d;
        assert(!cm_connection_create(directory, strlen(directory), &live, &lifetime, &d));
        assert(lstat(endpoint, &after) == 0 && before.st_ino == after.st_ino && before.st_mode == after.st_mode);
        if (kind == 0)
        {
            char bytes[4];
            fd = open(endpoint, O_RDONLY);
            assert(read(fd, bytes, 4) == 4 && !memcmp(bytes, "keep", 4));
        }
        if (fd >= 0)
        {
            assert(close(fd) == 0);
        }
        assert(unlink(endpoint) == 0);
        teardown();
    }
}
static void swapped_directory(void)
{
    fixture();
    fault = 21;
    cm_owner_diagnostics d;
    assert(!cm_connection_create(directory, strlen(directory), &live, &lifetime, &d));
    assert(!live && d.first == CM_OWNER_PATH);
    struct stat replacement;
    assert(lstat(endpoint, &replacement) == 0 && S_ISSOCK(replacement.st_mode));
    /* This fixture deliberately owns BOTH directories. Normal close did not
     * unlink the replacement socket. No claim about rename-and-revert races. */
    assert(unlink(endpoint) == 0);
    assert(rmdir(directory) == 0);
    assert(rename(moved_directory, directory) == 0);
    teardown();
}
static void arguments(void)
{
    fixture();
    cm_owner_diagnostics d;
    assert(!cm_connection_create(directory, strlen(directory), &live, &lifetime, NULL));
    assert(!cm_connection_create(directory, strlen(directory), &live, NULL, &d));
    assert(!cm_connection_create(directory, strlen(directory), (cm_connection_owner **)&d, &lifetime, &d));
    char nul[] = "/tmp\0other";
    assert(!cm_connection_create(nul, sizeof(nul) - 1, &live, &lifetime, &d));
    assert(!live);
    assert(cm_connection_create(directory, strlen(directory), &live, &lifetime, &d));
    tracked_owner = live;
    assert(!cm_connection_close(&live, NULL) && live);
    assert(!cm_connection_measure(live, (cm_original_measurement_v1 *)&d, &d));
    assert(cm_connection_close(&live, &d) && !live);
    cm_owner_diagnostics saved = d;
    assert(cm_connection_close(&live, &d) && !memcmp(&saved, &d, sizeof(d)));
    assert(!cm_connection_attempt(live, &d));
    teardown();
}
static void premature_destruction(void)
{
    fixture();
    cm_owner_diagnostics d;
    assert(cm_connection_create(directory, strlen(directory), &live, &lifetime, &d));
    tracked_owner = live;
    assert(!cm_connection_destroy(&lifetime, &d));
    assert(lifetime && live && owner_free_calls == 0 && d.first == CM_OWNER_DESTROY);
    assert(!cm_connection_attempt(live, &d) && accept_calls == 0);
    cm_connection_close(&live, &d);
    teardown();
}
#ifdef CM_OWNER_CONTROLS
typedef struct
{
    cm_connection_owner *token;
    cm_owner_diagnostics diagnostics;
    int closing;
    int result;
    int measuring;
    cm_original_measurement_v1 value;
} contender;
static void *contend(void *context)
{
    contender *c = context;
    if (c->closing)
    {
        c->result = cm_connection_close(&c->token, &c->diagnostics);
    }
    else if (c->measuring)
    {
        c->result = cm_connection_measure(c->token, &c->value, &c->diagnostics);
    }
    else
    {
        c->result = cm_connection_attempt(c->token, &c->diagnostics);
    }
    return NULL;
}
static void concurrency(void)
{
    for (int closing = 0; closing < 2; closing++)
    {
        fixture();
        cm_owner_diagnostics d;
        assert(cm_connection_create(directory, strlen(directory), &live, &lifetime, &d));
        tracked_owner = live;
        assert(pthread_mutex_lock(&live->gate) == 0);
        contender c = { .token = live, .closing = closing };
        pthread_t thread;
        assert(pthread_create(&thread, NULL, contend, &c) == 0);
        if (closing)
        {
            /* Retained live-owner pin, alarm-bounded. Close cannot free until
             * the active synchronous holder unlocks; no close-by-cancellation. */
            while (!atomic_load(&live->revoked))
            {
                sched_yield();
            }
            assert(live->listener >= 0 && fcntl(live->listener, F_GETFD) >= 0);
            assert(pthread_mutex_unlock(&live->gate) == 0);
            assert(pthread_join(thread, NULL) == 0);
            assert(!c.token);
            live = NULL;
        }
        else
        {
            assert(pthread_join(thread, NULL) == 0);
            assert(!c.result && c.diagnostics.first == CM_OWNER_REVOKED);
            assert(pthread_mutex_unlock(&live->gate) == 0);
            assert(!cm_connection_attempt(live, &d) && accept_calls == 0);
            cm_connection_close(&live, &d);
        }
        teardown();
    }
}
static void delayed_busy_completion(void)
{
    fixture();
    cm_owner_diagnostics d;
    assert(cm_connection_create(directory, strlen(directory), &live, &lifetime, &d));
    tracked_owner = live;
    atomic_store(&pause_busy, 1);
    atomic_store(&busy_observed, 0);
    atomic_store(&resume_busy, 0);
    assert(pthread_mutex_lock(&live->gate) == 0);
    contender pending = { .token = live, .measuring = 1 };
    memset(&pending.value, 0xa5, sizeof(pending.value));
    pthread_t worker;
    assert(pthread_create(&worker, NULL, contend, &pending) == 0);
    while (!atomic_load(&busy_observed))
    {
        sched_yield();
    }
    assert(pthread_mutex_unlock(&live->gate) == 0);
    /* Complete resource close while an already-entered contender is paused.
     * Storage stays retained by the separate token; no freed-memory access is
     * needed to establish the required ordering. */
    assert(cm_connection_close(&live, &d));
    assert(!live && lifetime && owner_free_calls == 0);
    atomic_store(&resume_busy, 1);
    assert(pthread_join(worker, NULL) == 0);
    atomic_store(&pause_busy, 0);
    assert(!pending.result && pending.diagnostics.first == CM_OWNER_REVOKED);
    cm_original_measurement_v1 zero = {0};
    assert(!memcmp(&pending.value, &zero, sizeof(zero)));
    teardown();
}
static void deterministic(void)
{
    for (int f = 0; f <= 20; f++)
    {
        if (f > 0 && f < 8)
        {
            continue;
        }
        fixture();
        snapshot(0);
        cm_owner_diagnostics d;
        assert(cm_connection_create(directory, strlen(directory), &live, &lifetime, &d));
        tracked_owner = live;
        fault = f;
        int ok = cm_connection_attempt(live, &d);
        assert(accept_calls == 1);
        if (f == 20)
        {
            assert(!ok && "CLOEXEC_SENSITIVITY");
        }
        else if (f == 8 || f == 9 || f == 10 || f == 11 || f == 18 || f == 19)
        {
            assert(!ok);
        }
        else
        {
            assert(ok);
            cm_original_measurement_v1 value;
            int measured = cm_connection_measure(live, &value, &d);
            assert(measured == (f != 12 && f != 13));
            if (!measured)
            {
                cm_original_measurement_v1 zero = {0};
                assert(!memcmp(&value, &zero, sizeof(value))); /* REVOKE_PUBLICATION */
            }
        }
        cm_agent_candidate_diagnostics_v1 saved = d.candidate;
        assert(!cm_connection_attempt(live, &d));
        assert(accept_calls == 1); /* ACCEPT_ONCE */
        secondary = 1;
        cm_connection_close(&live, &d);
        assert(!live);
        printf("fault=%d owner=%u/%d cleanup=%u/%d uncertain=%u candidate=%u/%u/%d secondary=%u/%u/%d flags=%u\n",
            f, (unsigned)d.first, d.first_errno, (unsigned)d.cleanup, d.cleanup_errno,
            d.uncertain, d.candidate.first_stage, d.candidate.first_code,
            d.candidate.first_errno, d.candidate.cleanup_stage, d.candidate.cleanup_code,
            d.candidate.cleanup_errno, d.candidate.ownership_flags);
        assert(d.candidate.first_stage == saved.first_stage);
        if (f == 13 || f == 14)
        {
            assert(d.candidate.cleanup_code == CM_ESCROW_IO); /* CANDIDATE_SECONDARY */
        }
        assert(d.cleanup == CM_OWNER_CLOSE_ACCEPTED || d.cleanup == CM_OWNER_CLOSE_LISTENER);
        assert(fcntl(reuse_fd, F_GETFD) >= 0); /* CLOSE_RETRY */
        assert(close(reuse_fd) == 0);
        snapshot(1);
        assert(!memcmp(snapshots[0], snapshots[1], sizeof(snapshots[0])));
        teardown();
    }
    for (int point = 1; point <= 4; point++)
    {
        for (int a = 1; a <= 3; a++)
        {
            fixture();
            cm_owner_diagnostics d;
            cm_original_measurement_v1 value;
            assert(cm_connection_create(directory, strlen(directory), &live, &lifetime, &d));
            tracked_owner = live;
            if (point == 3)
            {
                assert(cm_connection_attempt(live, &d));
            }
            hook_stage = point;
            action = a;
            if (point == 3)
            {
                assert(!cm_connection_measure(live, &value, &d));
                cm_original_measurement_v1 zero = {0};
                assert(!memcmp(&value, &zero, sizeof(value))); /* REVOKE_PUBLICATION */
            }
            else
            {
                assert(!cm_connection_attempt(live, &d));
            }
            assert(d.first == CM_OWNER_REVOKED);
            cm_connection_close(&live, &d);
            teardown();
        }
    }
    for (int slot = 1; slot <= 3; slot++)
    {
        fixture();
        snapshot(0);
        int saved = fcntl(0, F_DUPFD_CLOEXEC, 3);
        assert(saved >= 0);
        slot_zero = slot;
        cm_owner_diagnostics d;
        assert(cm_connection_create(directory, strlen(directory), &live, &lifetime, &d));
        tracked_owner = live;
        assert(cm_connection_attempt(live, &d));
        assert((slot == 1 ? live->directory : slot == 2 ? live->listener : live->accepted) == 0);
        assert(cm_connection_close(&live, &d));
        assert(fcntl(0, F_GETFD) < 0 && errno == EBADF); /* FD_ZERO */
        assert(dup2(saved, 0) == 0 && close(saved) == 0);
        snapshot(1);
        assert(!memcmp(snapshots[0], snapshots[1], sizeof(snapshots[0])));
        teardown();
    }
}
#else
static pid_t client(int ready[2], int stop[2], int inherited)
{
    /* Fork before owner/listener/originals exist. Child owns no success-test
     * listener or original. Pipe ACK establishes completed connect + sentinel. */
    assert(pipe2(ready, O_CLOEXEC) == 0 && pipe2(stop, O_CLOEXEC) == 0);
    pid_t pid = fork();
    assert(pid >= 0);
    if (pid == 0)
    {
        alarm(10);
        close(ready[0]);
        close(stop[1]);
        char go;
        assert(read(stop[0], &go, 1) == 1);
        int fd = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
        struct sockaddr_un a = { .sun_family = AF_UNIX };
        strcpy(a.sun_path, endpoint);
        assert(connect(fd, (struct sockaddr *)&a, sizeof(a)) == 0);
        if (inherited)
        {
            pid_t writer = fork();
            assert(writer >= 0);
            alarm(10);
            if (writer > 0)
            {
                close(fd);
                int status;
                assert(waitpid(writer, &status, 0) == writer && WIFEXITED(status) && WEXITSTATUS(status) == 0);
                _exit(0);
            }
        }
        assert(write(fd, "sentinel", 8) == 8);
        pid_t writer_pid = getpid();
        assert(write(ready[1], &writer_pid, sizeof(writer_pid)) == (ssize_t)sizeof(writer_pid));
        assert(read(stop[0], &go, 1) == 1);
        close(fd);
        _exit(0);
    }
    close(ready[1]);
    close(stop[0]);
    return pid;
}
static void kernel(int inherited)
{
    fixture();
    snapshot(0);
    int ready[2];
    int stop[2];
    pid_t pid = client(ready, stop, inherited);
    cm_owner_diagnostics d;
    assert(cm_connection_create(directory, strlen(directory), &live, &lifetime, &d));
    tracked_owner = live;
    assert(write(stop[1], "G", 1) == 1);
    pid_t writer_pid;
    struct pollfd p = { .fd = ready[0], .events = POLLIN };
    assert(poll(&p, 1, 5000) == 1 && read(ready[0], &writer_pid, sizeof(writer_pid)) == (ssize_t)sizeof(writer_pid));
    assert(cm_connection_attempt(live, &d));
    assert(candidate_calls == 1 && accept_calls == 1);
    struct ucred peer;
    socklen_t peer_size = sizeof(peer);
    assert(getsockopt(live->accepted, SOL_SOCKET, SO_PEERCRED, &peer, &peer_size) == 0);
    assert(peer.pid == pid);
    assert(inherited ? writer_pid != peer.pid : writer_pid == peer.pid);
    if (inherited)
    {
        puts("EXPECTED LIMITATION: inherited writer differs from connection-time peer; UNQUALIFIED");
    }
    assert(flags(live->accepted) && flags(live->listener)); /* CLOEXEC */
    cm_original_measurement_v1 value;
    assert(cm_connection_measure(live, &value, &d));
    assert(value.inode && value.flags && value.source == 1);
    char bytes[8];
    assert(recv(live->accepted, bytes, sizeof(bytes), MSG_PEEK) == 8);
    assert(!memcmp(bytes, "sentinel", 8));
    /* Replace endpoint with attacker-owned bytes; close must preserve them. */
    assert(unlink(endpoint) == 0);
    int replacement = open(endpoint, O_CREAT | O_EXCL | O_WRONLY, 0600);
    assert(replacement >= 0 && write(replacement, "keep", 4) == 4);
    assert(close(replacement) == 0);
    assert(cm_connection_close(&live, &d));
    struct stat st;
    assert(lstat(endpoint, &st) == 0 && S_ISREG(st.st_mode)); /* REPLACEMENT_UNLINK */
    replacement = open(endpoint, O_RDONLY);
    assert(read(replacement, bytes, 4) == 4 && !memcmp(bytes, "keep", 4));
    assert(close(replacement) == 0 && unlink(endpoint) == 0);
    assert(write(stop[1], "S", 1) == 1);
    int status;
    assert(waitpid(pid, &status, 0) == pid && WIFEXITED(status) && WEXITSTATUS(status) == 0);
    close(ready[0]);
    close(stop[1]);
    snapshot(1);
    assert(!memcmp(snapshots[0], snapshots[1], sizeof(snapshots[0])));
    teardown();
    puts("kernel direct accept/measure/sentinel/replacement/local FD restoration PASS; UNQUALIFIED");
}
#endif
int main(void)
{
    alarm(60);
    arguments();
    premature_destruction();
    creation_failures();
    path_cases();
    swapped_directory();
#ifdef CM_OWNER_CONTROLS
    concurrency();
    delayed_busy_completion();
    deterministic();
    printf("deterministic cases=%u ownership/failure/revoke/fd0/lifetime assertions PASS; UNQUALIFIED\n", cases);
#else
    kernel(0);
    kernel(1);
    printf("kernel cases=%u PASS; inherited-writer case is an expected UNQUALIFIED limitation\n", cases);
#endif
    return 0;
}
