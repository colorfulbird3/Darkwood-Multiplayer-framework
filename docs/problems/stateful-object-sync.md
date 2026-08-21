# 阶段二：共享容器双向交互 + 世界状态同步（stateful-object-sync）

## 状态

Fixed in code —— awaiting real-machine verification（0.8.9-beta.9）

## 目标

1. HeldItem 可放回共享容器（空→放 / 同类→stack / 异类→swap）。
2. 世界状态对象（Generator / BearTrap / Lamp）状态同步框架（StatefulObjectSync），不再只同步"Entity 存在"。

## 现状（已工作）

- Container → Cursor、Cursor → PlayerInventory、World Pickup 生命周期均已收敛。
- 同步层覆盖 Character/Door/Window/Item/Inventory——但大量状态组件（TriggerBlocker、ItemLight、ItemSounds、GameEvents、Constructible）"Entity 存在但 State 没同步"。

## 旧行为

- held → 共享容器：提示"暂不支持"，无路径。
- 发电机/灯：客户端点击 → 原版本地 activate（客户端自己 isOn=true + 本地电源/drain 模拟）或根本不同步 → 两端状态分叉。

## 修复方案（新架构）

```
Host Authority
 ├─ Entity Sync（binding/存在性）
 ├─ Inventory Sync（物品转移 + Cursor Replay）
 └─ State Sync（StateAdapter 捕获/应用 typed 状态 → 1Hz 低频 + 事件即时广播）
```

### 1. HeldToContainer（新 ActionKindWire.HeldToContainer）

- 协议：`HeldToContainerPayload{SlotIndex}`（容器 ID 走请求 TargetValue）。
- Host：验证（容器存在/shared/槽合法/HeldItems 存在）→ 事务（空→`slot.invItem=new InvItemClass(held)`；同类→完整放入才 stack（不吞 remainder）；异类→swap：原槽物成为新 Held、槽接收 held）→ `HeldItems` 权威更新 → **ack 先入队**（发起者先收到、本地槽未清 → 可 Replay）→ 广播权威 `InventoryState`（全体）。
- Client：`DragDestinationPatch` shared 分支发 intent；ack 后 `AuthorityReplayScope` 内原版 Replay（非 swap → `slot.placeItem()`；swap → `slot.swapItems()`——原版自动把新 held 挂 cursor/迁移 UI）。
- `[CONTAINER]` / `[REPLAY]` / `[RECONCILE]` 日志。

### 2. StatefulObjectSync（StateSync）

- `GeneratorStateAdapter`（Schema 7：isOn/fuel/lowPower；Apply 幂等赋值燃料=Host 权威，客户端绝不 drain；item.isOn 同步本体视觉）；`LightStateAdapter`（Schema 8：light.enabled/destLightIntensity/poweringDown/lowPower——灯属于电源网络，由 Host 原版 restorePower 驱动后捕获）；BearTrap 扩展（Apply 同步 `TriggerBlocker.enabled = armed && !destroyed`）。
- `StateObjectInteract` intent（ActionKindWire=16）：客户端点击发电机不再本地 activate（`ItemActivatePatch` 发电机分支拦截），发 intent → Host **执行原版 `turnOn()/turnOff()`**（restorePower/drain 全链路）→ `BroadcastStateNow` 即时捕获广播 EntityDelta → 所有客户端 `adapter.Apply`（幂等，禁 toggle）。
- Tick 分层：Generator/Light/BearTrap 状态对象 **1Hz** 低频捕获（`StateThrottledSchemas`）；玩家/常规实体保持 15Hz diff；**事件即时**走 `CaptureNow`/`BroadcastStateNow` 不受限。
- 禁止事项落实：无字符串 type 分发（类型化 `component is Generator`）、客户端绝不先设 isOn、不同步 GameObject（只序列化标量）、typed schema 不再堆进通用 flag。

## 涉及模块

- Protocol：`ActionKindWire.HeldToContainer/StateObjectInteract`、`HeldToContainerPayload`、`StateObjectIntentPayload` + codec
- `DarkwoodAdapterRuntime.Entities.cs`：`HandleHeldToContainerRequest`、`HandleStateObjectInteractRequest`、`BroadcastStateNow`
- `DarkwoodAdapterRuntime.Messages.cs`：`TryRequestHeldToContainer`、`TryRequestStateObjectInteract`
- `DarkwoodAdapterRuntime.RuntimeEntities.cs`：`ReplayHeldToContainer`
- `DarkwoodContainerTakePatch.cs`：shared 分支 intent
- `DarkwoodInteractionPatches.cs`：发电机 activate 拦截
- `World/WorldStateAdapters.cs`：`GeneratorStateAdapter`、`LightStateAdapter`、BearTrap/TriggerBlocker 扩展
- `World/WorldStateAdapter.cs`：`WorldStateSchemas.Generator=7/Light=8`
- `DarkwoodEntityReplication.cs`：`CaptureNow`、状态对象 1Hz 节流
- `DarkwoodAdapterRuntime.cs`：adapter 注册

