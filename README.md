# Darkwood Multiplayer Framework

这是正在开发中的 Darkwood 主机权威联机框架与游戏适配层。`0.8.0-alpha.5` 已实现独立 Telepathy Host/Client、Protocol Envelope/Handshake、实时存档分块收发、隔离存档加载、Entity Registry 校验、独立 WorldSnapshot、实体/容器增量同步，以及主机和客户端玩家的双向姿态广播。

当前仍是需要双端游戏验证的 alpha，不应视为完整可玩版本。场景切换权威流程和客户端容器 Action Request/Host 原子事务仍在开发。

开源中的网络同步架构与 Darkwood 适配层实验工程。

## 构建

Darkwood 的游戏程序集、BepInEx、Mirror、Telepathy 和 Harmony 不随仓库分发。请先安装 Darkwood 与 BepInEx，并设置环境变量：

```powershell
$env:DMF_DARKWOOD_DIR = 'C:\path\to\Darkwood'
$env:DMF_DEPENDENCY_DIR = 'C:\path\to\Darkwood\BepInEx'
dotnet build .\src\DarkwoodMultiplayerFramework.sln -c Release
```

仅公开仓库中自己的源码；第三方组件请遵循各自许可证。

## 目录

- `src/`：公开源码。
- `ARCHITECTURE-ROADMAP.zh-CN.md`：架构路线图。
- `THIRD-PARTY-NOTICES.md`：第三方依赖说明。

## 许可证

见 `LICENSE`。Darkwood 游戏本体及其程序集不属于本项目。
