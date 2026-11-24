#define _GNU_SOURCE
#include "peer.h"
#include <errno.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/un.h>

static cm_peer_result result(cm_peer_state state, int error)
{
    cm_peer_result value = {state, error};
    return value;
}

static cm_peer_result failed(void)
{
    int error = errno;
    cm_peer_state state = CM_PEER_UNAVAILABLE;
    if (error == EBADF || error == ENOTSOCK || error == ENOTCONN || error == EINVAL)
    {
        state = CM_PEER_INVALID;
    }
    return result(state, error);
}

cm_peer_result cm_peer_query(int fd, cm_peer_facts *out)
{
    if (out == NULL)
    {
        return result(CM_PEER_INVALID, 0);
    }
    memset(out, 0, sizeof(*out));
    if (fd < 0)
    {
        return result(CM_PEER_INVALID, 0);
    }
    int type = 0;
    socklen_t length = sizeof(type);
    if (getsockopt(fd, SOL_SOCKET, SO_TYPE, &type, &length) != 0)
    {
        return failed();
    }
    if (length != sizeof(type) || type != SOCK_STREAM)
    {
        return result(CM_PEER_INVALID, 0);
    }
    int listening = 0;
    length = sizeof(listening);
    if (getsockopt(fd, SOL_SOCKET, SO_ACCEPTCONN, &listening, &length) != 0)
    {
        return failed();
    }
    if (length != sizeof(listening) || listening != 0)
    {
        return result(CM_PEER_INVALID, 0);
    }
    struct sockaddr_storage address;
    memset(&address, 0, sizeof(address));
    length = sizeof(address);
    if (getsockname(fd, (struct sockaddr *)&address, &length) != 0)
    {
        return failed();
    }
    if (length < sizeof(sa_family_t) || length > sizeof(struct sockaddr_un) ||
        address.ss_family != AF_UNIX)
    {
        return result(CM_PEER_INVALID, 0);
    }
    memset(&address, 0, sizeof(address));
    length = sizeof(address);
    if (getpeername(fd, (struct sockaddr *)&address, &length) != 0)
    {
        return failed();
    }
    if (length < sizeof(sa_family_t) || length > sizeof(struct sockaddr_un) ||
        address.ss_family != AF_UNIX)
    {
        return result(CM_PEER_INVALID, 0);
    }
    struct ucred credentials;
    memset(&credentials, 0, sizeof(credentials));
    length = sizeof(credentials);
    if (getsockopt(fd, SOL_SOCKET, SO_PEERCRED, &credentials, &length) != 0)
    {
        return failed();
    }
    if (length != sizeof(credentials) || credentials.pid <= 0 ||
        credentials.uid == (uid_t)-1 || credentials.gid == (gid_t)-1)
    {
        return result(CM_PEER_INVALID, 0);
    }
    out->pid = credentials.pid;
    out->uid = credentials.uid;
    out->gid = credentials.gid;
    return result(CM_PEER_FACTS, 0);
}
