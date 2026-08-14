# 项目概览

## 身份

Darkwood Multiplayer Framework（DMF）是运行在 Darkwood Unity/Mono 游戏上的 BepInEx 插件框架。目标是让一个 Host 维护世界、实体和共享物品的权威状态，多个 Client 通过 TCP 收发协议消息并在本地还原世界。

当前开发基线：`0.8.7-alpha.9`。

## 原技术栈（运行时）

| 层 | 实际依赖/实现 |
|---|---|
| 插件加载 | BepInEx 5.4.23.5 |
| 运行时补丁 | Harmony/0Harmony 2.9.0.0 |
| 游戏适配 | Darkwood `Assembly-CSharp` + UnityEngine 反射/Harmony 适配 |
| 网络传输 | Telepathy TCP 1.0.341.0 |
| 编码 | DMF 自有 `BinaryWriter`/`BinaryReader` Protocol Envelope |
| 适配器目标 | .NET Framework 4.7.2 (`net472`) |
| 纯逻辑模块 | `netstandard2.0`/`netstandard2.1` |
| 自测 | `net7.0` SelfTests，含 Telepathy loopback |

UnityEngine 的文件/程序集版本不能用来推断 Unity Editor 版本；源码没有声明具体 Editor 版本。

## 模块职责

- `Core`：连接状态、快照阶段、EntityId、StateVersion。
- `Protocol`：Envelope、握手身份、存档/快照/实体/Action/库存 DTO 与 codec。
- `Network`：Telepathy 传输、会话、握手状态机、分块传输。
- `Entities`：持久/运行时实体注册表和 digest。
- `Snapshots`：WorldSnapshot wire、分块组装、哈希与应用阶段。
- `Actions`：RequestId 幂等缓存、revision/CAS 权威抽象。
- `Inventory`：共享容器状态和事务模型。
- `DarkwoodAdapter`：正式游戏入口、Harmony 补丁、存档、实体扫描、库存和运行时编排。
- `Rendering`：远端玩家模型和插值。
- `SelfTests`：协议、握手、分块、快照、Action 和 Telepathy loopback 测试。

## Hermes + DS V4 Pro 的职责

Hermes 是开发 Agent/任务编排层，DS V4 Pro 是 Agent 可调用的开发模型。它们不进入 Unity 进程，不替代 Telepathy、BepInEx、Harmony 或 DMF Protocol。模型生成的任何代码都必须经过编译、自测和双端实机验证。
