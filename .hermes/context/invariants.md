# DMF 不变量清单

这些规则是所有 Agent、实现和审查任务的硬约束。

## 权威与事务

- `HOST_AUTHORITATIVE_WORLD`：Host 是世界、实体、怪物结果和共享容器的唯一最终写入者。
- `CLIENT_SENDS_INTENT`：Client 只能发送意图/ActionRequest，不能把本地预测状态当作最终状态广播。
- `AT_MOST_ONCE_APPLY`：同一 `(SessionId, PeerId, RequestId)` 最多 Apply 一次；重复包返回缓存结果。
- `CAS_REVISION`：Action 必须携带 ExpectedRevision；旧版本请求拒绝并返回当前权威版本。
- `ATOMIC_INVENTORY`：容器和对应玩家库存 shadow 必须原子更新，不能只更新其中一边。
- `AUTHORITATIVE_ROLLBACK`：拒绝、超时、断线必须使用 Host 状态恢复 cursor、来源槽和目标槽。

## 身份与快照

- `STABLE_ENTITY_ID`：Persistent EntityId 不能使用 Unity `GetInstanceID()`、名称或 Instantiate 顺序。
- `RUNTIME_ID_HOST_ASSIGNED`：动态实体的 RuntimeEntityId 只能由 Host 分配。
- `READY_AFTER_APPLY`：存档、注册表、快照和关键共享容器未完整应用时，Client 不得 READY。
- `NO_SILENT_MISSING`：重复 ID、关键实体缺失、无法重绑或 digest 冲突必须可观测，并按策略阻止 READY。
- `MONOTONIC_REVISION`：客户端不得用旧 revision 覆盖新状态。

## 版本与证据

- `WIRE_VERSION_EXPLICIT`：Envelope、SaveBundle、WorldSnapshot 的 wire 版本必须明确；当前握手字段与内部 header 的漂移要先解决。
- `TEST_CURRENT_BASELINE`：SelfTests 必须使用当前 alpha.9 identity，不得以旧 alpha.1 fixture 作为发布依据。
- `EVIDENCE_NOT_ASSUMPTION`：源码存在不等于实机验证；没有双端日志只能标 `IMPLEMENTED_UNVERIFIED`。
- `NO_GUESS_API`：没有真实 Hermes/DS V4 SDK 文档时，禁止虚构运行时 API。
