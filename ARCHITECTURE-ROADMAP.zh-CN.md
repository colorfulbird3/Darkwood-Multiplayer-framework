# Darkwood Multiplayer Framework — 架构路线图

> 最后更新：0.8.9-beta.2（2026-08-17）

## 当前状态

| 项 | 值 |
|---|---|
| 当前发布版本 | **0.8.9-beta.2**（所有权拆分：RuntimeEntity/Combat/Player/SaveState 四服务 + EntityStateAdapter + Protocol 11 子目录 + xUnit 15 项） |
| 集成验证 | **VERIFY-001 核心玩法矩阵通过**（0.8.7）；**VERIFY-002 回环自测全链路通过**（详见 `docs/VERIFY-002.md`，0.8.9-beta.2 重构后复测通过）；0.8.8 新功能双机矩阵进行中 |
| 下一开发版本 | 0.8.9 剩余工程（SelfTests 全量迁 xUnit / RuntimeEntityService 字典收拢 / UDP-KCP 评估）或新玩法 |
| 权威模型 | **Hybrid Authority / Trust Mode**（见下） |

版本路线：

```text
0.8.7-beta.1            ← 0.8.7 收口（VERIFY-001）
        ↓
0.8.8-alpha.1..6        ← Runtime Entity 生命周期 + 场景切换 + 回环自测（已完成）
        ↓
0.8.8-beta.1..5         ← 封版 + Container Revision + 实机二轮修复（掉落物/夹子同步/营救/Despawn 定向）
        ↓
0.8.9-beta.2            ← 十刀架构重构（本版：partial 拆分 + SessionContext + MessageRouter + 协议领域文件 + Transport 真接口 + Host/Client Tick 分离 + Runtime Entity 生命周期模型 + xUnit）
        ↓
0.8.8/0.8.9 实机矩阵    ← 双机验证 Runtime Entity / 掉落物 / 场景切换 / 长时稳定性
        ↓
0.8.9+                  ← 剩余工程（xUnit 全量迁移 / RuntimeEntityService 收拢 / UDP-KCP 评估）→ 稳定性
        ↓
0.9.0-alpha             ← 恢复横向扩展玩法
```

---

## 权威模型：Hybrid Authority / Trust Mode

架构已从"全 Host Authoritative"演变为混合权威。共享容器、门窗、物品交互在 0.8.7-alpha.22/23 起改为**信任模式**：客户端本地直接执行、状态上报主机同步。不要再笼统称整个项目为"Host Authoritative"。

| 领域 | 权威方 | 说明 |
|---|---|---|
| 世界 / 存档 | **HOST** | 主机打包存档、剥离导航图、分块传输 |
| 敌人 AI / 血量 / 战斗结算 | **HOST** | 客户端不跑 AI；伤害由主机结算 |
| 玩家生命 / 倒地 / 营救 | **HOST** | 主机维护全员血量与倒地状态机 |
| 世界快照 | **HOST** | 权威快照 + 注册表摘要 |
| 玩家姿势 | CLIENT 输入 → HOST 转发 | 15 Hz pose 广播 |
| 共享容器 | **CLIENT 本地执行 → HOST 应用并转发** | InventoryState 上报，无审批 |
| 门 / 窗 / 物品交互 | **CLIENT 本地执行（信任）→ HOST 重放广播** | 无距离/权限校验 |

数据流（共享容器）：

```text
Client A 本地操作 Container
        ↓
InventoryState（含版本）
        ↓
Host Apply（应用到自身世界）
        ↓
Host Relay
        ↓
其他 Client
```

---

## 0.8.7-beta.2：Container Revision（容器并发保护）

信任模式带来并发风险：两个客户端同时操作同一容器，Host 直接 Apply 两份状态会复制物品（例：10 木板被 A 拿 3、B 拿 5，最终箱 5 + 背包 8 = 13）。**不回退到审批制**，只加轻量乐观锁：

- 客户端上报 `ContainerState { ExpectedRevision = 10, NewRevision = 11 }`。
- Host：`current == Expected → Accept`；否则 `Conflict → 回发最新状态`，客户端本地 UI 以最新状态刷新。
- 仍保持"本地即时操作"体验，仅冲突时纠正。

SelfTests：`container revision conflict`。

---

## 0.8.8：Runtime Entity 生命周期（主开发线）

### 背景（真实已发生的问题）

alpha.20 真机：Host 快照 755 个容器，Client 只能绑定 753 个——**2 个运行时生成对象**（乌鸦群、动物尸体）不在存档中，客户端仅靠加载 Host Save 无法生成。当前临时方案：缺失比例 ≤ max(10, 5%) 时记入 `missingEntities` 跳过并允许 READY（FIX-007）。**0.8.8 的第一刀就打这里**，把它们纳入网络世界。

