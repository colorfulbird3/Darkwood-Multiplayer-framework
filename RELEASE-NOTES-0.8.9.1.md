# Darkwood Multiplayer Framework 0.8.9.1

> 0.8.9.1 = v0.9.0 架构（Trusted Client + Host World Authority）系列内部迭代收官版。
> **wire 已变更：不得与 0.8.9-beta.8 及更早版本混连；两端必须使用本版安装包。**

## 本版内容

- **共享容器双向交互**：HeldItem → 容器（HeldToContainer 新协议，Host 权威：空→放 / 同类→stack / 异类→swap），客户端原版 placeItem/swapItems Replay；`[CONTAINER]` 诊断
- **StatefulObjectSync 世界状态同步框架**：typed State Adapter（Generator{running,fuel,powered} / Light{enabled,brightness} / BearTrap{armed,triggered,broken}+TriggerBlocker）；Host 执行原版 turnOn 后事件即时广播（BroadcastStateNow），世界状态对象 1Hz 节流；同步 State 不同步 GameObject
- **背包权威 revision**：InventorySnapshot 携带 {playerId, revision}，Host 每次权威修改递增，客户端拒绝旧 revision 覆盖（`[INV-REV]`）——修掉"place-accepted 后物品消失"
- **WorldDroppedItem 拾取直进背包**：恢复原版语义（不经过 Cursor），Host 用真实 DroppedItem 数据转入权威背包（空间/距离校验），客户端原版 createItem 初始化 UI
- **Drop 改 Trusted Client 原版执行**：本地生成掉落物 → 上报 → Host 分配 EntityId 广播 → 本地对象复用为 mirror（无双份/ghost）
- **EventSync 动作事件**：PlayerAction 中继通道
- **服务层目录分层**：Network/（PlayerSync/InventorySync/EntitySync/WorldStateSync/EventSync/SnapshotSync）、Authority/（ClientAuthority/HostAuthority）、WorldState/

## 安装

解压到游戏目录（BepInEx 插件三件套：Adapter + Protocol + Core）；双开联机请两台机器都装本版。
详细变更见 docs/problems/、docs/architecture/v0.9.0-multiplier-architecture.md。