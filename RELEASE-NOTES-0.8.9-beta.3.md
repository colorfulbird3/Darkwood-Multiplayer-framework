# Darkwood Multiplayer Framework 0.8.9-beta.3

0.8.9 收尾改动。功能与 0.8.9-beta.2 对齐；wire 格式未变。

## 改动

- `PlayerService.PersistGuestProfile` 里两个旧字段名没改干净，编译报错——已修正。
- `TransferProgress` 改为只读后，7 处旧赋值残留（`Network.cs`/`SelfTest.cs`/`State.cs`）——全部收敛到 `SaveState.SetProgress(...)`。
- `DrainPendingSnapshotRequests()` 之前只返回 `ReadyMessage[]`，把 peer id 丢了，主机按错误参数准备快照——改为返回 `KeyValuePair<int, ReadyMessage>[]`。
- `CombatService` 构造器参数从 `DarkwoodAdapterRuntime` 改为 `IMultiplayerRuntimeHost`。
- `FaultInjectingTransport` 断开时可能触发两次 `Disconnected`（自己触发 + Telepathy 后续事件）——加了防重入，`Connect()` 时重新武装；补了 2 个测试。

## 测试

- 单元测试 24 项通过（含 FaultInjectingTransport 9 项：丢包/延迟/重复/断线/损坏/防重入）
- SelfTests 81 项通过
- 回环自测全链路通过（握手 → 存档 → 快照 → READY）

## 已知问题

- 真机双机测试尚未完成。
- 部分运行时实体仍依赖宽容快照处理（见 `docs/problems/`）。
- 传输层目前只有 TCP。

## 使用

F6 面板 / F1 主机 / F2 加入 / F3 停止 / F4 营救。安装：解压后双击 `安装.bat`。
