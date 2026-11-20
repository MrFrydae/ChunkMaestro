/* Deterministic OS error/metadata injection, separate from real filesystem proof. */
#include "faults.h"
#include <unistd.h>
#include <errno.h>
static int fault;
void cm_test_fault(int value) { fault = value; }
int cm_fault_lstat(const char *path, struct stat *value)
{
    int result = lstat(path, value);
    if (!result && fault == 1) value->st_uid = geteuid() + 1;
    if (!result && (fault == 2 || fault == 3)) value->st_uid = 0;
    return result;
}
acl_t cm_fault_acl(const char *path, acl_type_t type)
{
    if (fault == 4 || fault == 5)
    {
        errno = fault == 4 ? ENOENT : EACCES;
        return NULL;
    }
    return acl_get_file(path, type);
}
int cm_fault_tag(acl_entry_t entry, acl_tag_t *tag)
{
    if (fault == 6) { errno = EIO; return -1; }
    return acl_get_tag_type(entry, tag);
}
int cm_fault_entry(acl_t acl, int kind, acl_entry_t *entry)
{
    int result = acl_get_entry(acl, kind, entry);
    if (result == -1 && fault == 7) errno = EIO;
    return result;
}
int cm_fault_free(void *acl)
{
    int result = acl_free(acl);
    if (fault == 8) { errno = EIO; return -1; }
    return result;
}
