# Darkwood Multiplayer Framework 架构路线图

本文档记录 0.8.x 及后续版本的架构目标与**真实进度**。当前发布版本：**0.8.7-alpha.18**（构建 0 警告 0 错误，SelfTests 54/54，双端实机验证进行中）。

> 重要认知（2026-08-14 项目评审后更新）：项目当前主要矛盾不是缺功能，而是**实现速度超过验证速度**。
> 0.8.7 已从"Runtime Entity"演变为"Playable-system expansion + Hot Join + 客户端存档加载稳定化"；
> Runtime Entity 生命周期与 Scene Transition 是尚未偿还的底层欠账，已排入 0.8.8。

## 0.8.x 版本规划

| 版本 | 主题 | 状态 |
|---|---|---|
| `0.8.6` | Action Core：Pickup 的 Request → Validate → Apply → Result 闭环 | ✅ 已完成 |
| `0.8.7` | 玩法系统扩展（容器/战斗/门窗/物品 + 倒地营救）+ Hot Join + 客户端存档加载稳定化 | 🔧 代码完成（alpha.18），等待真机矩阵 |
| `0.8.8` | Runtime Entity：Host 分配运行时 ID，补齐 Spawn/Despawn 生命周期；Scene Transition | ⬜ 未开始 |
| `0.8.9` | Stability：断线重连、重复包、超时诊断；怪物伤害从距离近似模型收敛为真实攻击事件 | ⬜ 未开始 |
| `0.9.0` | 横向玩法扩展 | ⬜ 未开始 |

### 0.8.7 的真实内容

0.8.7 实际已实现（截至 alpha.18）：

- **Action 协议扩展**：Pickup、ContainerTake、ContainerPut、Attack、DoorInteract、WindowInteract、ItemActivate，统一走 `Client Intent → ActionRequest → Host Validate → Host Apply → ActionResult/Rejected`。
- **Hot Join**：ClientHello → GuestKey → Host Guest Profile；访客独立背包/快捷栏/位置/加入天数/次数；Host 持久化访客档案；SESSION_FULL 限员。
- **倒地与营救**：PlayerHealth / RescueRequest / RescueProgress / AllDowned；全员倒地才走原版死亡结局。
- **客户端存档加载稳定化（FIX-002~005）**：强制真实 Load 分支；跳过 joinPaths；主机剥离 A* 导航图后传输（实测 -64%）+ 客户端跳过图反序列化；剥离带运行时保护（字段缺失/重复/结构异常回退完整存档）。
- **注册表稳定化**：实体数连续 3 次不变才发送 Ready，避免世界流式生成期间注册表不完整。

### 0.8.7 已知欠账（不阻塞 beta，但必须记录）

- **Runtime Entity 生命周期未落地**：`AllocateRuntimeId()` 仅有 ID 分配，协议中没有 RuntimeEntitySpawn/Despawn；扫描器产出的对象仍全部走 Persistent ID。夜间新怪、动态掉落物、投掷武器等运行时对象尚未走完整生命周期链。
- **怪物攻击访客是近似模型**：Host 按距离扫描（≤1.6m + 0.5s 冷却）扣影子血量，不是真实攻击命中事件；0.8.9 前应收敛。
- **Scene Transition 未实现**：跨场景（梦境/地点切换）期间的 Action 暂停、Registry 重建与重新快照仍需设计。

## 0.8.7-beta.1 发布门槛（真机矩阵）

beta.1 的定义：**第一版"真实双端基本玩法验证完成"的 0.8.7**。不再产生 alpha.19 等横向迭代，冻结功能，按以下矩阵逐项真机验证：

```text
第一关（加载链）：
  Host 进游戏 → Client 加入 → 下载裁剪后存档 → 0→100% 加载
  → onFinishedLoading → Registry 稳定 → Snapshot → READY → 双方互相可见

第二关（玩法）：
  Pickup / Container Take / Container Put → Door / Window
  → Client 攻击怪物 → 怪物攻击 Client → Client 倒地 → 营救
  → 断线重连 → 第二个 Client Hot Join
```

## 联机生命周期

```text
CONNECT → VERSION_CHECK → SAVE_TRANSFER → LOAD_SAVE
→ ENTITY_REGISTRY → WORLD_SNAPSHOT → READY → LIVE_REPLICATION
```

客户端在收到 `READY` 前不发送玩家实体状态，也不处理实时交互请求。断线重连或切换场景后，必须重新执行 `WORLD_SNAPSHOT` 和 `READY`。

## 实体身份

- `WorldEntityId`：存档中的箱子、门、发电机、工作台和固定物品。
- `RuntimeEntityId`：敌人、投射物、临时掉落物等运行时实体，由主机分配。
- 禁止使用 Unity `GetInstanceID()`、名称或 Instantiate 顺序作为网络身份。

实体注册表必须在主机和客户端建立后比较 digest；不匹配时停止实时同步并记录明确原因。

## 主机权威与 Action 层

```text
Client Request → Host Validate → Host Apply → StateVersion++ → Replicate Result
```

拾取、丢弃、攻击、开门、制作和使用物品都应走统一 Action 协议。客户端只发送意图，不直接提交最终世界状态。

## 通用状态版本

容器现有的 `InventoryRevision` 应逐步抽象为通用 `StateVersion`，覆盖实体、门、敌人、发电机、容器和世界快照。客户端收到旧版本时直接丢弃，避免乱序消息回滚状态。

## 玩家同步

玩家状态保持约 15 Hz：位置、方向、移动/奔跑、瞄准和攻击状态。主机校验速度、场景和可行走范围；客户端使用插值显示远端玩家，不把每帧 Transform 当作最终权威状态。

## 实现优先级（更新后）

1. **冻结功能开发**：完成 0.8.7-beta.1 真机矩阵，不横向增加玩法同步对象。
2. Runtime Entity：协议增加 SpawnEntity / RuntimeEntitySpawn / Despawn 生命周期消息；扫描器区分 Persistent/Runtime 实体。
3. Scene Transition：切场景期间暂停 Action/Delta，重建 Registry 并重新快照。
4. Stability：断线重连、重复包、超时与诊断界面；怪物真实伤害事件（替代距离近似模型）。
5. 将 `InventoryRevision` 提取为通用 `StateVersion`。
6. 序列化、实体注册表、快照和断线重连测试持续扩充。
