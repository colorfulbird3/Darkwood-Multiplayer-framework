# 客户端交互 Replay 架构（client-interaction-replay）

## 状态

Fixed in code —— awaiting real-machine verification（0.8.9-beta.8）

## 用户现象

1. 客户端从共享容器 grab 后：物品逻辑进 held、`UIInvItem != null`，但**鼠标只显示"丢弃"文字，没有物品图标/数量**。
2. 客户端从自己背包拿到 rag 后：本地 `pickedUpItem` 存在，但任何网络操作（Place / Drop）都返回 `NOT_HOLDING`。
3. 地面拾取直接进背包（与原版"光标拿取"体验不符）。

## 根因

旧架构是"客户端手工模拟 Darkwood 交互"：
- grab 后手工 `new InvItemClass(...)` / 手工拼 `UIInvItem`，源槽 UI 被 Host 的 `InventoryState` 先清掉 → sticky/inactive UI 引用，鼠标无图标。
- 客户端从 playerInv/hotbar grab 走的是**原版本地执行**，Host 的 `HeldItems[peer]` 为空 → 之后全部 `NOT_HOLDING`。
- 地面拾取灌进 shadow 背包，不经 cursor 状态机。

**手工复现 Darkwood 的 UI / Cursor / Sprite / amount / slot / stack / swap / prompt 必然遗漏原版内部状态。**

## 旧行为

- Client 本地先用原版做 mutation，Postfix 再把结果上报（“先执行后上报”）→ 双方状态经常分叉。
- grab/place 的结果 UI 由客户端自己造假。

## 修复方案（新架构）

```
CLIENT INTENT → HOST AUTHORITY → AUTHORITATIVE RESULT → CLIENT REPLAY ORIGINAL DARKWOOD LOGIC → AUTHORITATIVE RECONCILE
```

- 客户端交互 patch 统一：`Ready && !Replaying` → 只发 Intent、`return false`；`ReplayingAuthoritativeAction` → 放行原版执行、绝不二次发 Intent。
- 新增 `AuthorityReplayScope`（Runtime.BeginAuthorityReplay/Dispose）：Scope 内 `ApplyingRemote=true` + `ReplayingAuthoritativeAction=true`。
- Host Accepted 后客户端在 Scope 内**直接调用原版方法**：
  - Container / Player grab → `sourceSlot.grabItem()`
  - Cursor → 背包 → `destSlot.placeItem()`
  - Drop → 复用 `Player.spawnDroppedInvItem` 的 **cursor cleanup 段**（despawn UIInvItem + pickedUpItem=null + refreshRecipes），**绝不实例化 world DroppedItem**
  - 地面拾取 → 对 runtime mirror 的 `slots[0].grabItem()`（cursor-only）并把 UI 从 mirror 根解离，再由 `RuntimeEntityDespawn` 销毁 mirror
- Replay 用 `Core.sendTriggerInfo` 抑制 onTake/onPlace（避免与 Host 权威世界事件双发）。
- **消息顺序**：Host 先给发起 Client 发 `ActionResult`（其本地 source slot 未清 → 可 Replay），再广播权威 `InventoryState`（reconcile）。绝不先清 source。
- 玩家背包/快捷栏 grab 改走 `PlayerGrab`（Host 从 shadow 整槽 → `HeldItems[peer]`），不再本地执行。
- 地面拾取改走 `HeldItems[peer]`（World → Cursor），不再直接 shadow.Add 进背包。
- `HeldItemState` 增加 `Ammo`。

## 涉及模块

- `World/DarkwoodWorldAuthorityService.cs`（Drop 事务）
- `DarkwoodAdapterRuntime.Entities.cs`（ContainerGrab/PlayerGrab/Pickup/HeldToInventory Host handlers + 消息顺序）
- `DarkwoodAdapterRuntime.Messages.cs`（TryRequestPlayerGrab 等客户端 intent）
- `DarkwoodAdapterRuntime.RuntimeEntities.cs`（ack → Replay 原版）
- `DarkwoodContainerTakePatch.cs`（grab/place intent + Replay 放行）
- `DarkwoodReplayTriggerGuard.cs`（Replay 内 sendTriggerInfo 抑制，新增）
- `DarkwoodAdapterRuntime.cs`（AuthorityReplayScope）
- Protocol：`PlayerGrab` Action、`HeldItemStatePayload.Ammo`、`PlayerGrabPayload`

## 网络生命周期

- Intent（ActionRequest）→ Host 验证 → Accepted（ActionResult 带足够上下文）→ Client Replay 原版 → Autority reconcile（InventoryState / PlayerInventoryState / RuntimeEntityDespawn）。
- Host `HeldItems[peer]` 状态机：`Empty → Acquire/Accept → Holding → Place/Drop → Empty`；被拒保持在 Holding/Empty，永不让 Host 与 Client 分叉。

## 验证方式

- `[REPLAY]`：确认原版方法执行成功
- `[CURSOR]`：`uiActiveInHierarchy=true`、`sprite=...`、`amount=...`
- `[HELD-STATE]`：Host 侧 transition
- `[INTENT]`：请求确实发出

## 已通过测试

- Build 0 错
- Unit Tests 50/50
- SelfTests 85/85
- Loopback：见本轮运行报告

## 真机测试结果

awaiting real-machine verification —— 待用户双开按 TEST 1-10（Container→Cursor / →Backpack / Backpack→Cursor / stack / swap / Drop / World→Cursor / 循环 30 次 0 ghost）。

## 剩余风险 / 已知限制

- 玩家背包 grab 的 ammo/modifiers 尚未在 shadow 中保存（ContainerGrab 已带 ammo；PlayerGrab 用 shadow 副本，ammo 暂为 0——影响手持弹药显示，后续补 shadow 字段）。
- held→共享容器放置暂不支持（提示放回背包/丢地面）。
- 真机尚未验证 `grabItem` Replay 在物品有特殊状态（recipe/upgrades）时的完整视觉。
