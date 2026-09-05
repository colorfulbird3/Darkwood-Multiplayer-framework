# DMF ← Darkwood Coop 设计融合蓝图（Phase 1 设计稿，未开工）

> 状态：DESIGN / 待确认。2026-09-06。
> 原则：**迁移思想、不复制代码**；保持 BepInEx5 + Harmony + Telepathy TCP + Binary Protocol + EntityId + Snapshot + Runtime Entity + Hybrid Authority；不重写网络层/协议；不复制 Coop SyncClass 结构。
> 依据：DMF 0.8.9.4-pre.1 现状（companion 508c24d→3078d5d）+ DarkwoodCoopOnline 0.3.92 逆向（F:\darkwood\coop-decompiled + 备忘 docs/architecture/darkwood-coop-mod-notes.zh-CN.md）。
> 关键提醒：提案中大量条目在 DMF **已存在且更成型**——本计划按「✅ 只收尾不重写 / 🟡 补件 / ❌ 真缺口」推进，避免重复工程。

## 目标架构（层）
```
Darkwood Object → WorldEntity(薄视图 over 现有双 Registry+Binding)
      → IWorldObjectAdapter (typed schema + owner-binding 多 payload)
      → State(Snapshot/Delta) + Event(即时)
      → Binary Protocol(EntityDelta/WorldEvent/RuntimeSpawn…)
      → Host Authority (Host 存档=真相；Trusted Client 容器/门窗本地执行上报)
      → Client Replica (只收 Binding/Event，幂等 Apply，代理冻结 AI)
```

## 1) 现状 ↔ 新设计映射
| 提案 | DMF 现状 | 状态 | 动作 |
|---|---|---|---|
| WorldEntity 层/Registry | EntityRegistry(持久) + RuntimeEntityRegistry(单调不复用) + WorldEntityBinding + BindingManifest + world-stable 门 | ✅ 已有更强 | 薄 `WorldEntity` facade（不新协议） |
| SyncKey 名@坐标 | 权威描述符(ComponentType/Uid/Path/Name/位置降级)→Host EntityId，Client 只收 Binding | ✅ 一致更强 | 无 |
| 每域 Sync 类 | IWorldStateAdapter + Registry + Schema0..8 + 0.8.9.4 多 payload/owner-binding | ✅ 已有统一版 | 新对象=加 adapter/schema |
| Event+Snapshot 双模式 | Snapshot(WorldSnapshot+Binding)已有；对象事件已部分(intent/CaptureNow/BroadcastStateNow/RuntimeSpawn) | 🟡 世界级事件缺 | P3 |
| Container Revision/Hash/Sig/Reconcile | containerRevisions + 四类 Commit + Ack/Reconcile + 背包 revision 门 + 幂等缓存 | ✅ 已有更强 | 仅 sig 快比对优化 |
| Runtime Entity 统一生命周期 | RuntimeEntityRegistry + Spawn/Despawn(Drop/Enemy/Corpse/Loot) + DropRoundTrip | 🟡 | InitialState 落地、Runtime Enemy、热加入含 runtime 验证 |
| WorldState(Clock/Rain/Night/Quest/Flag) | 无 | ❌ 真缺口 | 新域 |
| Enemy/NPC 不同步 AI | Character typed + 客户端代理冻结 + Host 结算 + die2 收敛 | ✅ 已有 | NPC 离散状态/对话/任务(P6) |
| 协议扩展(Register/WorldEvent/Snapshot/Delta/Spawn/Destroy) | 均已有对应消息 | 🟡 | 仅新增世界事件/WorldState 消息组 |
| Harmony 事件驱动禁轮询 | 已事件驱动(15Hz 移动/1Hz typed/事件即时) | ✅ | 不学 Coop 物理轮询 |

## 2) 需新增模块
1. `DarkwoodAdapter/World/WorldEntity.cs`（薄视图：Register/Allocate/Bind/Resolve/Lifecycle 收口现状）。
2. **WorldState 域**：Protocol 新消息组(WorldEventFired/FlagChanged(Byte|Int)/ClockState/RainState/DialogueState/NpcState/ChapterAdvance…)+codec；`DarkwoodAdapter/WorldState/WorldStateManager.cs`（Host 单点：时钟/雨/夜事件/flag/任务/对话；Client 只读应用）。
3. Runtime Entity 补件：RuntimeEntitySpawn.InitialState；RuntimeEnemy 生命周期；热加入 runtime 快照验证。

## 3) 需修改模块
- Protocol：消息表+codec；`ProtocolVersions.Framework` 递增（0.8.9.4→0.9.0-pre.x，首个 wire 变化时）。
- `World/WorldStateAdapter*.cs`：新 schema/adapter 登记。
- `DarkwoodAdapterRuntime*.cs`：事件路由、WorldStateManager 装配、handler 注册。
- SelfTests/xunit：roundtrip/负例。
- 文档/版本（roadmap/RELEASE-NOTES/current-status）。

## 4) 数据流
```
事件流: Client 原版 → Postfix/Intent → (本地执行/上报) → Host 校验/信任+应用 → 广播事件/权威状态 → 幂等 Apply
快照流: 加入 → SaveBundle → 读档(隔离目录) → BindingManifest(只收) → WorldSnapshot → Applied → Ready → 15Hz+1Hz+事件
世界态: Host 单点(时钟/夜/flag/任务) → 变化即 WorldEventFired/… → 广播 → Client 只读镜像（最终一致由 1Hz/快照兜底）
```
对象变化优先事件即时；连续运动走 delta+插值；状态对象 1Hz+事件；Host EntityId 唯一、Client 不自造 id；不引入审批。

## 5) 开发顺序（修订版；✅项收尾不重写）
- P0 DMF-WorldEntity-Core：薄视图+契约文档（小）
- P1 DMF-WorldObject-Adapter：owner-binding 泛化 + 事件即时通道 + 试点 adapter（门锁/构造体）（中）
- P2 DMF-RuntimeEntity：InitialState + RuntimeEnemy + 热加入 runtime 快照（中大）
- P3 DMF-WorldState：WorldEvent/Flag 事件域 → Clock/Rain/Night 首批 → 任务/对话/章节（大，分批）
- P4 DMF-Container-Reconcile：sig 快比对 + 真机冲突矩阵（小中）
- P5 DMF-Character：死亡/HP/动画/倒地营救 真机矩阵（中）
- P6 DMF-NPC-AI：NPC 离散状态/对话/任务（依赖 P3）（大）
贯穿门禁：build 0 错 + SelfTests + xunit + 双端实机 TEST 矩阵；完成前不写 Real-machine verified。

## 6) 风险
- 高：范围失控/重复开发（✅收尾不重写）；wire 纪律；与既有不变量冲突（信任模式禁审批/幂等/Host 权威 → 过 AGENTS 不变量闸）
- 中：事件可靠投递（Reliable 通道 + 1Hz/快照兜底）；性能（throttle 分层 + 真机观察 GC）；AI 边界（只同步结果）；文档漂移
- 低：合规（仅思想迁移，不复制代码）
