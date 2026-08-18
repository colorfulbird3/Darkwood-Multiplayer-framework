# Darkwood Multiplayer Framework 0.8.9-beta.4

0.8.9 世界层权威同步（第 2-6 刀全量）。**一切世界状态变更的最终裁决者只有 Host。**

## 第 2 刀：Drop + Pickup 全 Host Authority

- 客户端扔东西不再本地产生世界结果：`InvSlot.dropItem` 拦截 → `DropRequest`（只含槽位/数量/落点）→ Host 执行。
- Host 执行 `DarkwoodWorldAuthorityService.DropItem`：peer=0 读真实背包；peer>0 读权威 shadow（**客户端不传物品属性**）；调用原版流程创建掉落物（DroppedItem 预制）→ 分配 RuntimeEntityId → 注册 binding → **立即广播 Spawn** → 背包状态随 ActionResult 返回。
- 客户端收到 Spawn 创建**可交互镜像**（DroppedItem 预制 + 保留碰撞器 + isDroppedItem）——点击拾取经拦截转发 Host。
- Pickup：Host 权威（读容器 → shadow 校验/添加 → 扣容器 → Despawn 广播 → 背包状态返回）；统一 `TryGetItem` 解析。

## 第 3 刀：Container Authority（撤 TrustMode）

- 客户端不再"先改后上报"：`transferItemToPlayer / transferItemAllToPlayer / transferItemTo / grabItem / controllerPickUpItem` 全部拦截 → `ContainerTake / ContainerPut` Intent。
- Host 事务：容器扣/加 → shadow 加/扣 → 容器 revision++ → **立即广播权威容器状态** → 背包状态返回。
- 客户端容器状态上报路径（TrustMode）正式撤除；Host 本地容器操作后立即广播。

## 第 4 刀：Generic Item Interaction

- `ItemInteract` 通用意图：Host 应用状态（searched 等）并立即广播。
- 交互后立即广播（不再只靠 15Hz 轮询兜底）。

## 第 5 刀：Trap / 特殊物品

- 反编译确认：Darkwood 捕兽夹没有独立 Trap 类——夹子是 `Item`（armed 走 `isOn`，第 2 刀已有主机权威），被困状态在 `Character.inBearTrap`。
- `EntityState` 新增位 5：`inBearTrap` 同步（踩夹子的角色在客户端镜像上正确表现被困）。

## 第 6 刀：Reconciliation

- 重复请求防护（`ActionIdempotencyCache`）+ 3 个 xUnit 幂等测试。
- 已有机制确认在位：乱序 revision 丢弃、Despawn 幂等（未登记忽略）、快照/晚加入、自动重连、扫描器降级为审计器。

## 测试

- 单元测试 27 项通过（+3 幂等）
- SelfTests 81 项通过
- 回环自测全链路通过

## 已知问题

- 客户端拖拽容器内重排（placeItem/swap 在共享容器内）仍本地执行，会被主机权威广播覆盖（最终一致）。
- 搜尸体（search）物品进背包路径尚未 Host 权威化（客户端本地执行，背包差异待下版）。
- 真机双机测试尚未完成。
