# Darkwood Multiplayer Framework 0.8.8-beta.2

**0.8.8 开发线收官封版**：Runtime Entity 全链路 + 出生点修正 + 场景切换 + 本地回环自测。

## 0.8.8 全部内容（alpha.1 → alpha.6）

### Runtime Entity 运行时实体（全新协议）

| 阶段 | 内容 |
|---|---|
| alpha.1 协议 | `RuntimeEntitySpawn`/`RuntimeEntityDespawn` 消息 + `RuntimeEntityKind`（DroppedItem/Enemy/Corpse/LootContainer）+ 严格 codec（非法 kind/reason/ID=0 直接拒绝） |
| alpha.2 注册表 | `RuntimeEntityRegistry`：ID 由主机分配、会话内单调递增、**绝不复用**；duplicate spawn / despawn unknown 容错；`ClearAlive` 保留计数器（场景切换 ID 连续） |
| alpha.3 随机事件容器 | 乌鸦群/动物尸体等运行时 deathDrop 容器：**35 米范围门控**（客户端进入范围才单播 Spawn）+ **一次性动画**（触发后离开再进入不重播，按玩家独立）+ 初始库存随 Spawn 携带 + 客户端镜像禁碰撞器（防物品复制） |
| alpha.4 运行时敌人 | 夜间事件怪等非存档敌人：范围门控 Spawn → 客户端实例化 AI 冻结代理 → 注册进实体复制表 → **15Hz delta 自动驱动**（位置插值/血量/攻击/死亡动画）；尸体转 deathDrop 容器后自动衔接 alpha.3 机制 |
| alpha.5 热加入 | 新客户端 READY 后立即进入门控派发循环——出生点附近的存活运行时实体 66ms 内触发，远处事件等走近再触发；**FIX-013：客户端在游戏默认出生点出生**（playerBase 的 playerSpawn，与单机新游戏一致，不再在主机位置） |
| alpha.6 场景切换 | 主机切场景（chapter1/2）→ 广播 `SceneChange` → 客户端 3 秒后**自动重连**（完整握手 + 新场景存档加载）；主机重置运行时状态（ID 继续递增）；两侧按场景名隔离增量 |

### 本地回环自测（新增能力）

- 配置 `Gameplay/SelfTestAuto = true`：启动后**全自动**执行——开主机 → 自动读档 → 主机 READY → 回环客户端（127.0.0.1）→ 完整协议链（握手/真实存档传输+SHA-256/权威快照/访客档案/READY）。**实测 9 秒全链路通过**，日志带 `[自测 Ns]` 时间戳。
- 手动模式：F7 启动 / F8 停止。
- 正常联机保持 `false`。

### 修复

- **FIX-014**：RescueOverlay 静态构造在 OnGUI 外访问 `GUI.skin` → 启动崩溃，改懒初始化。
- **PrepareSave 健壮性**：主机档案未激活时自动从档案列表恢复（quickLoadGame 等路径）。
- 加载分段诊断（0.8.8-alpha.3 引入）：Load 入口 → 导航图跳过 的耗时日志，用于排查卡 0% 问题。

## 验证状态（VERIFY-002）

- 构建 0 警告 / 0 错误；SelfTests **78/78**。
- **回环自测全链路通过**（本机实测，9 秒：握手 → 真实存档 1,013,859 字节 SHA-256 → 权威快照 → 访客档案 → READY）。
- 访客档案热加入持久化验证（self-test 身份二次加入 join 计数递增）。
- **实机矩阵待测**（诚实标注）：出生点/范围门控/怪物代理/场景切换的真人双机验证，随 0.8.8-beta 测试轮进行；详见 `docs/VERIFY-002.md`。

## 兼容性

- 无向下兼容（既定策略）：握手比较 FrameworkVersion+GameVersion，本版 wire 变更随版本绑定。
- 要求：BepInEx 5.4.x、Darkwood 1.4、双方版本一致。

## 已知边界

- 开服**之前**已生成的运行时对象仍走快照容忍路径（热加入正式快照改造评估中）。
- 自测路径（quickLoadGame）下出生点回退 (0,0,0)、快照实体数偏小——为快速读档的时序特性，真实客户端（正常读档主机）不受影响。
