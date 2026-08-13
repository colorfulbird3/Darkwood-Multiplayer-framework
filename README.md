# Darkwood Multiplayer Framework

Darkwood（Unity Mono 生存恐怖游戏）的多人联机插件框架。通过 BepInEx 5 + Harmony 注入游戏，使用自有二进制协议与 Telepathy TCP 在局域网 / Radmin VPN 中联机。

当前版本：**0.8.7-alpha.10**（协议版本 3）

## 架构

- Host 是世界、实体和共享容器的唯一权威；Client 只提交意图（ActionRequest）。
- 主机权威：共享柜子物品事务（拿/放/拖/叠/换）、近战攻击、开关门/封窗/物品开关、怪物死亡与尸体掉落。
- 世界快照 + 实体增量（15 Hz）同步怪物/门窗/物品状态；远端怪物 AI 冻结，远端玩家模型插值。

详见 `IDEA.md`（项目事实单一入口）、`AGENTS.md`（Agent 工作规则）和 `.hermes/`（上下文与任务）。

## 构建与自测

构建需要本机提供合法的 Darkwood `Darkwood_Data/Managed` 游戏程序集（不随仓库分发），以及 BepInEx/Harmony 核心 DLL（本地 `Payload/`，不随仓库分发）。

```powershell
dotnet build '.\src\DarkwoodMultiplayerFramework.sln' -c Release --no-restore -m:1 -p:MSBuildEnableWorkloadResolver=false
dotnet run --project '.\src\DarkwoodMultiplayerFramework.SelfTests\DarkwoodMultiplayerFramework.SelfTests.csproj' -c Release --no-build -p:MSBuildEnableWorkloadResolver=false
```

## 安装包

安装包与 ZIP 只在本机发布流程中生成，不进仓库。见 `RELEASE-NOTES-*.md`。

## 许可证

本项目源码见 `LICENSE`；第三方组件声明见 `THIRD-PARTY-NOTICES.md`。
