#ifndef CM_ESCROW_CONTROLS_H
#define CM_ESCROW_CONTROLS_H
#define _GNU_SOURCE
#include <fcntl.h>
#include <poll.h>
#include <sys/stat.h>
#include <sys/vfs.h>
#include <time.h>
#include <unistd.h>
/* Compiler-only interposition; production has no injectable function table. */
int cm_control_fcntl(int, int, ...);
int cm_control_fstat(int, struct stat *);
int cm_control_fstatfs(int, struct statfs *);
int cm_control_close(int);
int cm_control_poll(struct pollfd *, nfds_t, int);
int cm_control_clock(clockid_t, struct timespec *);
#ifdef CM_ESCROW_INTERPOSE
#define fcntl cm_control_fcntl
#define fstat cm_control_fstat
#define fstatfs cm_control_fstatfs
#define close cm_control_close
#define poll cm_control_poll
#define clock_gettime cm_control_clock
#endif
#endif
