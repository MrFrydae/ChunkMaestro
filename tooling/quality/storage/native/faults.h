/* Test-only interposition. Never linked into a product artifact. */
#include <sys/stat.h>
#include <sys/acl.h>
int cm_fault_lstat(const char *, struct stat *);
int cm_fault_tag(acl_entry_t, acl_tag_t *);
int cm_fault_entry(acl_t, int, acl_entry_t *);
int cm_fault_free(void *);
acl_t cm_fault_acl(const char *, acl_type_t);
#ifndef CM_FAULT_IMPLEMENTATION
#define lstat cm_fault_lstat
#define acl_get_file cm_fault_acl
#define acl_get_tag_type cm_fault_tag
#define acl_get_entry cm_fault_entry
#define acl_free cm_fault_free
#endif
