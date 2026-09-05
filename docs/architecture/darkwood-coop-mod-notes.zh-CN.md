# Darkwood Coop Mod（Nexus #36）逆向与借鉴备忘

> 状态：研究归档（2026-09-06）。用途：记录社区「DarkwoodCoopOnline 0.3.92」的可证实事实与对 DMF 的可借鉴项，避免重复调研。
> 来源：①Nexus https://www.nexusmods.com/darkwood/mods/36 （作者 felipesfx，巴西；v0.3.92 2026-08-13；未开源、禁转载）②本人反编译其插件 DLL（本机 ilspycmd）→ F:\darkwood\coop-decompiled（138 cs）。
> 合规：仅借鉴思路与协议组织；不复制其代码（授权不明）。

## 一、技术栈（实证）
- BepInEx 5.x + Harmony；网络 = **Steam P2P 邀请/SteamID64**（免端口转发）；Epic/GOG 版走 LAN 模式；自研二进制包（PacketType 100+ 消息）；非 Mirror/Telepathy/Photon/LiteNetLib。

## 二、同步模型（作者原文 + 反编译）
- Host 权威；**Host 存档 = 世界真相**，客户端只读镜像。
- 同步域：玩家位置/动画、敌人刷怪/AI/死亡、门、家具、容器及内容、掉落物、陷阱、发电机、灯、窗板、锯、昼夜钟、雨、倒地/复活、夜事件/天数/章节（host 单点）。
- 实体身份：静态 = `对象名@x_z`（取整瓦片坐标）；尸体 = `corpse:<ownedId>`；运行时物=专属 Spawn 消息；玩家 = Steam/Goldberg/local GUID。
- 容器：逐容器 version + lastSyncedSig，失配冷却请求 + 5s 周期 reconcile。
- 结构：每对象类型一个 Sync 静态类（DoorSwing/DoorBarricade/DoorLock、Generator、Lamp、Trap(+Place+Spawn)、Saw、Workbench、Furniture、Prop、Enemy(51KB)、Shadow、Container、Drop、Clock、Rain、Night/WorldEvent、Quest、Flag、Dialogue、NpcState、Chapter、Dream、SharedVision、Voice、Pause…≈120 类）。

## 三、其已知问题（评论区/作者自述实证）
- 物理级联对象需高频轮询 → 卡顿源（作者自述）。
- 仓库移物消失、门加固失败、组合锁/烤箱/对话模态互斥卡死、换 Host 存档重置 Day1、host 击杀在 client 端 HP 残留+无法补刀（死亡未本地收敛）、NPC 只打 host、对话只播首句。修法=逐条补丁+双端 LogOutput.log。

## 四、对 DMF 的可借鉴项（★=实证锚定 / △=推论）
1. ★ 存档/会话 **identity 绑定**（防换档丢进度）→ 建议：SaveBundle/EntityId 清单加「存档实例会话锚」（小改，可自测）。
2. ★ 随机/夜事件/天数 host 单点决定 → 我们的 WorldEvent 域照此设计（消息：NightEvent/FlagBool/FlagInt/WorldEventFired 事件广播模式）。
3. ★ 模态交互（锁/烤箱/工作台同触）需要 **host 仲裁互斥令牌**而非全局 pause（我们暂无模态系统，若做直接采纳）。
4. △ 客户端插值代理优于其物理轮询（我们已 Character 代理冻结 AI + 15Hz 插值；不用学）。
5. ★ AI 离散状态位显式广播 + 客户端禁本地重复判定（我们死亡广播+本地 die2 收敛正是防「HP 残留/无法补刀」）。
6. ★ 物品转移 host 事务化（我们 ContainerCommit/InventoryTransaction + reconcile 更强）。
7. ★ 转场/传送/梦境/章节是雷区：host 先转、client 跟随同结果 + 场景对齐校验（补强我们 SceneChange/stale 丢弃为「同结果校验」）。
8. ★ 明确「不同步为预期」白名单（迷雾/光照/可见性）。
9. △ Steam P2P 免端口转发 = 可选传输适配器（我们的 P3 预留 UDP/KCP/新适配器时评估；Telepathy 留 LAN/direct）。
10. ★ 双端 LogOutput.log 诊断惯例（我们已有 TestHarness trace + SYNC-HEALTH 计数器，更强）。

## 五、诚实边界
- 对方无源码/协议文档，无法同口径对比；本文所有「我们更强」均为推论性判断。
- 深层反编译走读（EnemySync 51KB 等逐链路）另见 F:\darkwood\coopmod-analysis.md（子代理产出，若生成）。
