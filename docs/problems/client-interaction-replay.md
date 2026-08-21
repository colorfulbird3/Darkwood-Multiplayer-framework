# 客户端交互 Replay 架构（client-interaction-replay）

## 状态

Fixed in code —— awaiting real-machine verification（0.8.9-beta.8 迭代二）

## 真机已确认（beta.8 首测）

- ✅ Container → Cursor 的 Authority Replay **成功**：`[REPLAY] InvSlot.grabItem success` + `[CURSOR] uiActiveInHierarchy=True sprite=91/InvItem amountLabel=2 hostHeldKnown=True`。
- ✅ Cursor Drop 成功；Host `RuntimeEntitySpawn` 成功。
- ❌ beta.8 首测未通过项：Cursor→Backpack 全 `INVALID_TARGET_SLOT`；多个 Runtime DroppedItem 一段时间后 Host `ENTITY_NOT_FOUND`（Client mirror 残留）；World Pickup→Cursor 网络成功但 `UIInvItem` 缺失。
  **禁止把 beta.8 整体标 Real-machine verified。**

## 本轮修复（beta.8 迭代二，fixed in code）

- **P0-1（INVALID_TARGET_SLOT）**：拓扑强门——Trigger Ready 门补发真实背包拓扑（含全槽容量）+ Host `TopologyReady` gate（未就绪 → `PLAYER_INVENTORY_NOT_READY`，绝不猜容量/误拒）；`[HELD] place-validate` 完整诊断。
- **P0-2（ENTITY_NOT_FOUND / mirror 残留）**：原子 `RegisterAndBroadcastDroppedItem`（ID→registry→replication binding→生命周期监视→bookkeeping→spawn 一步到位）；TickHost 生命周期监视按"Unity 对象真消失"才 Despawn（原先误判"本轮扫描未见"→吞实体）；`ENTITY_NOT_FOUND` 前 `[RUNTIME-CHECK]` 诊断（bindingAlive/rootAlive/inventoryAlive/itemAlive/trackedByLifecycle）。
- **P0-3（World Pickup 无 UI）**：world mirror 无 UI 槽 → `grabItem` 后若 cursor `UIInvItem` 缺失/inactive，用**原版 `createInvItemIcon` 等价**（`Core.AddPrefab "UI/InvItem"`+setUISprite+refresh+数量）补全新 UI 并挂到 UI 根；`[REPLAY] result=visual-failed` 才算失败（P0-I）。
- **P0-G**：`CreateDroppedItem` 对照原版补齐抛掷初速度；Host 掉落物复用原版 `spawnDroppedInvItem` 的初始化序列（AddPrefab/createItem/addToSaveable/WorldGrid/velocity）。
- **P1-A**：掉落物点击不再同时发 `ItemActivate`（只走 Pickup intent）。
- **P1-B**：`PlaceAt` partial stack 不再吞 Held remainder——不能完整放入即为 `SLOT_OCCUPIED`（原版 placeItem 不拆分）。

## 本轮修复（beta.8 迭代三，fixed in code）

- **P0-A/C（不再用 grabItem 伪造 World Pickup）**：原版 `Item.getDroppedItem` = `transferItemAllToPlayer`（进背包，不产生 cursor）；Host 已把 World→HeldItems 定格（权威）→ 客户端按权威 `HeldItemState` **独立构造** cursor（`new InvItemClass(type,dura,amount,qual,recipe)` + 恒存宿主槽 `initialize`），完全脱离 mirror。
- **P0-B（dangling source）**：`pickedUpItem.slot` 指向恒存宿主槽（玩家背包槽0），不再指向将被销毁的 mirror slot → `placeItem` 不再 NRE。
- **P0-E（sprite 全错 32/InvItem）**：全新 `createInvItemIcon`（AddPrefab "UI/InvItem"+initialize+setUISprite+refresh+数量），sprite 由真实 baseClass 决定，绝不复用 mirror/prefab 默认 UI。
- **P0-D（强 invariant 诊断）**：`[CURSOR-WORLD]` + `[CURSOR-WORLD-AFTER-DESPAWN]`——slotInventoryAlive 必须 True、UI 存活、sprite 匹配。
- **P0-F/G（Host Empty↔Client Holding 软锁）**：HeldToInventory ack 后无论 Replay 成败都强制 reconcile（Apply 权威背包 + ClearHeldItem + refreshRecipes + `[RECONCILE]`）。
- **P0-H（NOT_HOLDING 自愈）**：客户端收到 `NOT_HOLDING/ALREADY_HOLDING` 且本地 pickedUpItem≠null → 清 stale cursor + 上报真实背包 + `[HELD-DESYNC]`；连带恢复容器交互（P0-I）。

## 本轮修复（beta.8 迭代四，fixed in code）

- **问题1（World Pickup 全显示同一把枪）根因**：原版 `InvItemClass.setUISprite()`/`refresh()` 仅当 **`slot.inventory.open`** 才设真实图标（`Sprite.SetSprite(baseClass.iconType)+Build()`）。World Pickup 时玩家背包 UI 关闭 → open=false → sprite 停留在 `"UI/InvItem"` 预制默认（枪，spriteId=32）。Container grab 正常是因源容器已打开。
  - mirror 创建改原版 `slot.createItem(type,amount,dura,quality,recipe)`（按 Type 重建，绝不复制 prefab 默认 InvItem/UI/baseClass/sprite）；`[DROP-MIRROR]` 诊断。
  - ReplayPickedUpWorldItem：Ensure mirror 按 Host 权威 Type 重建（不符才 createItem）→ 原版 `slot.grabItem()` → UI 缺失时 `createInvItemIcon`（主动 `sprite.SetSprite(baseClass.iconType)+Build()`，不受 open 约束）→ 恒存宿主槽防 dangling；`[PICKUP-REPLAY]` 诊断。sprite 全部来自原生物品定义（无人工映射）。
- **问题2（客户端旧存档带入联机）**：新增 **Inventory bootstrap 门**：新 `GuestProfileApplied` 消息；Host 在收到客户端应用 Host 权威档案的 ack 前，客户端上报**只取容量（topology-only）、忽略内容**（`[INV-BOOTSTRAP] ignored client inventory content before host seed`）；GuestProfile seed 由 Host `ResolveGuestProfile`（新→空/starter kit；返回→Host GuestProfiles 恢复）建立；客户端 ApplyGuestProfile 后**清 stale cursor + 权威 replace 背包 + ack**。之后 drift 收敛才允许更新 shadow 内容。
- **回归**：B（stale inventory isolation）已加回环自检 `[SELFTEST-BOOTSTRAP] ...=PASS`；A（多类型 sprite）/D（cursor isolation）留真机（`[CURSOR-WORLD]`/`[DROP-MIRROR]`/`[PICKUP-REPLAY]`）。

## 剩余风险 / 已知限制

- 玩家背包 grab 的 ammo/modifiers 尚未在 shadow 中保存（ContainerGrab 已带 ammo；PlayerGrab shadow 副本 ammo 暂 0）。
- held→共享容器放置暂不支持（提示放回背包/丢地面）。
- 本轮修复（拓扑门/原子注册/World pickup UI/P1）**尚未真机验证**。
- wire 未变（仍 beta.8，两端同版即可）。

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
