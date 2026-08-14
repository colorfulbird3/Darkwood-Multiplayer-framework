# 当前状态与风险

## 基线

- 发布基线：`0.8.7-alpha.11`（2026-08-13）。游戏目录 BepInEx/plugins、Payload、安装包与 ZIP 均已更新。
- alpha.11 ZIP SHA256：`369CD0DEEA56159ACC0C4B843231DD55D332401AEA1E31318529414E6E966851`（manifest 32/32 校验通过；游戏目录与安装包插件 DLL 哈希一致）。
- 版本契约：**无向下兼容**（PROTO-001 已定）。握手只比较 FrameworkVersion + GameVersion；内部 SaveBundle/WorldSnapshot wire 版本是实现细节。任何旧版本连接被握手拒绝。
- GitHub：canonical 源码以普通提交推送至 https://github.com/colorfulbird3/Darkwood-Multiplayer-framework（main），tag `v0.8.7-alpha.11`；Payload/第三方二进制/安装包不进仓库。

## VERIFIED

- 2026-08-13：solution 构建为 0 warning / 0 error。
- 2026-08-13：SelfTests 43/43 通过（退出码 0）；fixture 使用 `ProtocolVersions` 常量（Framework 0.8.7-alpha.11）；含框架版本/游戏构建不一致拒绝负例。
- 安装包 RELEASE-MANIFEST.sha256 32/32 校验通过；游戏目录 BepInEx/plugins 与安装包 Payload 的 7 个框架 DLL SHA256 完全一致。

## IMPLEMENTED_UNVERIFIED

- PROTO-001 版本契约统一（握手单一门槛）——代码与自动测试完成，未做双端实机（含"旧版本被拒绝"实机验证）。
- SYNC-001 近战攻击权威闭环、怪物死亡镜像、门窗/物品交互 Action 化、容器拒绝自校正——未做双端实机。
- 以上状态只能是 implemented_unverified，直到真实双端矩阵完成。

## BUG_OPEN / 架构缺口

1. Host 的远端玩家库存 shadow 初始状态可能错误地来自本机 `Player.Instance`（INV-001 仍未复验）。
2. 火器/投掷物不同步；陷阱、发电机专属逻辑、剧情/任务事件无 Action 链路。
3. 运行时生成实体（夜间怪物/掉落物）的 Spawn/Destroy/Reconnect 同步缺失（P2 RuntimeEntityId）。
4. 远端玩家攻击不计算技能加成（Strong/weak），近战弧为 MeleeSensor 近似值，封窗/物品开关拦截依赖 `selectedObject` 启发式——均需双端实机调参确认。
5. 公开仓库和 Release 的第三方二进制/许可证声明需要核对。
6. 部分 csproj 的 `HintPath` 依赖本机 `Darkwood_Data/Managed` 和 `Payload`；干净克隆环境需要用户提供合法游戏引用。

## 任务顺序

`INV-001 → SYNC-001/PROTO-001 双端实机 → reviewer → 下一迭代`

不要从 Release Notes 推导 VERIFIED。状态只能由命令输出、哈希、日志或真实用户复现更新。
