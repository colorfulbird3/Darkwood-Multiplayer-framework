# Protocol Contract（alpha.9）

## 当前公开身份

```text
ProtocolVersion       = 3
FrameworkVersion      = 0.8.7-alpha.9
Handshake SaveSchema  = 1
Handshake Snapshot    = 3
GameVersion           = Application.version
```

注意：当前 `DarkwoodSaveBundle` 内部 Schema 为 3，`WorldSnapshotWireCodec` 内部 Schema 为 2。它们与握手字段不一致。不得继续把三者写成同一个事实；Gate G1 要么统一公共常量，要么把 Envelope/SaveBundle/WorldSnapshot 三种 wire schema 明确分开并让握手报告真实版本。

## Join 状态机

```text
Disconnected → Connecting → VersionChecking → SaveTransfer
             → LoadingSave → BuildingRegistry → ApplyingSnapshot → Ready
```

失败必须进入 `Failed`/`Disconnected` 并清理 pending request、传输组装器、临时存档和远端实体。Client 在 `Ready` 前不得发送姿态、Action 或实时增量。

## 消息边界

- Envelope：magic、header、message type、flags、sequence、session id、长度受限 payload。
- 握手：比较 Protocol、Framework、Game、Save Bundle Schema 和 World Snapshot Schema。
- 存档：manifest + chunk + hash；完整哈希验证后才加载独立客户端目录。
- 快照：registry manifest、实体状态、共享库存状态；应用结果需记录 applied/rebound/missing。
- Action：`ActionRequest`、`ActionResult`、`ActionRejected`。请求至少绑定 session、peer、RequestId、目标 EntityId、ExpectedRevision 和 payload。

## 兼容性规则

- alpha.8 与 alpha.9 不兼容，双端必须使用同一 alpha.9 DLL。
- 修改 wire 字段、枚举或语义时，递增对应版本并增加 roundtrip、截断、未知枚举和旧版本拒绝测试。
- 幂等键按 `(SessionId, PeerId, RequestId)` 隔离；重连不得复用旧 session 缓存结果。
- SelfTests 必须断言发布使用的真实常量，不允许维护一套过期 fixture。
