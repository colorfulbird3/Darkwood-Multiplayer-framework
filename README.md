# Darkwood Multiplayer Framework

Darkwood（Unity Mono 生存恐怖游戏）的多人联机插件框架。通过 BepInEx 5 + Harmony 注入游戏，使用自有二进制协议与 Telepathy TCP 在局域网 / Radmin VPN 中联机。

当前版本：**0.8.9-beta.3**（0.8.9 收口：IMultiplayerRuntimeHost 窄依赖 + 玩家状态封装 + 存档服务行为化 + 故障注入测试基础设施）

## 特性

- **信任模式联机**（类普通合作游戏）：联机后玩家操作本地直接执行并同步——开关门、封窗、物品开关、打开容器、拿取/放入物品都不经过主机审批；容器状态上报主机后广播给其他玩家，双方物品保持一致。
- 世界快照 + 实体增量（15 Hz）同步怪物/门窗/物品状态；远端怪物 AI 冻结，远端玩家模型插值。
- **Runtime Entity 运行时实体**：不写入存档的对象（乌鸦群、动物尸体、夜间事件怪）纳入网络世界——客户端进入 35 米范围才触发生成（一次性动画不重播），运行时敌人由 15 Hz 增量驱动位置/血量/攻击/死亡动画。
- 客户端在**游戏默认出生点**出生（与单机新游戏一致），不再在主机位置。
- **场景切换自动重连**：主机切换章节时客户端 3 秒后自动重连并加载新场景存档，无需手动操作。
- 热加入：主机开局后随时接受新玩家；访客身份 + 主机侧档案持久化，断线重连不丢物品；按天数分档初始装备；超员拒绝。
- 倒地与营救：玩家阵亡时若还有队友存活则进入倒地状态（无法行动、视角原地），队友按 F4 营救（3 秒、头顶进度条、可取消）；全员倒地触发原版死亡结局。
- 客户端存档加载优化：主机打包时剥离 A* 导航图后传输（实测约 -64%），客户端跳过寻路图重建与连接，弱机加载显著加速；剥离带运行时保护（字段缺失/重复/结构异常时回退为完整存档传输）。
- 本地回环自测：配置 `SelfTestAuto=true` 启动即自动验证完整联机协议链（详见下）。
- 版本契约：无向下兼容；握手只比较框架版本与游戏版本，不一致直接拒绝加入。

## 使用指南

### 快捷键

| 按键 | 功能 |
|---|---|
| **F6** | 开关联机面板（配置主机地址 / 端口 / 访客身份） |
| **F1** | 创建主机（监听 TCP 17777） |
| **F2** | 以面板配置的地址连接主机（加入） |
| **F3** | 停止联机会话 |
| **F4** | 营救倒地队友（需在倒地队友附近） |
| **F7** / **F8** | 启动 / 停止回环自测客户端（连接本机主机验证全链路，见下） |

### 快速开始（主机 / 客户端）

1. **主机**：启动游戏 → 读档进入世界 → 按 **F6** 打开面板确认端口与身份 → 按 **F1** 创建主机。日志出现"主机正在监听 TCP 端口 17777"即可等待加入。
2. **客户端**：启动游戏 → 按 **F6** 打开面板 → 填入主机地址（局域网 IP 或 Radmin VPN IP）→ 保存 → 按 **F2** 加入。首次加入会从主机下载存档并加载（加载期间请勿操作），进入世界后在**游戏默认出生点**出生。
3. 主机与客户端必须使用相同版本；双方 Steam 均为在线状态（联机本身不依赖 Steam，但存档含 Steam 成就数据）。

### 本地回环自测（可选，验证安装完整性）

1. 用文本编辑器打开 `BepInEx/config/com.darkwood.multiplayer.framework.rebuilt.adapter.cfg`，把 `Gameplay` 段的 `SelfTestAuto` 改为 `true` 并保存。
2. 启动游戏，**不要做任何操作**——自动开主机 → 自动读档 → 回环客户端走完 握手/存档/快照/READY 全链路（约 10 秒）。
3. 日志（`BepInEx/LogOutput.log`）出现 `✓✓ 回环自测全链路通过` 即安装正确。测完把 `SelfTestAuto` 改回 `false`。

## 下载与安装

发布安装包（ZIP）见 GitHub Releases。安装说明随安装包提供（解压后双击 `安装.bat`）。

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
- `docs/VERIFY-001.md`：核心玩法真机验证证据（0.8.7）。
- `docs/VERIFY-002.md`：0.8.8/0.8.9 回环自测全链路验证证据（含 closeout fix）。
- `RELEASE-NOTES-0.8.9-beta.3.md`：当前版本发布说明。

## 许可证

本项目源码见 `LICENSE`；第三方组件声明见 `THIRD-PARTY-NOTICES.md`。
