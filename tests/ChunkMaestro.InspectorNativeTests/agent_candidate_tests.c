#define _GNU_SOURCE
#include "session_escrow.h"
#include <assert.h>
#include <errno.h>
#include <dirent.h>
#include <signal.h>
#include <fcntl.h>
#include <poll.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/vfs.h>
#include <sys/un.h>
#include <sys/wait.h>
#include <unistd.h>
#ifndef SO_PEERPIDFD
#define SO_PEERPIDFD 77
#endif
#ifdef CM_CANDIDATE_CONTROLS
static int fault, close_fault, acquired_fd, acquires, closes, duplicates;
static int profile_fault, measure_fault;
void *cm_candidate_calloc(size_t count, size_t size)
{
    if (fault == 17)
    {
        errno = ENOMEM;
        return NULL;
    }
    return calloc(count, size);
}
int sd_pid_notify_with_fds(pid_t p, int u, const char *s, const int *f, unsigned n)
{
    (void)p; (void)u; (void)s; (void)f; (void)n;
    abort();
}
int sd_notify_barrier(int u, uint64_t t)
{
    (void)u; (void)t;
    abort();
}
int sd_listen_fds_with_names(int u, char ***n)
{
    (void)u; (void)n;
    abort();
}
int cm_candidate_getname(int fd, struct sockaddr *a, socklen_t *n)
{
    assert(fd == 19);
    a->sa_family = AF_UNIX;
    *n = sizeof(sa_family_t);
    return 0;
}
int cm_candidate_getsockopt(int fd, int level, int option, void *out, socklen_t *n)
{
    assert(fd == 19 && level == SOL_SOCKET);
    if (option == SO_PEERCRED)
    {
        struct ucred value = { .pid = 7, .uid = 1000, .gid = 1000 };
        if (fault == 15)
        {
            value.pid = 0;
        }
        memcpy(out, &value, sizeof(value));
        *n = sizeof(value);
        return 0;
    }
    assert(*n == sizeof(int));
    if (option == SO_TYPE || option == SO_ACCEPTCONN)
    {
        *(int *)out = option == SO_TYPE ? SOCK_STREAM : 0;
        if (fault == 1 && option == SO_TYPE)
        {
            *(int *)out = SOCK_DGRAM;
        }
        return 0;
    }
    assert(option == SO_PEERPIDFD && *(int *)out == -1);
    acquires++;
    assert(acquires == 1);
    if (fault >= 2 && fault <= 6)
    {
        int errors[] = { ENODATA, ENOPROTOOPT, EMFILE, EINTR, EFAULT };
        errno = errors[fault - 2];
        return -1;
    }
    if (fault == 13 || fault == 19)
    {
        errno = fault == 13 ? EACCES : 0;
        return -1;
    }
    *(int *)out = fault == 9 || fault == 16 ? -1 : acquired_fd;
    if (fault == 14)
    {
        *n = 0;
    }
    if (fault == 7 || fault == 8)
    {
        *n = fault == 7 ? 1 : sizeof(int) + 1;
    }
    return 0;
}
int cm_candidate_fcntl(int fd, int command, ...)
{
    assert(fd == acquired_fd || fd == 42);
    if (command == F_GETFD)
    {
        if (fault == 18)
        {
            errno = EIO;
            return -1;
        }
        return fault == 10 ? 0 : FD_CLOEXEC;
    }
    if (command == F_DUPFD_CLOEXEC)
    {
        duplicates++;
        if (measure_fault == 1)
        {
            errno = EMFILE;
            return -1;
        }
        return 42;
    }
    assert(command == F_GETFL);
    if (profile_fault == 1 || (fd == 42 && measure_fault == 2))
    {
        errno = EIO;
        return -1;
    }
    return profile_fault == 2 ? O_RDWR | O_EXCL : profile_fault == 3 ? O_RDWR | O_APPEND : O_RDWR;
}
int cm_candidate_fstat(int fd, struct stat *out)
{
    assert(fd == acquired_fd || fd == 42);
    memset(out, 0, sizeof(*out));
    out->st_dev = profile_fault == 4 ? 0 : 5;
    out->st_ino = 7;
    return 0;
}
int cm_candidate_fstatfs(int fd, struct statfs *out)
{
    assert(fd == acquired_fd || fd == 42);
    memset(out, 0, sizeof(*out));
    out->f_type = profile_fault == 5 ? 0 : 0x50494446;
    return 0;
}
int cm_candidate_poll(struct pollfd *p, nfds_t n, int timeout)
{
    assert(n == 1 && timeout == 0 && p->fd == acquired_fd);
    p->revents = fault == 11 ? POLLIN : fault == 12 ? POLLHUP : 0;
    return p->revents != 0;
}
int cm_candidate_close(int fd)
{
    assert(fd == acquired_fd || fd == 42);
    closes++;
    // A retry consumes a reused descriptor and is always an assertion failure.
    assert(closes <= duplicates + 1);
    if (close_fault)
    {
        errno = closes == 1 ? EINTR : EIO;
        return -1;
    }
    return 0;
}
static void reset(void)
{
    fault = close_fault = profile_fault = measure_fault = 0;
    acquires = closes = duplicates = 0;
    acquired_fd = 0;
}
static void controls(void)
{
    cm_agent_candidate_diagnostics_v1 d;
    cm_agent_candidate_v1 *s = NULL;
    assert(cm_agent_candidate_acquire_v1(19, &s, NULL) == CM_ESCROW_INVALID);
    assert(acquires == 0 && closes == 0);
    assert(cm_agent_candidate_close_v1(&s, &d) == 0 && d.version == 1);
    for (int f = 1; f <= 19; ++f)
    {
        for (int cleanup = 0; cleanup < 2; ++cleanup)
        {
            reset(); fault = f; close_fault = cleanup;
            assert(cm_agent_candidate_acquire_v1(19, &s, &d) != 0);
            assert(s == NULL && d.first_code != 0 && d.version == 1);
            int known = f == 7 || f == 8 || f == 10 || f == 11 || f == 12 || f == 14 || f == 18;
            assert(closes == known);
            assert(d.cleanup_code == (known && cleanup ? 4u : 0u));
            assert((d.ownership_flags & 4) == (f == 6 || f == 9 || f == 16 || f == 19 ? 4u : 0u));
            assert(acquires == (f == 1 || f == 15 || f == 17 ? 0 : 1));
        }
    }
    for (int profile = 1; profile <= 5; ++profile)
    {
        reset(); profile_fault = profile; close_fault = 1;
        assert(cm_agent_candidate_acquire_v1(19, &s, &d) != 0 && s == NULL);
        assert(d.first_stage == 7 && d.cleanup_stage == 12 && d.cleanup_errno == EINTR);
        assert(d.first_errno == (profile == 1 ? EIO : 0));
        assert(closes == 1);
    }
    for (int failure = 0; failure <= 2; ++failure)
    {
        reset();
        assert(cm_agent_candidate_acquire_v1(19, &s, &d) == 0 && s != NULL);
        assert(d.ownership_flags == 1);
        assert(cm_agent_candidate_close_v1(&s, NULL) == CM_ESCROW_INVALID && s != NULL);
        cm_original_measurement_v1 untouched;
        memset(&untouched, 0x5a, sizeof(untouched));
        assert(cm_agent_candidate_measure_unqualified_v1(s, &untouched, NULL) == CM_ESCROW_INVALID);
        assert(duplicates == 0 && closes == 0);
        measure_fault = failure;
        close_fault = 1;
        cm_original_measurement_v1 value, zero = {0};
        memset(&value, 0xff, sizeof(value));
        assert(cm_agent_candidate_measure_unqualified_v1(s, &value, &d) != 0);
        assert(memcmp(&value, &zero, sizeof(value)) == 0);
        assert(d.first_stage == (failure == 1 ? 9u : failure == 2 ? 10u : 11u));
        assert(d.cleanup_stage == (failure == 1 ? 0u : 11u));
        unsigned first = d.first_stage;
        int before = duplicates;
        assert(cm_agent_candidate_measure_unqualified_v1(s, &value, &d) != 0);
        assert(duplicates == before);
        assert(cm_agent_candidate_close_v1(&s, &d) != 0 && s == NULL);
        assert(d.first_stage == first);
        assert(d.cleanup_stage == (failure == 1 ? 12u : 11u));
        assert(d.cleanup_errno == EINTR && d.ownership_flags == 2);
    }
    reset();
    assert(cm_agent_candidate_acquire_v1(19, &s, &d) == 0);
    cm_original_measurement_v1 value;
    assert(cm_agent_candidate_measure_unqualified_v1(s, &value, &d) == 0);
    assert(value.device == 5 && value.inode == 7 && value.flags == 2 && value.source == 1);
    assert(cm_agent_candidate_close_v1(&s, &d) == 0 && s == NULL && d.ownership_flags == 0);
    assert(closes == 2);
    puts("PASS candidate deterministic controls, fd0, first/cleanup precedence and post-free diagnostics");
}
#else
/* Owned ordinary-user resources only. No manager/service, PID reopen or payload
 * consumption by the candidate. This is kernel reference proof, not Debian qualification. */
