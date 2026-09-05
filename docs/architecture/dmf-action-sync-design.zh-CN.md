# DMF Action Sync —— 三层同步架构设计（Phase 1 分析稿，未重构）

> 状态：DESIGN / 待确认（2026-09-06）。依据：DarkwoodCoop Alpha 逆向（LampSync/GeneratorSync/DropSync/TrapSync，见 coopmod-analysis.md）+ DMF 0.8.9.5 现状与真机问题记录。
> 原则：**凡带原版逻辑副作用的对象，同步=执行原版函数**；State Sync 只做字段/慢态与最终一致兜底；禁止绕过原版直接改关键字段（isOn 等）。

## 0. 结论先行（我的评估）
赞成引入 **Action Sync 层**，但建议采用**三分法**而非全量替换：
- Action Sync：交互/副作用迁移（Generator/Lamp/Drop/Trap/…）——Host 权威执行一次，广播 **ActionExecuted**，各端在 ApplyingRemote 作用域内**重放同一原版函数**（视觉/电源/破坏副作用两端一致）。
- State Sync：保留现状（EntityDelta + typed payload + 1Hz/15Hz）作为慢态/最终一致与晚加入快照的**兜底**（不能删——晚加入者没有事件历史）。
- Event 即时通道：门等高频小对象事件化（r5 门 A 已实现 Host 单执行 + 广播）。

风险与护栏（见 §4）：重放必须幂等 + 去重（tick/requestId）、ApplyingRemote 防回环、杜绝双执行（host 单次）、不变量闸（禁重新引入审批、Host 权威、状态幂等）。

## 1. 当前同步方式问题（真机证据）
1. **纯字段赋值无法复现原版副作用**：发电机只赋 isOn/fuel → 客户端电源网络不跑 → 灯不亮（真机：客户端灯永不亮、只有主机灯跟随）。
2. **本地执行 + Host 再执行 = 双执行/分叉**：门/陷阱的 blocked 双端判定不一致（门已修 A；夹子拆除仍只有客户端本地执行 → 主机残留）。
3. **丢弃物远端用通用 DroppedItem + 缺少收养**：视觉错（玻璃瓶）、复制、延迟出现（主机扔→客户端不显→下次动作才显）——真机复现。
4. **State 周期捕获与事件竞态**：BroadcastStateNow 只广播发生变化的单实体，电源网络批量灯没有即时广播（r6 已补 powerItems，仍需重放机制）；客户端 1Hz 后置纠正造成「看起来没反应」。
5. 无统一「对象行为意图/执行回放」消息：意图散在 DoorInteract/ItemActivate/StateObjectInteract/Combat 各处，行为结果只以字段回传，丢失「函数副作用」。

## 2. 需新增模块
1. `Protocol`：**ActionExecuted 事件组**（Host→All）：`ActionExecuted{EntityId, ActionKey, Param, Tick}`（复用现有 tick；key 如 generator.turnOn / trap.destroy / drop.spawned / door.opened…）+ codec（wire 版本递增：0.8.9.5 → 0.9.0-pre.x）。
2. `DarkwoodAdapter/Actions/`：**ActionSyncManager**（注册端）：`ExecuteHost(id, actionKey, param)`（Host 唯一执行原版）与 `Replay(id, actionKey, param)`（各端 ApplyingRemote 重放）；幂等去重（(entityId, actionKey, tick) 最近 N 缓存）；路由表 ActionKey→(Execute 委托, Replay 委托, 需要 Apply 的字段后处理)。
3. `WorldEntity` 视图层已建（P0 WorldEntityView），接入 Action 定位。
4. SelfTests/xunit：ActionExecuted roundtrip/去重/未知 key 负例。

