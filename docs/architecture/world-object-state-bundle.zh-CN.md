# World Object State Bundle —— 通用世界对象同步（owner-binding + 多 typed payload）

> 状态：Phase 1（wire 模型）已提交（companion dd8684b，Framework 0.8.9.4-pre.1）；Phase 2（owner-binding 捕获/应用）代码已实现、build+SelfTests 绿（companion 里程碑提交后），**游戏内/真机待验**。对应规格「World Object Synchronization System」架构落位，取代先前 PLANNED 的 `docs/problems/world-state-owner-binding.md` 单点方案。
> 基线：0.8.9.3-pre.1（companion commit 508c24d）→ wire 0.8.9.4-pre.1。用户选定方向 A（2026-09-06）。

## 1. 为什么不是「Furniture/家具」

反编译 Assembly-CSharp 全量类型清单（1628 类）确认：Darkwood **没有 Furniture / 可推动物类族**。世界道具 = Item 预制体 + 专用状态组件（Generator / ItemLight / TriggerBlocker / Padlock / Locked / Workbench / Saw…）。因此「通用世界对象同步」的正确落点是把这类**多组件复合对象**的全部状态纳入既有 typed-adapter 骨架，而不是新建物体类别。

## 2. 现状问题（已核实，0.8.9.3-pre.1）

- 每实体 wire 只允许**一个** typed payload：`EntityStateWire{…, ushort StateSchema, byte[] ExtraState}`（`src/.../Protocol/Common/ReplicationProtocol.cs` L7-20；codec L51/L170-171，单 payload ≤4096B）。
- Host 捕获/应用把 typed 状态挂在 **primary 组件**上：`DarkwoodEntityReplication` 的 `entities[id]` 只存一个 primary（多为 Item），`CaptureDeltas`/`Apply` 用 `Adapters.Resolve(primary)` 单 adapter（`DarkwoodEntityReplication.cs` L74-78/L94-98）。
- 后果（对照 `docs/problems/world-state-owner-binding.md`）：发电机/灯/夹子这类对象，周期路径只发 primary（GenericItem 字段），`Generator.fuel/lowPower`、`Light.destLightIntensity`、`TriggerBlocker` 等**复合组件 typed 状态发不出去**；只有事件即时路径（`BroadcastStateNow`/`HandleStateObjectInteractRequest` 对 binding.Primary 直接 `is Generator` 判别，`DarkwoodAdapterRuntime.Entities.cs` L242-279）能触达部分 typed 状态。
- 文献佐证：`docs/problems/stateful-object-sync.md` ——「TriggerBlocker、ItemLight、ItemSounds、GameEvents、Constructible … 实体存在但 State 没同步」。

## 3. 目标架构（一个 EntityId = 一个绑定根 + N 个 typed payload）

```
EntitySync 中 EntityStateWire：
  EntityStatePayload[] Payloads     // 每项 = { ushort Schema; byte[] Data }
绑定根（Item / Door / Window / Character / Inventory）
  ├─ GenericItem  (schema 4)      ← primary 本体状态（原有）
  ├─ Generator    (schema 7)      ← 同 GO / 子 GO 的 Generator 组件
  ├─ Light        (schema 8)      ← ItemLight 组件
  ├─ BearTrap     (schema 5)      ← BearTrap/TriggerBlocker 组件
  └─ …新 adapter 按需登记（Padlock/Locked/Workbench…）
Host: 对绑定根及其子组件逐个 Resolve adapter → Capture → 打包为 Payloads
Client: 对每个 payload 按 schema 找 adapter → 在绑定根/子组件上 Apply（幂等赋值）
```

原则沿用不变量：Host 唯一权威、Apply 幂等（set 非 toggle）、typed 标量序列化（不序列化 GameObject）、事件即时 / 状态对象 1Hz 节流、wire 变化即框架版本递增、无向下兼容。

## 4. 分阶段实施

### Phase 1（本次）：wire 多 payload 模型（纯逻辑，可独立验证）
- `EntityStatePayload{ushort Schema; byte[] Data}`；`EntityStateWire` 增加 `EntityStatePayload[] Payloads`。
- **源码兼容**：保留既有 17/19 参构造与 `StateSchema`/`ExtraState`（= Payloads[0] 的派生便捷属性），既有读写点零改动；wire 编码改为「payload 数(≤8) + 每项 (schema, bytes≤4096)」。
- `Changed()` 按 payload 逐项比对；常量 `MaxStatePayloads=8`。
- `ProtocolVersions.Framework` 递增 → `0.8.9.4-pre.1`（wire 变，握手门自动生效）。
- SelfTests：多 payload roundtrip + 负数（payload 数超限 / 单 payload 超限）。
- 验收：build 0 错 + SelfTests 全绿。Adapter 行为暂不变（只发 0/1 个 payload）。