## 验证方式

- `[CONTAINER]`（Host action/result/broadcast）、`[GENERATOR]`、`[STATE]`、`[BEARTRAP]`、`[REPLAY] HeldToContainer`

## 已通过测试

- Build 0 错；Unit 50/50；SelfTests 85/85；Loopback 通过（11s+96s，横幅 0.8.9-beta.9，INV-BOOTSTRAP B 回归 PASS）

## 真机测试结果

awaiting real-machine verification：
- TEST 1：箱子拿物品→放回箱子（双方一致）
- TEST 2：A 拿 B 看（B 见变化）
- BearTrap：Host 踩夹子 → Client 同步 Sprite/Collider/状态/伤害
- Generator：开/关/耗油 → Client 同步灯/声音/状态
- Lamp：开电 → Client 所有灯同步

## 本轮修复（beta.9 迭代二，fixed in code）

- **P0 Inventory Authority Drift（place-accepted 后物品消失）**：根因=乱序/迟到旧 InventorySync 包覆盖已提交状态。
  - `PlayerInventoryStatePayload` 增加 **Revision + PlayerId**（wire 变）；
  - `DarkwoodPlayerInventoryShadow.Revision` + `Touch()`：Host 每次权威修改（PlaceAt/Remove/Rebuild/RefreshTopology/AddStarterKit）递增；
  - 所有 Host→Client 的 `CaptureState` 携带 `playerId=目标peer`；
  - 客户端 `ApplyPlayerInventory` 按 (playerId, revision) 门控：**旧 revision 包直接丢弃**（`[INV-REV] 丢弃迟到旧包`），不再覆盖新状态。
- **P1 HeldItem ownership（cursor 与 slot 双真）**：drop-resolve 显式 ownership 判定并日志——cursorMatch → **CursorOwned**（Drop 只走 HeldItem）；仅槽内 → **InventoryOwned**（PlayerSlot）。
- **P2 StatefulObjectSync**：已在 beta.9（Generator/Light/BearTrap adapters + StateObjectInteract intent + Host 原版执行 + BroadcastStateNow 即时 + 1Hz 节流）——本轮核对无回退，未重复实现；真机验收中。

## 本轮修复（beta.9 迭代三：WorldDroppedItem Pickup 重构 —— 恢复原版直进背包，fixed in code）

- **方向反转**：不再模拟 Cursor 拾取（grabItem/pickedUpItem/手工 Cursor UI 全部移除），恢复 Darkwood 原版语义：地面拾取**直接进背包**（与单机一致）。
- **Client**：点击 WorldDroppedItem 只发 `PickupRequest`（payload=ItemType+Amount 供校验），不执行 grabItem/pickedUpItem/拾取动画；ack=权威 `InventorySnapshot` → `ApplyPlayerInventory`（原版 createItem 初始化 Icon/Sprite/UI）。
- **Host**：验证 Entity 存在 → 距离合法（>4f 拒绝 TOO_FAR）→ 物品仍存在 → 玩家背包空间（shadow.CanFit，满则 INVENTORY_FULL）→ 用**真实原版 DroppedItem 数据**（dropped slots[0].invItem，非手工构造）转移进权威 shadow（shadow.AddItem 自动堆叠/找空槽，效果同原版 transferItemAllToPlayer）→ `InventoryRevision++`（内置 Touch）→ ack=InventorySnapshot(revision) → despawn 广播（RuntimeEntityDespawn / persistent Despawn）。
- **删除**：`ReplayPickedUpWorldItem`、`DumpCursorWorld`（[CURSOR-WORLD]/[PICKUP-REPLAY] 日志随之移除；`CreateCursorVisualFallback` 保留——HeldToContainer swap 视觉兜底仍在用）。
- 新增 `PickupPayload` 协议（wire 变）。

## 剩余风险 / 已知限制

- HeldToContainer swap 客户端 Replay 依赖 `swapItems` 原版（pickedUpItem.slot 必须有效——World 拾取的 cursor 已挂恒存宿主槽，应安全）。
- 捕兽夹视觉动画/下陷细节未全部 typed（TriggerBlocker 已同步），真机后按场景补。
- 灯闪烁（flicker）是客户端本地视觉，不做同步（属 Level 1 纯视觉）。
- wire 变更：beta.8 → beta.9（新 Action 枚举 + 协议），两端必须同版本。