typedef struct
{
    int fd;
    dev_t device;
    ino_t inode;
    mode_t mode;
} fd_fact;
static int snapshot(fd_fact facts[256])
{
    DIR *directory = opendir("/proc/self/fd");
    assert(directory);
    int count = 0;
    struct dirent *entry;
    while ((entry = readdir(directory)) != NULL)
    {
        char *end;
        long fd = strtol(entry->d_name, &end, 10);
        if (*end || fd < 0 || fd == dirfd(directory))
        {
            continue;
        }
        assert(count < 256 && fd <= 2147483647);
        struct stat st;
        assert(fstat((int)fd, &st) == 0);
        facts[count++] = (fd_fact){ (int)fd, st.st_dev, st.st_ino, st.st_mode };
    }
    closedir(directory);
    for (int i = 0; i < count; ++i)
    {
        for (int j = i + 1; j < count; ++j)
        {
            if (facts[i].fd > facts[j].fd)
            {
                fd_fact saved = facts[i]; facts[i] = facts[j]; facts[j] = saved;
            }
        }
    }
    return count;
}
static void kernel(int reap_first)
{
    char directory[] = "/tmp/cm-candidate-XXXXXX";
    assert(mkdtemp(directory));
    struct sockaddr_un address = { .sun_family = AF_UNIX };
    assert(snprintf(address.sun_path, sizeof(address.sun_path), "%s/s", directory) > 0);
    int listener = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
    assert(listener >= 0);
    assert(bind(listener, (struct sockaddr *)&address, sizeof(address)) == 0);
    assert(listen(listener, 1) == 0);
    int release[2];
    assert(pipe2(release, O_CLOEXEC) == 0);
    pid_t child = fork();
    assert(child >= 0);
    if (child == 0)
    {
        close(release[1]);
        close(listener);
        int connector = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
        assert(connector >= 0);
        assert(connect(connector, (struct sockaddr *)&address, sizeof(address)) == 0);
        assert(write(connector, "S", 1) == 1);
        char done;
        assert(read(release[0], &done, 1) == 1);
        close(connector);
        close(release[0]);
        _exit(0);
    }
    close(release[0]);
    int accepted = accept4(listener, NULL, NULL, SOCK_CLOEXEC);
    assert(accepted >= 0);
    char sentinel;
    assert(recv(accepted, &sentinel, 1, MSG_PEEK) == 1 && sentinel == 'S');
    int saved_stdin = fcntl(0, F_DUPFD_CLOEXEC, 3);
    assert(saved_stdin >= 0 && close(0) == 0);
    cm_agent_candidate_v1 *s = NULL;
    cm_agent_candidate_diagnostics_v1 d;
    assert(cm_agent_candidate_acquire_v1(accepted, &s, &d) == 0 && s != NULL);
    cm_original_measurement_v1 value, zero = {0};
    assert(cm_agent_candidate_measure_unqualified_v1(s, &value, &d) == 0);
    assert(fcntl(0, F_GETFD) == FD_CLOEXEC);
    assert(value.device != 0 && value.inode != 0 && value.source == 1);
    assert(recv(accepted, &sentinel, 1, MSG_PEEK) == 1 && sentinel == 'S');
    assert(write(release[1], "D", 1) == 1);
    int status;
    siginfo_t information;
    memset(&information, 0, sizeof(information));
    assert(waitid(P_PID, (id_t)child, &information, WEXITED | WNOWAIT) == 0);
    if (reap_first)
    {
        assert(waitpid(child, &status, 0) == child && WIFEXITED(status) && WEXITSTATUS(status) == 0);
    }
    assert(cm_agent_candidate_measure_unqualified_v1(s, &value, &d) != 0);
    assert(memcmp(&value, &zero, sizeof(value)) == 0 && d.first_stage == 8);
    assert(cm_agent_candidate_close_v1(&s, &d) != 0 && s == NULL);
    assert(d.ownership_flags == 0 && d.cleanup_code == 0);
    if (!reap_first)
    {
        assert(waitpid(child, &status, 0) == child && WIFEXITED(status) && WEXITSTATUS(status) == 0);
    }
    assert(fcntl(0, F_GETFD) == -1 && errno == EBADF);
    assert(dup2(saved_stdin, 0) == 0 && close(saved_stdin) == 0);
    assert(read(accepted, &sentinel, 1) == 1 && sentinel == 'S');
    close(accepted); close(listener); close(release[1]);
    assert(unlink(address.sun_path) == 0 && rmdir(directory) == 0);
    puts("PASS candidate real owned peer/liveness/no payload consumption; PID reuse NOTRUN");
}
#endif
int main(void)
{
    assert(sizeof(cm_agent_candidate_diagnostics_v1) == 32);
    assert(_Alignof(cm_agent_candidate_diagnostics_v1) == 4);
#ifdef CM_CANDIDATE_CONTROLS
    controls();
#else
    fd_fact before[256], after[256];
    int initial = snapshot(before);
    for (int repetition = 0; repetition < 8; ++repetition)
    {
        kernel(repetition % 2);
        int final = snapshot(after);
        assert(initial == final);
        for (int i = 0; i < initial; ++i)
        {
            assert(before[i].fd == after[i].fd && before[i].device == after[i].device &&
                before[i].inode == after[i].inode && before[i].mode == after[i].mode);
        }
    }
#endif
    return 0;
}
