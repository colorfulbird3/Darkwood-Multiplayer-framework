# Trusted Client 迁移（v0.9.2 重构）

## 状态

Phase 1 物品链 implementation in progress（fixed in code + 回环 PASS）—— TEST A-E 待真机验收

## 问题

0.8.9.1 出现双架构冲突：
- ContainerGrab accepted → HeldItem（Cursor 在客户端和 Host 间不一致）
- DropItem rejected: SLOT_EMPTY（旧 Host authority 仍查 shadow cursor）
- ContainerGrab → ALREADY_HOLDING（双向报错循环）

根因：代码注释写 "Trust Mode"，但 Host HeldItems / PlayerGrab / HeldToInventory / HeldToContainer / ActionRejected 等旧路径未真正退出。

## 修复

彻底淘汰旧 Player/Held Authority Action：
- **保留 enum / codec 兼容**（避免编译错误），Host 收到 peer>0 旧 Action 一律 `[LEGACY-AUTH]` warning
- **客户端全部本地原版执行**（grabItem/placeItem/swapItems/addToItem/controllerPickUpItem/controllerPlaceItem）
- **新增 4 种 Commit 协议**：InventoryCommit（玩家背包 revision 唯一 Owner = Client）/ ContainerCommit（共享容器 + 玩家背包同一事务）/ PickupCommit / DropCommit
- **客户端 Postfix dirty 节流**（0.4s）上报 InventorySnapshot / ContainerInventorySnapshot / DropCommit（localDropToken + 真实物品字段）

## 真机验收

TEST A-I（详见发布要求）：
- TEST A：背包↔Cursor↔快捷栏 50 次（0 PlayerGrab / 0 NOT_HOLDING / 0 ALREADY_HOLDING）
- TEST B：容器 50 次（0 ContainerGrab Authority request）
- TEST C：地面 Pickup（0 TOO_FAR / 0 ActionRejected）
- TEST D：Drop（0 SLOT_EMPTY / 0 ghost / 0 duplicate）
- TEST E：100 次循环（0 loss / 0 Icon missing / 0 stale cursor / 0 ActionRejected）

## 不再允许

- 给 SLOT_EMPTY / ALREADY_HOLDING 打补丁
- 手工模拟 Cursor UI
- 让 Host 维护 Client Cursor
- 没真机验证就写 "Real-machine verified"
