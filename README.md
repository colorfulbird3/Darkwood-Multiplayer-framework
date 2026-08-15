# Darkwood Multiplayer Framework

Darkwood（Unity Mono 生存恐怖游戏）的多人联机插件框架。通过 BepInEx 5 + Harmony 注入游戏，使用自有二进制协议与 Telepathy TCP 在局域网 / Radmin VPN 中联机。

当前版本：**0.8.7-beta.1**（核心玩法真机验证通过）

## 特性

- **信任模式联机**（类普通合作游戏）：联机后玩家操作本地直接执行并同步——开关门、封窗、物品开关、打开容器、拿取/放入物品都不经过主机审批；容器状态上报主机后广播给其他玩家，双方物品保持一致。
- 世界快照 + 实体增量（15 Hz）同步怪物/门窗/物品状态；远端怪物 AI 冻结，远端玩家模型插值。
- 热加入：主机开局后随时接受新玩家；访客身份 + 主机侧档案持久化，断线重连不丢物品；按天数分档初始装备；超员拒绝。
- 倒地与营救：玩家阵亡时若还有队友存活则进入倒地状态（无法行动、视角原地），队友按 F4 营救（3 秒、头顶进度条、可取消）；全员倒地触发原版死亡结局。
- 客户端存档加载优化：主机打包时剥离 A* 导航图后传输（实测约 -64%），客户端跳过寻路图重建与连接，弱机加载显著加速；剥离带运行时保护（字段缺失/重复/结构异常时回退为完整存档传输）。
- 版本契约：无向下兼容；握手只比较框架版本与游戏版本，不一致直接拒绝加入。

## 下载与安装

发布安装包（ZIP）见 GitHub Releases。安装说明随安装包提供。

## 构建与自测

前置条件：

1. 本机安装 Darkwood（Steam 版）——构建需要引用游戏目录的 `Darkwood_Data/Managed/` 程序集（不随仓库分发）。
2. 把仓库 clone 到 **Darkwood 游戏安装目录内部**（例如 `<SteamLibrary>/steamapps/common/Darkwood/Darkwood Multiplayer framework`），这样 csproj 中的相对引用（`..\..\..\Darkwood_Data\Managed\`）才能解析。也可以放在任意位置并自行创建对应层级结构。
3. .NET SDK 7+。BepInEx / Harmony 核心 DLL 已随仓库 `libs/` 分发，无需另行准备。

```powershell
dotnet build '.\src\DarkwoodMultiplayerFramework.sln' -c Release -m:1 -p:MSBuildEnableWorkloadResolver=false
dotnet run --project '.\src\DarkwoodMultiplayerFramework.SelfTests\DarkwoodMultiplayerFramework.SelfTests.csproj' -c Release --no-build -p:MSBuildEnableWorkloadResolver=false
```

构建 0 警告 / 0 错误且 SelfTests 全 PASS 后再提交。

## 项目文档

- `ARCHITECTURE-ROADMAP.zh-CN.md`：架构路线图。
- `AGENTS.md`：开发工作规则。
- `docs/VERIFY-001.md`：核心玩法真机验证证据。
- `RELEASE-NOTES-0.8.7-beta.1.md`：当前版本发布说明。

## 许可证

本项目源码见 `LICENSE`；第三方组件声明见 `THIRD-PARTY-NOTICES.md`。
