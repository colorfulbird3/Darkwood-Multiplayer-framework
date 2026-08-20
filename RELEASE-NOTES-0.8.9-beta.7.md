# 0.8.9-beta.7 更新说明

Darkwood Multiplayer Framework 首个对外发布版（beta.7）。此前的 0.8.9-beta.8/9/10 均为内部迭代，统一封装为本版本。

## 联机骨架（自 beta.6 起的核心变更）

- **实体身份 / Binding 清单 / 真实世界稳定门（World-stable gate）**：主机注册表真实扫描 + 指纹，连续稳定后一次性提交，向量同步。
- **Binding Matcher 三阶段匹配**：uid/type/path → uid/type/位置 → type/name/位置；同 UID 多组件（Dog+Inventory、Character+Inventory）按 ComponentType 区分，不再误判 ambiguous。
- **存档传输 codec 修复**：Binding 分块 Data/Hash 参数反转修复（100% 校验必失败 → 真机现能完整传输）。
- **存档剥离 `graph:null`**：savs.dat 的 `StaticSave.graph` 是 byte[]，剥离成 `""` 会导致反序列化崩溃丢角色；改为 `null`。
- **跨会话强制重载 / 回主菜单重载**：失败残留的脏世界不再复用；重载走原版 `LoadScene("Darkwood")` 主菜单中转，不再同场景重载崩溃。
- **连接稳定性**：版本不匹配握手失败时打印双方版本号；残留世界 fail-fast 明确提示，不再卡住。

## 世界状态同步（新增架构）

- **World State Adapter 架构**：`IWorldStateAdapter` + Registry（按具体类型优先匹配），wire 新增 `StateSchema/StatePayload` typed 通道——不再把几十种对象硬塞进通用 `EntityStateWire` 的 flags。
- 首批 Adapter：Character（纯视觉代理：关闭本地 AI/寻路/决策组件 + Rigidbody kinematic）、GenericItem / BearTrap（幂等赋值，禁止 toggle 语义）、Door / Window。
- `[WORLD-AUDIT]`：运行期输出未覆盖的大世界类型清单（用于逐步补齐 adapter）。
- 移除 Item `switchMe()` 的 toggle 应用；`Item.activate` 改为 Host 权威 intent（部分完成，发电机/灯光完整 transition 在后续版本）。

## 物品事务（HeldItem / Drop / Pickup / 容器）

- **HeldItem（鼠标手持物品）**：从容器 grab 到鼠标按原版语义吸附（copy constructor 保留 UI 图标 / 槽 / 整堆数量）；可放回背包指定槽（按槽放置 / 同类堆叠）或直接丢地；全部 Host 权威。
- **Drop 只解析一次**：来源判定优先级 光标手持 → 背包/快捷栏 → 共享容器 → 未解析阻断原版；联机下客户端绝不自行生成单机掉落物。
- **Runtime 实体生命周期统一**：掉落物 Spawn/Despawn 走 `RuntimeEntityDespawn`，彻底清理镜像 / Registry / EntityId，消灭“幽灵包袱”。
- **Host 销毁必广播**：stale 对象绝不 silent purge，转为权威 Despawn 广播（捕兽夹拆除等）。
- **Despawn 应用安全化**：Unity 对象已销毁也不会在清理注册表时抛异常（先收敛注册表，再 best-effort 视觉）。
- 容器 Grab 拿整堆（不是 1 个）。

## 稳定性 / 容错

- `CaptureDeltas` / `TickHost` 各子系统异常隔离（单实体/单子系统坏了不掉整个主循环）。
- 遍历全部改快照（消灭 “Collection was modified”）。
- 诊断增强：per-kind 同步统计（怪物/门/窗/物品/容器）、`[WORLD-LIFE]`、`[RUNTIME]`、`[HELD]`、`[RUNTIME-GHOST]`（客户端本地未注册掉落物扫描，联机下必须为 0）、F8 鼠标指向实体调试。
- 客户端 A* 导航图剥离后的防御：`WhereAmI` 空引用防护、无图 Path 请求静默（本地怪物 AI 保持关闭，主机是唯一权威）。

## 安装

1. 解压 zip，运行 `安装.bat`（自动备份原 BepInEx/plugins）。
2. 主机：进游戏世界 → 按 `F6` 打开联机面板 → 开始主机。
3. 客户端：主菜单按 `F6` → 输入主机 IP →连接（存档会自动下载加载）。

> 客户端请从主菜单直接连接；联机失败后若提示重启游戏，请重启后再连（避免残留世界）。日志统一在 `BepInEx/LogOutput.log`。

## 已知限制

- 发电机 / 灯光/事件类大世界对象的完整权威 transition 尚未完成（后续版本）。
- 捕兽夹的 armed/triggered/occupied 细分状态同步在后续版本补齐（拆除已同步）。
