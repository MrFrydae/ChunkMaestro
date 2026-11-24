#ifndef CM_POLICY_CONTROLS_H
#define CM_POLICY_CONTROLS_H
#define _GNU_SOURCE
#include <fcntl.h>
#include <linux/openat2.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <sys/xattr.h>
#include <unistd.h>
#include <stdlib.h>
ssize_t cm_policy_pread(int, void *, size_t, off_t);
int cm_policy_fstat(int, struct stat *);
int cm_policy_fcntl(int, int, ...);
long cm_policy_openat2(long, int, const char *, const struct open_how *, size_t);
ssize_t cm_policy_xattr(int, const char *, void *, size_t);
void *cm_policy_calloc(size_t, size_t);
int cm_policy_close(int);
#ifdef CM_POLICY_INTERPOSE
#define pread cm_policy_pread
#define fstat cm_policy_fstat
#define fcntl cm_policy_fcntl
#define syscall cm_policy_openat2
#define fgetxattr cm_policy_xattr
#define calloc cm_policy_calloc
#define close cm_policy_close
#ifdef CM_POLICY_BRIDGE_INTERPOSE
#define cm_policy_snapshot_v1_take cm_policy_inner_take
#define cm_policy_snapshot_v1_copy cm_policy_inner_copy
#define cm_policy_snapshot_v1_facts cm_policy_inner_facts
#define cm_policy_snapshot_v1_check cm_policy_inner_check
#define cm_policy_snapshot_v1_destroy cm_policy_inner_destroy
#endif
#endif
#endif
