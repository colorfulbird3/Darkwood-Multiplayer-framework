# Darkwood Multiplayer Framework 源码结构

0.8.x 可维护源码工程（BepInEx 5 + Harmony，netstandard2.0 / net472）。

## 模块

- `Core`：版本、连接状态和通用结果类型（含存档图剥离、快照容忍等纯逻辑，SelfTests 可测）
- `Network`：传输和协议边界
- `Entities`：持久实体/运行时实体身份（EntityId、Fnv1a 签名）
- `Snapshots`：世界快照生命周期
- `Actions`：玩家操作请求与结果（信任模式下仅剩拾取与近战等少数链路）
- `Inventory`：容器状态与同步
- `Rendering`：远端玩家视觉替身边界
- `DarkwoodAdapter`：游戏侧适配层（Harmony 补丁、存档传输、容器/交互同步、倒地营救）
- `SelfTests`：无游戏依赖的单元自测（`dotnet run` 运行，全 PASS 为提交前提）

## 当前同步链路

Protocol Envelope → 握手（框架/游戏版本契约）→ 实时存档分块收发 → 客户端隔离目录安装/加载 → Entity Registry 校验 → 独立 WorldSnapshot（实体与容器）→ 双向应用确认 → READY → 15 Hz 实体/玩家同步与容器状态上报转发。玩家操作在**信任模式**下本地直接执行，状态上报主机并广播给其他玩家。

详见仓库根 `README.md` 与 `ARCHITECTURE-ROADMAP.zh-CN.md`。
