#ifndef CM_AGENT_CANDIDATE_CONTROLS_H
#define CM_AGENT_CANDIDATE_CONTROLS_H
#define _GNU_SOURCE
#include <fcntl.h>
#include <poll.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/vfs.h>
#include <unistd.h>
#include <stdlib.h>
void *cm_candidate_calloc(size_t, size_t);
#define calloc cm_candidate_calloc
int cm_candidate_fcntl(int, int, ...);
int cm_candidate_fstat(int, struct stat *);
int cm_candidate_fstatfs(int, struct statfs *);
int cm_candidate_close(int);
int cm_candidate_poll(struct pollfd *, nfds_t, int);
int cm_candidate_getsockopt(int, int, int, void *, socklen_t *);
int cm_candidate_getname(int, struct sockaddr *, socklen_t *);
#define fcntl cm_candidate_fcntl
#define fstat cm_candidate_fstat
#define fstatfs cm_candidate_fstatfs
#define close cm_candidate_close
#define poll cm_candidate_poll
#define getsockopt cm_candidate_getsockopt
#define getsockname cm_candidate_getname
#define getpeername cm_candidate_getname
#endif
