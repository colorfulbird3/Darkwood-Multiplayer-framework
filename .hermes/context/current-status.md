# 当前状态与风险

## 基线

- 发布基线：`0.8.7-alpha.10`（2026-08-13）。游戏目录 BepInEx/plugins、Payload、安装包与 ZIP 均已更新。
- alpha.10 ZIP SHA256：`4536DF53A6A47EF840F4E4AC4CB65CFC818A932AC28E31FF5EBFFBA966CA1624`（manifest 32/32 校验通过；游戏目录与安装包插件 DLL 哈希一致）。
- GitHub：canonical 源码以普通提交推送至 https://github.com/colorfulbird3/Darkwood-Multiplayer-framework（main），tag `v0.8.7-alpha.10`；Payload/第三方二进制/安装包不进仓库。

## VERIFIED

- 2026-08-13：solution 构建为 0 warning / 0 error。
- 2026-08-13：SelfTests 43/43 通过（含 SYNC-001 新增 8 项），退出码 0；fixture 使用当前身份常量（Protocol 3 / Framework 0.8.7-alpha.10 / Save 1 / Snapshot 3），不再依赖旧 0.8.6-alpha.1 fixture。
- 安装包 RELEASE-MANIFEST.sha256 32/32 校验通过（UTF-8 读取，大小写归一后比对）。
- 游戏目录 BepInEx/plugins 与安装包 Payload 的 7 个框架 DLL SHA256 完全一致。

## IMPLEMENTED_UNVERIFIED（alpha.10）

- 近战攻击权威闭环：客户端拦截 `Player.attack(float)` → Host 影子武器推导伤害 → MeleeSensor 近似弧命中 → 原生 `getHit` 结算 → 影子耐久扣减 + 完整玩家库存回传。
- 怪物死亡镜像：客户端 alive 1→0 时 `die2()` 本地生成尸体（无 onDeath 剧情触发），deathDrop 容器内容以 Host 权威广播为准。
- 开关门/封窗/物品开关 Action 化（带 ExpectedRevision CAS + 拒绝回传最新实体状态）。
- 容器拒绝路径回传权威容器状态（自校正残余分叉）。
- 以上均未做真实双端实机矩阵验证。

## BUG_OPEN / 架构缺口

1. Host 的远端玩家库存 shadow 初始状态可能错误地来自本机 `Player.Instance`（INV-001 仍未复验）。
2. 握手 Save Schema/Snapshot Schema 与内部 SaveBundle（wire 3）/WorldSnapshotWire（schema 2）的漂移仍未统一（PROTO-001；`ProtocolVersions` 常量已建立但三种 wire 版本未合并）。
3. 火器/投掷物不同步；陷阱、发电机专属逻辑、剧情/任务事件无 Action 链路。
4. 运行时生成实体（夜间怪物/掉落物）的 Spawn/Destroy/Reconnect 同步缺失（P2 RuntimeEntityId）。
5. 远端玩家攻击不计算技能加成（Strong/weak），近战弧为 MeleeSensor 近似值，封窗/物品开关拦截依赖 `selectedObject` 启发式——均需双端实机调参确认。
6. 公开仓库和 Release 的第三方二进制/许可证声明需要核对。
7. 部分 csproj 的 `HintPath` 依赖本机 `Darkwood_Data/Managed` 和 `Payload`；干净克隆环境需要用户提供合法游戏引用。

## 任务顺序

`PROTO-001 → INV-001 → SYNC-001 双端实机 → reviewer → 下一迭代`

不要从 Release Notes 推导 VERIFIED。状态只能由命令输出、哈希、日志或真实用户复现更新。
