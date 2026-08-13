# Protocol Contract（alpha.11）

## 版本契约：单一门槛，无向下兼容

```text
EnvelopeProtocol  = 3（信封头常量，框架线内不变）
FrameworkVersion  = 0.8.7-alpha.11（唯一版本门槛）
GameVersion       = Application.version（游戏构建）
```

- 握手只比较 `FrameworkVersion` 与 `GameVersion`，任一不一致即拒绝（`INCOMPATIBLE_FRAMEWORK_VERSION` / `INCOMPATIBLE_GAME_BUILD`）。
- **无向下兼容**：不维护旧版本 fixture、不做版本翻译；任何 wire 改动直接递增框架版本并发布新包，旧包全部作废。
- 内部 `DarkwoodSaveBundle`（wire 3）与 `WorldSnapshotWireCodec`（schema 2）版本头是实现细节，随框架版本绑定，不再单独协商（PROTO-001 已解决）。

## Join 状态机

```text
Disconnected → Connecting → VersionChecking → SaveTransfer
             → LoadingSave → BuildingRegistry → ApplyingSnapshot → Ready
```

失败必须进入 `Failed`/`Disconnected` 并清理 pending request、传输组装器、临时存档和远端实体。Client 在 `Ready` 前不得发送姿态、Action 或实时增量。

## 消息边界

- Envelope：magic、header、message type、flags、sequence、session id、长度受限 payload。
- 握手：比较 Framework、Game 两个版本字段。
- 存档：manifest + chunk + hash；完整哈希验证后才加载独立客户端目录。
- 快照：registry manifest、实体状态、共享库存状态；应用结果需记录 applied/rebound/missing。
- Action：`ActionRequest`、`ActionResult`、`ActionRejected`。请求至少绑定 session、peer、RequestId、目标 EntityId、ExpectedRevision 和 payload。

## 兼容性规则（无向下兼容）

- 双端必须使用同一框架版本（同一安装包），任何不一致在握手即被拒绝。
- 修改 wire 字段、枚举或语义时，递增框架版本并增加 roundtrip、截断、未知枚举和版本不一致拒绝测试。
- 幂等键按 `(SessionId, PeerId, RequestId)` 隔离；重连不得复用旧 session 缓存结果。
- SelfTests 必须断言发布使用的真实常量（`ProtocolVersions`）。
