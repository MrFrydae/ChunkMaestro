#ifndef CM_PEER_SYSCALL_CONTROLS_H
#define CM_PEER_SYSCALL_CONTROLS_H
#ifndef _GNU_SOURCE
#define _GNU_SOURCE
#endif
#include <sys/socket.h>
int cm_peer_test_getsockopt(int fd, int level, int option, void *value, socklen_t *length);
int cm_peer_test_getsockname(int fd, struct sockaddr *address, socklen_t *length);
int cm_peer_test_getpeername(int fd, struct sockaddr *address, socklen_t *length);
#endif
