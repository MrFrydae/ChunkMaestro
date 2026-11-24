#ifndef CM_POLICY_SNAPSHOT_H
#define CM_POLICY_SNAPSHOT_H
#include <stdint.h>
typedef struct cm_policy_snapshot cm_policy_snapshot;
/* All completions are UNQUALIFIED. Slots transfer exclusive ownership. */
int32_t cm_policy_snapshot_v1_take(int32_t *parent, int32_t *file, cm_policy_snapshot **output);
int32_t cm_policy_snapshot_v1_copy(cm_policy_snapshot *snapshot, uint8_t *buffer, uint32_t capacity, uint32_t *written);
int32_t cm_policy_snapshot_v1_facts(cm_policy_snapshot *snapshot, uint8_t *buffer, uint32_t capacity, uint32_t *written);
int32_t cm_policy_snapshot_v1_check(cm_policy_snapshot *snapshot);
void cm_policy_snapshot_v1_destroy(cm_policy_snapshot **snapshot);
#endif
