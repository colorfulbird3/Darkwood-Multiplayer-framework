# Darkwood Multiplayer Framework 0.8.9-beta.2

**所有权拆分正式版**：Runtime 业务全部移交领域服务，功能与 0.8.9-beta.1 完全对齐（零行为变化、零 wire 改动）。

## 本版内容：0.8.9 所有权拆分（8 步）

| # | 步骤 | 说明 |
|---|---|---|
| 1 | RuntimeEntityService | 运行时实体（容器/掉落物/敌人）的注册表+字典+镜像全归服务；外部只调公开方法 |
| 2 | CombatService | 血量/倒地/怪物伤害/攻击锚点/无敌时间/营救会话全归服务；断线清理 `OnPeerDisconnected` |
| 3 | PlayerService | 远端坐标/背包影子/Guest 档案解析与持久化归服务 |
| 4 | SaveState 服务 | Save/Snapshot 传输 19 个状态字段分家 + `Reset`/断线收口；`TransferProgress` 属性转发兼容面板 |
| 5 | EntityStateAdapter | Character/Door/Window/Item/Inventory 状态转换独立成适配器；EntityReplication 只管"何时/哪些/Revision/插值" |
| 6 | Runtime 收行 | 配置 → `Config.cs`、状态机 → `State.cs`；主壳 451→363 行（业务壳 ~100 行） |
| 7 | Protocol 目录化 | 11 个子目录（Common/Handshake/Save/Snapshot/Player/Action/Combat/RuntimeEntity/Scene/Inventory） |
| 8 | xUnit 渐进 | +7 项（分片装配/篡改、连接状态机、codec、通道能力）→ **15 项** |

## 最终结构

```text
DarkwoodAdapterRuntime（Unity 壳 + 少量字段）
   ├── RuntimeEntities/DarkwoodRuntimeEntityService
   ├── Combat/DarkwoodCombatService（含 Rescue）
   ├── Players/DarkwoodPlayerService（含 GuestProfile）
   ├── Save/DarkwoodSaveTransferService
   └── World/DarkwoodEntityStateAdapter
Protocol 11 子目录 · tests/UnitTests 15 项
```

每个服务：**自己的字段自己管，自己的 Reset 自己做**。未来加新玩法 = 加服务，不再动 God Object。

## 验证

- 构建 0 警告 / 0 错误
- SelfTests **81/81**
- xUnit **15/15**
- 回环自测全链路通过（握手 → 存档 SHA-256 → 快照 → 档案 → READY，9 秒）

## 功能基线（与 0.8.9-beta.1 一致）

运行时实体全链（容器 35m 门控一次性 / 敌人代理 15Hz delta / 掉落物镜像）、默认出生点、场景切换自动重连、Container Revision 乐观锁+并发补偿、营救（4 米）、倒地攻击者逃离、Despawn 定向广播、僵尸连接清理。

## 使用指南

F6 面板 / F1 创建主机 / F2 加入 / F3 停止 / F4 营救 / F7-F8 回环自测。
安装：关闭游戏后双击 安装.bat。
