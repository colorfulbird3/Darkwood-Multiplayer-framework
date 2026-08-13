# Darkwood Multiplayer Framework

Darkwood（Unity Mono 生存恐怖游戏）的多人联机插件框架。通过 BepInEx 5 + Harmony 注入游戏，使用自有二进制协议与 Telepathy TCP 在局域网 / Radmin VPN 中联机。

当前版本：**0.8.7-alpha.14**

## 特性

- 主机权威：Host 是世界、实体和共享容器的唯一权威；Client 只提交意图（ActionRequest）。
- 共享柜子物品事务：拿取、放置、拖放、堆叠、交换均由主机验证、原子修改并广播。
- 近战攻击权威闭环：客户端攻击交给主机校验结算；伤害只由主机从影子库存推导。
- 怪物死亡镜像：尸体与掉落物以主机权威内容为准。
- 开关门、封窗、物品开关等交互由主机校验后重放并广播。
- 世界快照 + 实体增量（15 Hz）同步怪物/门窗/物品状态；远端怪物 AI 冻结，远端玩家模型插值。
- 热加入：主机开局后随时接受新玩家；访客身份 + 主机侧档案持久化，断线重连不丢物品；按天数分档初始装备；超员拒绝（SESSION_FULL）。
- 倒地与营救：玩家阵亡时若还有队友存活则进入倒地状态（无法行动、视角原地），队友按 F4 营救（3 秒、头顶进度条、可取消）；复活恢复生命上限 10%、体力回满；全员倒地触发原版死亡结局。
- 版本契约：无向下兼容；握手只比较框架版本与游戏版本，不一致直接拒绝加入。

## 下载与安装

发布安装包（ZIP + SHA256 校验文件）见 GitHub Releases。安装说明随安装包提供。

## 构建与自测

构建需要本机提供合法的 Darkwood `Darkwood_Data/Managed` 游戏程序集（不随仓库分发），以及 BepInEx/Harmony 核心 DLL（本地 `Payload/`，不随仓库分发）。

```powershell
dotnet build '.\src\DarkwoodMultiplayerFramework.sln' -c Release --no-restore -m:1 -p:MSBuildEnableWorkloadResolver=false
dotnet run --project '.\src\DarkwoodMultiplayerFramework.SelfTests\DarkwoodMultiplayerFramework.SelfTests.csproj' -c Release --no-build -p:MSBuildEnableWorkloadResolver=false
```

## 项目文档

- `ARCHITECTURE-ROADMAP.zh-CN.md`：架构路线图。
- `AGENTS.md`：开发工作规则。
- `RELEASE-NOTES-0.8.7-alpha.14.md`：当前版本发布说明。

## 许可证

本项目源码见 `LICENSE`；第三方组件声明见 `THIRD-PARTY-NOTICES.md`。
