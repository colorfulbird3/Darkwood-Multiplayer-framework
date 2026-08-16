# Darkwood Multiplayer Framework 0.8.9-beta.3

**0.8.9 closeout 收尾版**：修复代码残留 + 故障注入测试基础设施。功能与 0.8.9-beta.2 对齐（零行为变化、零 wire 改动）。

## 本版内容

### 收口修复（0.8.9 closeout fix）

| # | 修复 | 说明 |
|---|---|---|
| 1 | `PlayerService.PersistGuestProfile` 旧字段名 | `PeerGuestRecords/RemoteInventories` → 私有字段小写名（确定的编译错误） |
| 2 | `TransferProgress` 赋值残留 7 处 | 只读属性后所有写路径收敛到 `SaveState.SetProgress(...)`（确定的编译错误） |
| 3 | `DrainPendingSnapshotRequests` 丢 peerId | 返回 `KeyValuePair<int, ReadyMessage>[]`，主机快照请求不再丢失玩家身份 |
| 4 | `CombatService` 构造器 | 参数改为 `IMultiplayerRuntimeHost`（internal），连构造依赖也锁死 |
| 5 | `FaultInjectingTransport` 重复 Disconnected | inner.Stop() 后 Telepathy 事件再触发 → 防重入守卫 + `Connect()` 重新武装 |

### 可靠性测试基础设施（0.9.0 前哨）

`FaultInjectingTransport`（Network 纯库，包装任意 ITransport）：
- `DropEveryN` 丢包 / `DelayMilliseconds` 延迟 / `DuplicateEveryN` 重复 / `DisconnectAfterMessages` 断线 / `CorruptNextPacket` 损坏
- **xUnit 9 项**：透传、丢包、重复、断线、损坏、延迟、事件转发、Disconnected 防重入、重连重新武装

### 测试规模

- SelfTests **81/81**（回环全链路）
- xUnit **24/24**（Core/Protocol/Entities/Network + FaultInjection）
- 回环自测：握手 → 存档 SHA-256 → 快照 → 档案 → READY（9 秒）

## 发布纪律

- 干净全量构建（删 obj/bin 后 `dotnet build`，杜绝增量缓存掩盖编译错误）
- GitHub Release 仅 3 资产（ZIP / README-install / RELEASE-NOTES）
- 版本串：Plugin `0.8.9.4` / Framework `0.8.9-beta.3`

## 功能基线（与 0.8.9-beta.2 一致）

运行时实体全链、默认出生点、场景切换自动重连、Container Revision 乐观锁、营救（4 米）、倒地攻击者逃离、Despawn 定向广播、僵尸连接清理、所有权拆分四服务 + EntityStateAdapter。

## 使用指南

F6 面板 / F1 创建主机 / F2 加入 / F3 停止 / F4 营救 / F7-F8 回环自测。
安装：关闭游戏后双击 安装.bat。
