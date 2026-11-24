#define _GNU_SOURCE
#include "peer.h"
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <sys/wait.h>
#include <unistd.h>

#define REQUIRE(expression) do \
{ \
    if (!(expression)) \
    { \
        fprintf(stderr, "FAIL line %d: %s (errno=%d)\n", __LINE__, #expression, errno); \
        exit(1); \
    } \
} while (0)

typedef struct
{
    size_t count;
    int numbers[4096];
} descriptor_snapshot;

static int descriptor_order(const void *left, const void *right)
{
    int a = *(const int *)left;
    int b = *(const int *)right;
    return (a > b) - (a < b);
}

/* Complete descriptor-number set, excluding only this enumeration's own FD. */
static descriptor_snapshot descriptors(void)
{
    descriptor_snapshot value = {0};
    DIR *directory = opendir("/proc/self/fd");
    REQUIRE(directory != NULL);
    int scan = dirfd(directory);
    REQUIRE(scan >= 0);
    struct dirent *entry;
    errno = 0;
    while ((entry = readdir(directory)) != NULL)
    {
        if (entry->d_name[0] == '.')
        {
            continue;
        }
        char *end = NULL;
        errno = 0;
        long number = strtol(entry->d_name, &end, 10);
        REQUIRE(errno == 0 && *end == 0 && number >= 0 && number <= INT_MAX);
        if ((int)number != scan)
        {
            REQUIRE(value.count < sizeof(value.numbers) / sizeof(value.numbers[0]));
            value.numbers[value.count++] = (int)number;
        }
        errno = 0;
    }
    REQUIRE(errno == 0);
    REQUIRE(closedir(directory) == 0);
    qsort(value.numbers, value.count, sizeof(value.numbers[0]), descriptor_order);
    return value;
}

static void same_descriptors(const descriptor_snapshot *before)
{
    descriptor_snapshot after = descriptors();
    REQUIRE(before->count == after.count);
    REQUIRE(memcmp(before->numbers, after.numbers, after.count * sizeof(after.numbers[0])) == 0);
}

static void facts(int fd, pid_t pid)
{
    cm_peer_facts value;
    memset(&value, 0x5a, sizeof(value));
    cm_peer_result status = cm_peer_query(fd, &value);
    REQUIRE(status.state == CM_PEER_FACTS && status.error == 0);
    REQUIRE(value.pid == pid && value.uid == getuid() && value.gid == getgid());
    REQUIRE(fcntl(fd, F_GETFD) >= 0);
}

static void refuses(int fd, cm_peer_state expected, int error)
{
    cm_peer_facts value;
    memset(&value, 0x5a, sizeof(value));
    cm_peer_result status = cm_peer_query(fd, &value);
    REQUIRE(status.state == expected && status.error == error);
    REQUIRE(value.pid == 0 && value.uid == 0 && value.gid == 0);
}

#ifdef CM_PEER_CONTROLS
#include "peer_syscall_controls.h"
static int call_number;
static int target_call;
static int injected_error;
static int shape;

static int control(socklen_t *length)
{
    ++call_number;
    if (call_number != target_call)
    {
        return 0;
    }
    if (injected_error != 0)
    {
        errno = injected_error;
        return -1;
    }
    if (shape == 1)
    {
        *length = 0;
    }
    if (shape == 2)
    {
        *length = (socklen_t)4096;
    }
    return 1;
}

int cm_peer_test_getsockopt(int fd, int level, int option, void *value, socklen_t *length)
{
    REQUIRE(level == SOL_SOCKET);
    REQUIRE(option == SO_TYPE || option == SO_ACCEPTCONN || option == SO_PEERCRED);
    int action = control(length);
    if (action != 0)
    {
        if (action > 0 && option == SO_PEERCRED && shape >= 3)
        {
            struct ucred *credentials = value;
            credentials->pid = shape == 3 ? 0 : (shape == 4 ? -1 : getpid());
            credentials->uid = shape == 5 ? (uid_t)-1 : (shape == 7 ? 0 : getuid());
            credentials->gid = shape == 6 ? (gid_t)-1 : (shape == 7 ? 0 : getgid());
        }
        return action < 0 ? -1 : 0;
    }
    return getsockopt(fd, level, option, value, length);
}

int cm_peer_test_getsockname(int fd, struct sockaddr *address, socklen_t *length)
{
    int action = control(length);
    return action == 0 ? getsockname(fd, address, length) : (action < 0 ? -1 : 0);
}

int cm_peer_test_getpeername(int fd, struct sockaddr *address, socklen_t *length)
{
    int action = control(length);
    return action == 0 ? getpeername(fd, address, length) : (action < 0 ? -1 : 0);
}

static void controls(int fd)
{
    const int errors[] = {EINTR, EACCES, ENOPROTOOPT};
    for (target_call = 1; target_call <= 5; ++target_call)
    {
        for (size_t index = 0; index < sizeof(errors) / sizeof(errors[0]); ++index)
        {
            injected_error = errors[index];
            call_number = 0;
            refuses(fd, CM_PEER_UNAVAILABLE, injected_error);
            REQUIRE(call_number == target_call); /* No retry or fallback. */
        }
        injected_error = 0;
        for (shape = 1; shape <= 2; ++shape)
        {
            call_number = 0;
            refuses(fd, CM_PEER_INVALID, 0);
            REQUIRE(call_number == target_call);
        }
    }
    target_call = 5;
    for (shape = 3; shape <= 6; ++shape)
    {
        call_number = 0;
        refuses(fd, CM_PEER_INVALID, 0);
        REQUIRE(call_number == 5);
    }
    /* Pure result control proves absence of a root-policy decision, without
     * assuming another identity. This is not a real root peer test. */
    shape = 7;
    call_number = 0;
    cm_peer_facts root_facts;
    cm_peer_result root_result = cm_peer_query(fd, &root_facts);
    REQUIRE(root_result.state == CM_PEER_FACTS && root_result.error == 0);
    REQUIRE(root_facts.pid == getpid() && root_facts.uid == 0 && root_facts.gid == 0);
    REQUIRE(call_number == 5);
    target_call = 0;
    facts(fd, getpid());
}
#else
static void child_ok(pid_t child)
{
    int status = 0;
    REQUIRE(waitpid(child, &status, 0) == child);
    REQUIRE(WIFEXITED(status) && WEXITSTATUS(status) == 0);
}

static void kernel_cases(int *pair)
{
    pid_t parent = getpid();
    pid_t child = fork();
    REQUIRE(child >= 0);
    if (child == 0)
    {
        /* Inheritance does not change socketpair-time credentials to child PID. */
        facts(pair[0], parent);
        facts(pair[1], parent);
        exit(0);
    }
    child_ok(child);
    char directory[] = "/tmp/cm-peer-XXXXXX";
    REQUIRE(mkdtemp(directory) != NULL);
    struct sockaddr_un address;
    memset(&address, 0, sizeof(address));
    address.sun_family = AF_UNIX;
    REQUIRE(snprintf(address.sun_path, sizeof(address.sun_path), "%s/peer.sock", directory) > 0);
    int listener = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
    REQUIRE(listener >= 0);
    REQUIRE(bind(listener, (struct sockaddr *)&address, sizeof(address)) == 0);
    refuses(listener, CM_PEER_INVALID, ENOTCONN);
    REQUIRE(listen(listener, 1) == 0);
    refuses(listener, CM_PEER_INVALID, 0);
    int ack[2];
    REQUIRE(pipe(ack) == 0);
    child = fork();
    REQUIRE(child >= 0);
    if (child == 0)
    {
        REQUIRE(close(ack[1]) == 0);
        REQUIRE(close(listener) == 0);
        int client = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
        REQUIRE(client >= 0);
        REQUIRE(connect(client, (struct sockaddr *)&address, sizeof(address)) == 0);
        facts(client, parent); /* Listener listen-time credentials, no systemd claim. */
        REQUIRE(write(client, "S", 1) == 1);
        char byte;
        REQUIRE(read(ack[0], &byte, 1) == 1);
        REQUIRE(close(client) == 0);
        REQUIRE(close(ack[0]) == 0);
        exit(0);
    }
    REQUIRE(close(ack[0]) == 0);
    int accepted = accept4(listener, NULL, NULL, SOCK_CLOEXEC);
    REQUIRE(accepted >= 0);
    facts(accepted, child);
    char sentinel = 0;
    REQUIRE(read(accepted, &sentinel, 1) == 1 && sentinel == 'S');
    REQUIRE(write(accepted, "R", 1) == 1); /* Still caller-owned and usable. */
    REQUIRE(write(ack[1], "A", 1) == 1);
    child_ok(child);
    REQUIRE(close(ack[1]) == 0);
    REQUIRE(close(accepted) == 0);
    REQUIRE(close(listener) == 0);
    REQUIRE(unlink(address.sun_path) == 0);
    REQUIRE(rmdir(directory) == 0);

    int types[] = {SOCK_STREAM, SOCK_DGRAM, SOCK_SEQPACKET};
    for (size_t index = 0; index < sizeof(types) / sizeof(types[0]); ++index)
    {
        int fd = socket(AF_UNIX, types[index] | SOCK_CLOEXEC, 0);
        REQUIRE(fd >= 0);
        refuses(fd, CM_PEER_INVALID, index == 0 ? ENOTCONN : 0);
        REQUIRE(close(fd) == 0);
        if (index != 0)
        {
            int other[2];
            REQUIRE(socketpair(AF_UNIX, types[index], 0, other) == 0);
            refuses(other[0], CM_PEER_INVALID, 0);
            REQUIRE(close(other[0]) == 0 && close(other[1]) == 0);
        }
    }
    int tcp = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, 0);
    REQUIRE(tcp >= 0);
    refuses(tcp, CM_PEER_INVALID, 0); /* No TCP connection/network traffic. */
    REQUIRE(close(tcp) == 0);
    int file = open("/dev/null", O_RDONLY | O_CLOEXEC);
    REQUIRE(file >= 0);
    refuses(file, CM_PEER_INVALID, ENOTSOCK);
    REQUIRE(close(file) == 0);
    refuses(file, CM_PEER_INVALID, EBADF); /* No intervening descriptor creation. */
}
#endif

int main(void)
{
    REQUIRE(getuid() == geteuid() && getgid() == getegid());
    descriptor_snapshot initial = descriptors();
    int pair[2];
    REQUIRE(socketpair(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0, pair) == 0);
    facts(pair[0], getpid());
    REQUIRE(cm_peer_query(pair[0], NULL).state == CM_PEER_INVALID);
    refuses(-1, CM_PEER_INVALID, 0);
    REQUIRE(write(pair[1], "P", 1) == 1);
    descriptor_snapshot before = descriptors();
    for (int index = 0; index < 1000; ++index)
    {
        facts(pair[0], getpid());
        refuses(-1, CM_PEER_INVALID, 0);
    }
    same_descriptors(&before);
    char byte = 0;
    REQUIRE(read(pair[0], &byte, 1) == 1 && byte == 'P');
#ifdef CM_PEER_CONTROLS
    controls(pair[0]);
#else
    kernel_cases(pair);
#endif
    REQUIRE(close(pair[0]) == 0 && close(pair[1]) == 0);
    same_descriptors(&initial);
    puts("PASS: peer facts boundary (not authentication, policy or systemd qualification)");
    return 0;
}
