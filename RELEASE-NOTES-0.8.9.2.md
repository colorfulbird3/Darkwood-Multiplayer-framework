# Darkwood Multiplayer Framework 0.8.9.2

> 0.8.9.2 = v0.9.0 架构（Trusted Client + Host World Authority）迁移版（架构迁移进行中；尚未真机验证）。
> **wire 已变更：不得与 0.8.9.1 及更早版本混连；两端必须使用本版安装包。**

## 状态

- 架构迁移进行中（Phase 1 migration in progress）。
- **未真机验证**（Fixed in code + 回环 PASS；TEST A-I 需用户真机双开验收）。
- 完成 TEST A-E 之前不要写"Real-machine verified"。

## 本版主要变更（架构重整）

- **彻底淘汰旧 Host-player authority**：保留旧 ActionKind enum / codec 兼容，Host 收到 peer>0 旧 Action 一律 `[LEGACY-AUTH]` warning（不再容忍 SLOT_EMPTY/ALREADY_HOLDING/NOT_HOLDING）。
- **客户端 Inventory/Cursor 全面本地原版执行**：DarkwoodContainerGrabPatch / DarkwoodContainerTakePatch / DarkwoodContainerDragDestinationPatch 的 Prefix 一律 return true（不再拦截 grabItem/placeItem/swapItems/addToItem/controllerPickUpItem/controllerPlaceItem），Postfix 标记 dirty 并节流上报。
- **新增四种 Commit 协议**：InventoryCommit（玩家背包 revision 单向递增，Client 自有 Owner）/ ContainerCommit（共享容器 + 玩家背包同一事务 + baseContainerRevision 多人冲突检测）/ PickupCommit（runtime EntityId + 玩家 revision）/ DropCommit（localDropToken + 玩家 revision）。
- **DropCommit 走 Trusted Client**：客户端原版 `spawnDroppedInvItem` 完整本地执行，Postfix 生成 localDropToken + 上报；Host 接收后仅登记权威 EntityId 并广播 Spawn（不查 Cursor、不判 SLOT_EMPTY）；发起方按 token 复用本地对象为 mirror，其他 Client 走 spawn 落地。
- **PickupCommit 走 Trusted Client**：客户端原版地面拾取直接进背包，Postfix 上报 RuntimeEntityId + InventorySnapshot；Host 信任并 Unregister + Despawn 广播；race 走 InventoryReconcile（发送权威玩家背包快照让 apply 端拒绝旧 revision）。
- **玩家背包 revision 唯一 Owner**（P0-G）：玩家自己的 Inventory Revision 由该玩家 Client 拥有；Host 只门控 incoming > last（拒绝旧 revision）。
- **协议扩展**：ProtocolMessageType 新增 InventoryCommit/ContainerCommit/PickupCommit/DropCommit/LegacyAuthAction + ContainerStateReport。

## 安装

解压到游戏目录（BepInEx 插件三件套：Adapter + Protocol + Core）；双开联机请两台机器都装本版。

## 不再允许

- 旧 PlayerGrab / HeldToInventory / HeldToContainer / ContainerGrab 作为主路径
- 给 SLOT_EMPTY / ALREADY_HOLDING 打特判补丁
- 让 Host 维护 Client Cursor
- 把复杂世界状态塞进 GenericItem flags

## 详细变更

详见 docs/problems/trusted-client-migration.md、world-state-owner-binding.md 等。
