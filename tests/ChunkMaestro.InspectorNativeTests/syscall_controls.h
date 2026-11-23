#ifndef CM_SYSCALL_CONTROLS_H
#define CM_SYSCALL_CONTROLS_H
#include <linux/openat2.h>
#include <sys/types.h>
ssize_t cm_test_fgetxattr(int fd, const char *name, void *buffer, size_t size);
int cm_test_openat2(int fd, const char *name, const struct open_how *how);
#endif
