# Darkwood Multiplayer Framework 架构路线图

## 联机生命周期

```text
CONNECT -> VERSION_CHECK -> SAVE_TRANSFER -> LOAD_SAVE
-> ENTITY_REGISTRY -> WORLD_SNAPSHOT -> READY -> LIVE_REPLICATION
```

客户端在进入 `READY` 前不发送实时玩家状态，也不处理实时交互请求。断线重连或切换场景后，需要重新执行世界快照与 READY 握手。

## 实体身份

- 持久实体使用场景、类型、SaveableObject 唯一 ID 和层级路径生成稳定 ID。
- 运行时实体由主机分配 RuntimeEntityId。
- 不使用 Unity `GetInstanceID()`、对象名称或 Instantiate 顺序作为网络身份。

## 主机权威 Action

```text
Client Request -> Host Validate -> Host Apply -> StateVersion++ -> Replicate Result
```

拾取、丢弃、攻击、开门、制作和使用物品都应逐步迁移到统一 Action 协议。客户端发送意图，主机验证并应用最终世界状态。

## 快照与玩家同步

世界快照按阶段分块传输，支持校验、超时重试和断点诊断。玩家姿态以约 15 Hz 发送，客户端使用插值显示远端玩家，避免把每帧 Transform 当作最终权威状态。

当前版本是重构中的 0.8 架构，不代表所有 0.7.0 游戏行为都已迁移完成。