## 3. 需修改模块
- `Generator`（示范）：Host 侧已有 HandleStateObjectInteract→turnOn/Off；改为**广播 ActionExecuted(generator.turnOn)**；客户端收到 → Replay 调 `g.turnOn()`（=r7 本地镜像，正式化、事件化，不再靠 1Hz 推断）；**禁止直接赋 isOn**；fuel 仍 Host 权威 typed 覆盖。
- `Lamp/Light`：删除「靠 ItemLight typed 驱动」作为主路径的期望——灯状态**由本地电源网络在 Replay turnOn/powerDown 后自愈**；typed Light 仅做晚加入/兜底。
- `Drop`：远端镜像 = **收养(2m/同类型/未入网) 或 ItemsDatabase 原型 prefab 创建**（r7 已落地）；Host 在检测到新掉落（含 DropCommit、自身 vanilla、扫描）时广播 ActionExecuted(drop.spawned{runtimeId,type,…})；删除不必要字段推导。
- `Trap`：新增 **ActionExecuted(trap.destroy{runtimeOrPersistentId})**；拆除/触发→Host 执行原版销毁→广播→全端 Destroy（先 RE 原版拆除方法）。
- `Door`（已完成 A）：迁入 ActionExecuted 门 toggle 事件（统一路由）；保留 Host 单执行。
- `DarkwoodAdapterRuntime*`：handler 路由把 3 类 intent 的「成功结果广播」统一走 ActionSyncManager；ApplyingRemote 期间禁止 intent/重入。
- `IWorldStateAdapter`：语义收敛为**慢态捕获/兜底**（不再承担副作用触发）。

## 4. Action Sync 设计方案（核心）
消息：`ActionExecuted { EntityId; byte ActionKey; byte[] Param; long Tick; EntityId? ActorId }`（Host→All，Reliable）。
流程：
```
客户端意图（本地不执行） → Host: 校验/单次执行原版函数 → 捕获权威字段 → 广播 ActionExecuted + 权威 StateDelta
→ 各客户端: ApplyingRemote=true; Replay(原版函数, 参数); 应用权威字段; ApplyingRemote=false; 去重缓存(tick)
```
关键决策：
- **谁执行**：Host 永远是唯一「逻辑执行者」；客户端只「视觉/环境副作用重放」（turnOn 的 restorePower、trap 的 Destroy、drop 的 AddPrefab/收养）。禁在客户端执行有数值权威副作用的部分（fuel drain 不做——drain 仅 Host；客户端 turnOn 后本地 drain 由 Host fuel typed 覆盖）。
- **幂等/去重**：ActionSyncManager 按 (entityId, key, tick) LRU 去重；重放不得依赖本地猜测（禁止「本地先执行再上报」回退）。
- **回环防护**：ApplyingRemote 内 Replay 调用的原版方法若再命中 Patch → 直接放行（不再发 intent）；已有的 BeginRemoteApply/EndRemoteApply 复用。
- **与 State 关系**：Action 是「加速器」，StateDelta/1Hz/快照是「真相/兜底/晚加入」——两者可重复应用（Apply 幂等）。
- **范围**：World Object（Generator/Lamp/Trap/Door/Drop/可破坏物）先做；Character/Enemy AI 不动（仍冻结代理）；Inventory 事务不动（已 Commit/Reconcile）。
- **纪律**：wire 变化随 0.9.0-pre.x 发布；SelfTests/xunit roundtrip+负例先行；每 Action 一例真机验收清单。

## 5. 实施顺序（建议）
A0 架构落地：Protocol ActionExecuted + ActionSyncManager 骨架 + SelfTests（可离线）。
A1 Generator/Lamp 事件化迁移（替换 r7 推断式镜像）→ 真机灯双向验收。
A2 Drop Action（spawned 事件化，保留收养/原型）→ 真机单份+视觉验收。
A3 Trap destroy 事件（先 RE 原版拆除方法）→ 真机拆除双向验收。
A4 Door 迁入统一路由 + 清理旧分支/死代码。
每步：build 0 错 + SelfTests + xunit + BOOT3 自动矩阵 + 真机点验。

## 6. 开放问题
- 原版拆除/部署夹子走的方法（RE 待做）；发电机 client turnOn 的 drain 数值策略（Host fuel 覆盖频率 1Hz 是否够）；Lamp isOn 是否仍需直接同步（防晚加入快照空窗）；Action 去重窗口大小。