### 拆分（不一次写完）

#### 0.8.8-alpha.1：Runtime Entity 协议

新增消息（当前协议完全没有）：

```csharp
RuntimeEntitySpawn
{
    RuntimeEntityId   // 只能由 Host 分配
    EntityKind
    PrototypeId
    Scene
    Position
    Rotation
    InitialState
    ServerTick
}

RuntimeEntityDespawn
{
    RuntimeEntityId
    ServerTick
    Reason
}
```

#### 0.8.8-alpha.2：RuntimeEntityRegistry

Runtime Entity 不再混进 Persistent Registry：

```text
EntityRegistry
├── PersistentRegistry
└── RuntimeRegistry
```

`AllocateRuntimeId()` 已存在，接入网络生命周期：

```text
Host 发现新 Runtime Entity
        ↓
AllocateRuntimeId()
        ↓
Register
        ↓
RuntimeEntitySpawn → Clients
```

**ID 纪律：整个 Session 单调递增，绝不复用**（销毁的 #3 不再分配给新对象），否则晚到的 `Despawn #3` 会误杀新生 #3。

#### 0.8.8-alpha.3：Runtime Dropped Item（最小闭环）

第一版只做一种实体验证全链路：Host 动态生成物品 → `Runtime ID 1001` → Spawn → Client 出现；Host 拾取/删除 → `Despawn #1001` → Client 消失。

验收：`Spawn → Delta → Interaction → Despawn` 四步全过。

#### 0.8.8-alpha.4：Runtime Enemy

Host：实例化 + AI + 寻路 + 血量 + 攻击 + 死亡；Client：纯远端代理（冻结 AI，插值跟随）。

- 生成：`RuntimeEntitySpawn` → Client 生成代理 → 15 Hz `EntityDelta` → 插值。
- 死亡：`Despawn Enemy #1001`；有尸体/掉落时 `Spawn Corpse #1002`、`Spawn Item #1003/1004`。

#### 0.8.8-alpha.5：Hot Join 的 Runtime Snapshot

世界快照扩展：

```text
WorldSnapshot
├── Persistent Entity State
├── Container State
├── Player State
└── RuntimeEntityState[]   ← 新增
```

晚加入的客户端从快照恢复所有存活 Runtime Entity（#1001 Enemy、#1002 Corpse、#1003 Drop），否则 Runtime Entity 只支持"在线时看见"。

#### 0.8.8-alpha.6：Scene Transition

```text
READY
   ↓
SceneChangeBegin（暂停 Action / EntityDelta / InventoryDelta / Combat）
   ↓
Host Load New Scene / Client Load New Scene
   ↓
Persistent Registry Build → Stabilize
   ↓
Runtime Snapshot → World Snapshot
   ↓
SceneReady → READY
```

**切场景期间绝不再发旧场景的 Delta**（stale packet 直接丢弃）。

### 0.8.8 SelfTests 新增清单

```
runtime id allocation
runtime spawn roundtrip
duplicate runtime spawn
runtime despawn roundtrip
despawn unknown id
spawn → delta → despawn
late join runtime snapshot
scene transition stale packet
```

---

## 0.8.9：稳定性（几乎不加玩法）

- Reconnect / Retry / Timeout / State recovery / Diagnostics / Long-run test
- 重点用例：Action retry、Snapshot retry、Runtime Spawn/Despawn duplicate、Late packet、Scene stale packet、Disconnect during scene load / snapshot。

---

## 0.8.7 已知欠账（当前存在、暂不阻塞）

1. **运行时生成物不同步**（乌鸦/动物尸体等）：FIX-007 容忍跳过，0.8.8 解决。
2. **容器并发复制风险**：0.8.7-beta.2（Container Revision）解决。
3. **拾取仍走主机审批**（Pickup Action）：信任模式改造未覆盖，观察后再决定是否本地化。
4. **近战攻击仍走主机结算**（伤害由主机影子武器库推导）：0.8.8 Runtime Enemy 后统一调整。
5. **场景切换未实现**：0.8.8-alpha.6。
6. **热加入期间运行时对象缺失**：0.8.8-alpha.5。

## 历史里程碑

- alpha.20：首次完整双端连通（READY + 双方可见 + 事件同步）。
- alpha.21：修存档损坏（FIX-008 扩容账本 token）。
- alpha.22：信任模式第一步（FIX-011 删除距离/权限校验）。
- alpha.23：容器交互客户端本地权威（FIX-012），核心玩法矩阵真机通过。
