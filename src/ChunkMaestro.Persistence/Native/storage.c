#include <sys/stat.h>
#include <sys/acl.h>
#include <unistd.h>
#include <fcntl.h>
#include <errno.h>
#include <stdint.h>

/* Compile against the host SDK: no managed assumptions about Darwin struct stat. */
int cm_scope(const char *path, int ancestor)
{
    struct stat value;
    if (lstat(path, &value) || S_ISLNK(value.st_mode)) return 0;
    if (ancestor)
    {
        if (!S_ISDIR(value.st_mode) || (value.st_uid != 0 && value.st_uid != geteuid())) return 0;
        if ((value.st_mode & (S_IWGRP | S_IWOTH)) && !(value.st_uid == 0 && (value.st_mode & S_ISVTX))) return 0;
    }
    else if ((!S_ISDIR(value.st_mode) && !S_ISREG(value.st_mode)) ||
        (S_ISREG(value.st_mode) && value.st_nlink != 1) || value.st_uid != geteuid() || (value.st_mode & (S_IRWXG | S_IRWXO))) return 0;
    acl_t acl = acl_get_file(path, ACL_TYPE_EXTENDED);
    if (!acl) return errno == ENOENT;
    acl_entry_t entry;
    int result = acl_get_entry(acl, ACL_FIRST_ENTRY, &entry);
    int valid = 1;
    while (result == 0)
    {
        acl_tag_t tag;
        if (!ancestor || acl_get_tag_type(entry, &tag) || tag != ACL_EXTENDED_DENY)
        {
            valid = 0;
            break;
        }
        result = acl_get_entry(acl, ACL_NEXT_ENTRY, &entry);
    }
    if (result != -1 || errno != EINVAL) valid = 0;
    if (acl_free(acl)) valid = 0;
    return valid;
}
int cm_open(const char *path)
{
    return open(path, O_RDONLY | O_NOFOLLOW | O_CLOEXEC);
}
int cm_matches(int descriptor, const char *path)
{
    struct stat held, current;
    return !fstat(descriptor, &held) && !lstat(path, &current) &&
        !S_ISLNK(current.st_mode) && held.st_dev == current.st_dev && held.st_ino == current.st_ino;
}
