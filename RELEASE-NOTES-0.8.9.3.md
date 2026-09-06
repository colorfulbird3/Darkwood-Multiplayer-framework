# Darkwood Multiplayer Framework 0.8.9.3

> 0.8.9.3 = Host World Authority + Trusted Client 架构的功能收敛版：世界对象交互（门/灯/发电机/夹子/掉落物）双向同步真机闭环，
> 新增 **Host 权威事件同步层**（剧情世界事件 / flag / 时钟 / 天气 / 表现事件：气泡、地点发现、外部场景、地图标记、爆炸复演）。
> **wire 已变更：不得与 0.8.9.2 及更早版本混用；两端必须使用本版安装包。**

## 状态
- 真机已验证：掉落物双向（扔/捡/本体销毁）、门/灯/发电机双向、夹子触发动画双向 + 被夹出血同步、玻璃瓶等无原型物品掉落镜像、主机本地开关发电机/灯即时镜像、客户端捡走主机掉落物主机本体清除。
- 事件同步层为新增基础设施，待真机验收（代码 + 回环 PASS + SelfTests/xunit 65 通过）。
## 本版主要变更
### 交互与掉落物（真机闭环）
- 夹子触发双向同步：真实触发点 `Trigger.OnAfterTrigger` 捕获（host 即时广播 / client 上报 host → host 补合拢 → 权威广播）；夹子合拢视觉两端一致。
- 夹子被夹出血 FX：权威合拢到达时在客户端被夹处复演 vanilla 血渍（贴地），远端可见。
- 玻璃瓶等掉落镜像修复：物品世界原型若为投掷形态（ThrownItem/无 Inventory）→ 回退 vanilla 丢弃同款通用 `Items/DroppedItem` 并按权威槽 addSlot 填充。
- 客户端捡走主机运行时掉落物 → 主机本体销毁 + Despawn（不再残留幽灵实体）。
- 主机本地拾取即时 Despawn（不等 5 秒扫描）；掉落收养排除本地 pending（防误收养双份）。
- 主机本地开关发电机/灯 → 与客户端发起对称的即时 Action 广播（客户端电源/灯即时镜像）。
### Host 权威事件同步层（新增，P1-P3）
- 通道：`PresentationEvent` 通用表现事件（kind 扩展不改 wire）；世界事件 `Events.fireWorldEvent`、剧情 flag（bool/int，只传生效终值）、时钟（日推进/恢复走动沿 + 5s 心跳）、雨（5s 心跳）。
- 复演纪律：client 收到后在防回声 guard 内调用同一 vanilla 表现函数/写同字段；游戏性副作用归零（爆炸伤害/力由 host 结算，client 只复现视觉与状态）。
- 已接入表现事件：地点发现（日记/地图）、外部场景进出（排除梦境误拽）、地图标记揭示、非玩家气泡文字、爆炸桶引爆复演。
- 协议 wire 扩展 0.8.9.8-pre.1（内部版本号；对外发布号 0.8.9.3）。
## 安装
- 解压到游戏目录（Doorstop + BepInEx 已在包内，双击 `安装.bat` 或运行 `install.ps1` 自动备份+部署）；双开联机请两台机器都装本版。
## 不再允许 / 已知边界
- 旧版（≤0.8.9.2）与新版混用。
- 事件同步层中：旁白类无目标气泡、NPC 对话状态表（Journal/Dialogue）、章节/梦境/暂停权威化、敌人受击动画/声音的持续通道仍未接入（下一版）。
## 详细变更
- 详见 docs/problems/trap-visual-desync.md、world-object-state-bundle.zh-CN.md、dmf-action-sync-design.zh-CN.md 与 docs_coop_sync_study.zh-CN.md（coop 同步机制研究与 DMF 实施蓝图）。