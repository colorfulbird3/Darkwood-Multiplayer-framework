# Darkwood Multiplayer Framework 0.8.9-beta.1

**架构重构正式版**：十刀重构全部完成，功能与 0.8.8-beta.5 完全对齐（零行为变化、零 wire 改动）。

## 本版内容：0.8.9-architecture 十刀

| # | 重构项 | 说明 |
|---|---|---|
| 1 | Runtime 拆分 | `DarkwoodAdapterRuntime` 1798 行单文件 → 8 个 partial（主/Network/Messages/Entities/RuntimeEntities/Players/Rescue/SelfTest） |
| 2 | RuntimeContext | `SessionContext` 会话权威状态源（角色/状态/身份/场景/错误）；`IsHost/IsClient` 不再摸会话对象 |
| 3 | MessageRouter | 25 种消息 → 11 个领域 handler（注册制），两个巨型 if-else 链消灭；新消息=新 handler+一行注册 |
| 4 | Protocol 拆分 | `ReplicationProtocol.cs` 445 行 → 9 个领域文件（Save/Snapshot/Inventory/Player/Action/Combat/RuntimeEntity/Scene） |
| 5 | Transport 真接口 | 删除假 `DeliveryMode`；`Capabilities` 诚实声明；降级显式警告 |
| 6 | Channel 分级 | `TransportChannel`（Control/ReliableGameplay/Realtime/Bulk）+ 消息类型映射；为未来 UDP/KCP 预留，上层不用改 |
| 7 | Host/Client 分离 | `TickHost/TickClient` 独立周期逻辑，Update 只剩生命周期壳 |
| 8 | Runtime Entity 领域模型 | `RuntimeEntityLifecycleState`（Pending/Spawned/Despawned）+ LocalInstance + 注册表状态 API |
| 9 | Container Revision | 乐观锁 + 并发补偿（0.8.8-beta.3 收口） |
| 10 | xUnit | 新测试项目 `tests/DarkwoodMultiplayerFramework.UnitTests`（乐观锁/ID 纪律/生命周期/codec/SessionContext，8 项） |

## 验证

- 构建 0 警告 / 0 错误
- SelfTests **81/81**
- xUnit **8/8**
- 回环自测全链路通过（握手 → 存档 SHA-256 → 快照 → 档案 → READY，9 秒）

## 功能基线（与 0.8.8-beta.5 一致）

运行时实体全链（容器 35m 门控一次性 / 敌人代理 15Hz delta / 掉落物镜像）、FIX-013 默认出生点、场景切换自动重连、Container Revision 乐观锁+并发补偿、营救（4 米范围）、倒地攻击者逃离、Despawn 定向广播、僵尸连接清理。

## 使用指南

F6 面板 / F1 创建主机 / F2 加入 / F3 停止 / F4 营救 / F7-F8 回环自测。
安装：关闭游戏后双击 安装.bat。
