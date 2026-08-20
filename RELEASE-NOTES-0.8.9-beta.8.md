# Darkwood Multiplayer Framework 0.8.9-beta.8

一句话：把物品交互从"客户端手工模拟 Darkwood UI"重构为"**客户端发 Intent → Host 权威 → 客户端复演 Darkwood 原版交互 → 权威状态校准**"——鼠标抓取/放回/堆叠/交换/丢弃全部走原版逻辑，杜绝手工拼 UI 造成的图标丢失与状态分叉。

## 主要修复

- **Container → Cursor / Backpack → Cursor / 地面 → Cursor**：抓取/拾取后鼠标显示与主机一致的真实物品图标与数量（原版 `grabItem` 自己创建 Cursor UI）。
- **修复 NOT_HOLDING**：玩家从自己背包/快捷栏拿物品现在走 `PlayerGrab`（Host `HeldItems` 权威），不再客户端本地抢飞。
- **消息顺序修复**：Host 先给发起方发 Accepted（本地 source 槽未清 → 可复演抓取），再广播权威容器状态——不再先清 source 导致抓取落空。
- **地面拾取改为光标拿取**：不再直接进背包，符合 Darkwood 原版光标手感。

## 同步 / 网络协议变化

- 新增 `PlayerGrab` Action；`PlayerGrabPayload{FromHotbar,SlotIndex}`。
- `HeldItemState` 增加 `Ammo`（容器抓取弹药物品时可显示弹量）。
- 协议版本升到 `0.8.9-beta.8`（无向下兼容，两端必须同版本）。

## World / Entity 生命周期变化

- 地面拾取：Pickup Accepted 先到（本地 mirror 还在 → 复演抓取挂光标），后广播 `RuntimeEntityDespawn` 销毁 mirror——光标图标从 mirror 根解离，不再连带销毁。
- drop 的世界对象仍只由 Host `RuntimeEntitySpawn` 创建（客户端绝不 Instantiate）。

## Inventory / HeldItem / Drop 变化

- 新增 `AuthorityReplayScope`：Host Accepted 后客户端在作用域内直接调用原版 `grabItem()` / `placeItem()` / swap/stack 所需原版逻辑。
- Replay 内抑制 `sendTriggerInfo`(onTake/onPlace)，避免与 Host 世界事件双发。
- Drop 复用原版 `spawnDroppedInvItem` 的 cursor-cleanup 段（图标销毁 + 清手持 + 刷新配方），不产生本地 ghost。

## 调试与诊断改进

- `[INTENT]` / `[AUTH]` / `[HELD-STATE]` / `[REPLAY]` / `[CURSOR]` / `[RECONCILE]` 全链路日志，可一眼定位是"Intent 没发 / Host 拒绝 / Replay 没跑 / 原版失败 / reconcile 覆盖"。

## 测试

- Build：0 错
- Unit Tests：50/50
- SelfTests：85/85
- Loopback：见本轮运行报告

## 真机验证

awaiting real-machine verification —— 待双开按 TEST 1-10（Container→Cursor→Backpack→Cursor→stack/swap→Drop→World Pickup→循环 30 次，0 幽灵/0 NOT_HOLDING/0 图标丢失）。

## 已知问题

- 玩家背包抓取时 shadow 尚未携带 ammo/modifiers（容器抓取已带 ammo）。
- held→共享容器放置暂不支持（提示放回背包或丢地面）。
- 未真机确认：recipe/upgrades 等特殊物品在 Replay 抓取时的完整视觉。

## 升级说明

- 主机与客户端都必须更新到 `Darkwood联机框架-安装包-v0.8.9-beta.8`（协议不向下兼容）。
- 客户端：主菜单直接连接；联机失败提示重启时请重启游戏后再连。
