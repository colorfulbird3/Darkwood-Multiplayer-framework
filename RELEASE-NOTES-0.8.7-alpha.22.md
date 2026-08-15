# Darkwood Multiplayer Framework 0.8.7-alpha.22

本版按用户要求将联机模型切换为 **MC 式信任模型（FIX-011）**：正常联机后，客户端操作不再经过主机的距离/权限判断，直接本地执行并把结果同步给主机广播——不再出现"被主机拒绝"。

## 信任模型改动（FIX-011）

- **删除全部距离/权限校验**：拾取、容器拿取/放入、近战攻击、开关门、封窗、物品开关的 TOO_FAR / POSE_MISMATCH / STALE_REVISION / DOOR_BARRICADED / ITEM_UNAVAILABLE 判断全部移除。保留的只有"执行前提"检查（实体存在、槽位存在、库存可容纳等数据完整性检查）。
- **客户端本地直接执行**：门、窗、物品（灯/柜子等）操作在客户端立即生效（不再等主机批准），随后把结果状态发给主机。
- **物品开关（含开柜子）**：客户端执行后报告 `isOn` 结果状态；主机直接应用该状态并广播——**主机不再调用 `activate()`**（避免在主机侧弹出容器 UI）。
- **容器拿取/放置**：仍由主机执行库存事务并广播（保证双方库存一致），但不再校验距离与版本。
- 已删除的无用常量 `InteractDistance`、`AttackPoseTolerance`。

## 加载诊断增强（针对 alpha.21 复测的"卡 0%"）

alpha.21 复测时客户端三次连接均在 Load 启动前断开（日志缺失 Load 入口痕迹）。本版新增两条诊断日志：
- `正在切换到章节场景 chapterX 并启动存档恢复…`（LoadScene 前）
- `客户端 SaveManager.Load 入口已触发…`（Load 入口）

复测若再卡 0%，日志可立刻区分：①LoadScene 前就断；②场景加载了但 Load 未启动；③Load 启动后卡在内部某阶段。

## 验证状态

- `dotnet build` 0 warning / 0 error；SelfTests 61/61。
- 协议 wire 无变更（ItemActivate 的 payload 复用现有 InteractPayload，携带 isOn 值）。
- 待真机复测：柜子交互 + 各操作同步 + 客户端加载链。

版本：0.8.7-alpha.22。
