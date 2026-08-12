# Darkwood Multiplayer Framework 架构路线图

本文档记录 0.8.x 及后续版本的网络架构目标。0.8.6 已开始落实 Action Core；文档中的后续功能仍是实现规划，不代表已经全部完成。

## 0.8.x 收口顺序

- `0.8.6` Action Core：先完成 Pickup 的 Request → Validate → Apply → Result 闭环。
- `0.8.7` Runtime Entity：Host 分配运行时 ID，补齐 Spawn/Despawn 生命周期。
- `0.8.8` Scene Transition：切场景期间暂停 Action/Delta，重建 Registry 并重新快照。
- `0.8.9` Stability：断线、重连、重复包、超时和诊断界面。

进入 `0.9.0-alpha` 前，0.8.x 不横向增加更多玩法同步对象。

## 联机生命周期

```text
CONNECT → VERSION_CHECK → SAVE_TRANSFER → LOAD_SAVE
→ ENTITY_REGISTRY → WORLD_SNAPSHOT → READY → LIVE_REPLICATION
```

客户端在收到 `READY` 前不发送玩家实体状态，也不处理实时交互请求。断线重连或切换场景后，必须重新执行 `WORLD_SNAPSHOT` 和 `READY`。

## 实体身份

- `WorldEntityId`：存档中的箱子、门、发电机、工作台和固定物品。
- `RuntimeEntityId`：敌人、投射物、临时掉落物等运行时实体，由主机分配。
- 禁止使用 Unity `GetInstanceID()`、名称或 Instantiate 顺序作为网络身份。

实体注册表必须在主机和客户端建立后比较 digest；不匹配时停止实时同步并记录明确原因。

## 主机权威与 Action 层

```text
Client Request → Host Validate → Host Apply → StateVersion++ → Replicate Result
```

拾取、丢弃、攻击、开门、制作和使用物品都应走统一 Action 协议。客户端只发送意图，不直接提交最终世界状态。

## 通用状态版本

容器现有的 `InventoryRevision` 应逐步抽象为通用 `StateVersion`，覆盖实体、门、敌人、发电机、容器和世界快照。客户端收到旧版本时直接丢弃，避免乱序消息回滚状态。

## 世界快照

大快照应分段传输，而不是一个巨型消息：

```text
SnapshotBegin
WorldState
EntityState[]
ContainerState[]
PlayerState[]
RuntimeEntityState[]
SnapshotEnd
```

每段需要带序号、总段数和校验值，支持超时重试、断点诊断和重连恢复。

## 玩家同步

玩家状态保持约 15 Hz：位置、方向、移动/奔跑、瞄准和攻击状态。主机校验速度、场景和可行走范围；客户端使用插值显示远端玩家，不把每帧 Transform 当作最终权威状态。

## 实现优先级

1. 将 C# 源码和工程文件纳入 `src/`。
2. 实现连接状态机与 `READY` 门控。
3. 固化 Persistent/Runtime Entity ID 分配和注册表校验。
4. 将 `InventoryRevision` 提取为通用 `StateVersion`。
5. 将世界快照拆成可重试的分段消息。
6. 统一 Action、事件和错误码。
7. 增加序列化、实体注册表、快照和断线重连测试。
