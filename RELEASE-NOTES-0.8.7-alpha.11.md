# Darkwood Multiplayer Framework 0.8.7-alpha.11

本版完成 **PROTO-001 版本契约统一**：框架正式确立**无向下兼容**策略，握手简化为单一版本门槛。

## 变更

- **握手契约简化**：`ProtocolIdentity` 从五字段（Protocol/Framework/Game/SaveSchema/SnapshotSchema）缩减为两字段（`FrameworkVersion` + `GameVersion`）。任何不一致（框架版本或游戏构建）直接拒绝加入，错误码 `INCOMPATIBLE_FRAMEWORK_VERSION` / `INCOMPATIBLE_GAME_BUILD`。
- **消除 Schema 漂移**：`SaveSchema`/`SnapshotSchema` 握手字段删除。内部 `DarkwoodSaveBundle`（wire 3）与 `WorldSnapshotWireCodec`（schema 2）的版本头现在是实现细节，随框架版本绑定，不再单独协商——PROTO-001 的漂移问题自此不存在。
- **单一版本常量**：`ProtocolVersions` 收敛为 `EnvelopeProtocol`（信封头常量 3）+ `Framework`（唯一版本门槛）。
- **不保留兼容路径**：不维护旧版本 fixture、不做旧版本翻译；SelfTests 的负例改为框架版本不一致与游戏构建不一致的拒绝测试（43/43 通过）。
- 其余功能基线不变（主机权威物品事务、近战/门窗/物品交互权威闭环、怪物死亡镜像，均为 alpha.10 引入，双端实机仍待验证）。

## 版本与兼容性

- 框架版本 `0.8.7-alpha.11`；Envelope 协议版本 3（常数）。
- **无向下兼容**：alpha.11 与 alpha.10 及所有更早版本互不兼容，双方必须使用同一版本安装包。

## 验证状态

- `dotnet build` 0 warning / 0 error；SelfTests 43/43 通过（退出码 0）。
- 双端实机未验证：**IMPLEMENTED_UNVERIFIED**。

版本：0.8.7-alpha.11。