### Phase 2：owner-binding 捕获/应用（真实能力跃迁）
- Replication 增加 `CollectStatePayloads(bindingRoot)`：对根及 `GetComponentsInChildren<Component>()` 逐个 `Adapters.Resolve` → 聚合去重（按 schema）→ 打包进 `EntityStateWire.Payloads`（保留 legacy primary 单字段路径结果）。
- Registry 扩展：`TryGetBySchemaId(ushort)`、`ResolveAll(Component[])`；adapter 加可选 `CanApplyTo(Component)`（apply 目标发现：根/子组件按 adapter 类型找）。
- Apply 改为遍历 payloads：按 schema 找 adapter → 目标组件 → `adapter.Apply`（幂等）。兼容旧包语义（首个 payload 仍驱动旧单路径）。
- 接入 Generator/Light/BearTrap 全 typed 周期同步；事件即时路径（`BroadcastStateNow`）改为复用同一 collect 逻辑。
- 新适配器试点（后续按需）：Padlock/Locked 门锁状态（DMF 当前零覆盖）等。
- 验收：build/SelfTests + 游戏内回环（F7）+ 双实例场景（真机需用户存档环境）。

### Phase 3：验收与发布
- 真机双端矩阵（含 world-state-owner-binding 文档 TEST G/H/I：BearTrap/Generator/Reconnect）。
- 场景测试补充；RELEASE-NOTES；companion 提交与（用户要求时）Release。

## 5. 改动点清单（Phase 1 + Phase 2 引用面，全仓 70 处 grep 依据）

- `Protocol/Common/ReplicationProtocol.cs`：EntityStateWire / EntityStatePayload / WriteEntities / ReadEntities / Changed（codec 在 ReplicationProtocolCodec，`DarkwoodWorldSnapshotCodec` 复用 Encode(EntityDeltaMessage) 无需改）。
- `SelfTests/Program.cs`：EntityDeltaRoundtrip（L155）+ 新增用例。
- `DarkwoodEntityReplication.cs`：CaptureDeltas(L74-78) / Apply(L94-98) / CaptureAll / CaptureNow / Changed(L287) / QueueAuthoritativeDespawn(L51) / ForceDespawn 等实体复制逻辑（Phase 2）。
- `WorldState/WorldStateAdapter.cs`：IWorldStateAdapter + registry 扩展（ResolveAll / TryGetBySchemaId）；`WorldStateSchemas` 登记新 schema。
- `WorldState/WorldStateAdapters.cs`：adapter 补 apply-target 发现；Phase 2 试点 adapter。
- `DarkwoodEntityScanner.cs` / `WorldState/WorldEntityBinding.cs`：绑定根如何携带复合组件（开放问题，见 §6）。
- `DarkwoodAdapterRuntime.Entities.cs` L242-279（StateObjectInteract 事件路径）；`DarkwoodAdapterRuntime.cs` adapter 注册点。

## 6. 开放问题（实现前须核实，反编译/实机取证）
1. 复合对象在 authoritative registry 的 primary 到底是 Item 还是 Generator/ItemLight？（`HandleStateObjectInteractRequest` 现按 binding.Primary `is Generator` 判别，与扫描五类冲突——需查 `DarkwoodEntityScanner` 与 binding 构造。）
2. Apply 目标发现策略：schema→目标组件类型 的映射（adapter 需要显式声明主目标类型，避免 GetComponent 遍历猜测）。
3. Runtime 实体（掉落物/敌人代理）binding 的复合组件同样适用？（`RegisterRuntimeEntity`/`RegisterBinding` 路径）
4. 快照字节增长：多 payload 全量快照上限与分块预算（现 256 KiB/块）。

## 7. 纪律
wire 变 → `ProtocolVersions.Framework` 递增 + 两端同版本；SelfTests roundtrip/负例先行；不 push / 不覆盖运行时 DLL / 不启动游戏除非用户明确要求；companion 提交为里程碑提交（canonical → companion 镜像规则同 508c24d）。
