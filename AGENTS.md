# Darkwood Multiplayer Framework — Agent 工作规则

本文件是本地源码树的通用开发入口。如果本文件与其他开发上下文文件冲突，以用户当前请求和更严格的安全限制为准。

## 项目边界

- 本目录是后续开发的 canonical 本地源码树：`src/`。
- 游戏运行时仍使用 BepInEx 5 + Harmony + Unity Mono + Telepathy TCP。
- 开发工具链（开发 Agent 与开发模型）不是游戏运行时依赖；除非用户提供真实 SDK、协议文档和许可证，不得把它们写成 Unity 运行时依赖。
- 当前基线是 `0.8.7-alpha.16`；无向下兼容，握手只比较框架版本与游戏版本。

## 不变量

1. Host 是世界、实体和共享物品的唯一权威。
2. Client 只发送 intent/action；不能直接提交共享容器或 Host 世界的最终状态。
3. 每个 Action 必须带 `SessionId + PeerId + RequestId + TargetEntityId + ExpectedRevision`，Host 必须幂等处理。
4. Host 的容器和玩家库存 shadow 必须在一个事务中更新；拒绝、超时、断线都要能回传完整状态并回滚客户端预测。
5. Persistent EntityId 不能依赖 `GetInstanceID()`、名称或 Instantiate 顺序。
6. 快照/注册表/共享容器未完整校验时，Client 不得进入 READY。
7. 无向下兼容：框架版本是唯一 wire 门槛（PROTO-001 已定）；改动 wire schema 只递增框架版本并补 roundtrip、负例和版本不一致拒绝测试，不保留兼容路径。

## 修改前检查

- 先读取相关源码、现有日志和程序集行为，不凭空猜 Darkwood 方法签名。
- 先确认修改目标是 canonical 源码树，而不是旧的 GitHub 工作副本或安装包解压目录。
- 不读取、提交或输出 API key、个人存档、玩家隐私和第三方受限二进制。

## 验证命令

在 `Darkwood Multiplayer framework` 目录执行：

```powershell
dotnet build '.\\src\\DarkwoodMultiplayerFramework.sln' -c Release --no-restore -m:1 -p:MSBuildEnableWorkloadResolver=false
dotnet run --project '.\\src\\DarkwoodMultiplayerFramework.SelfTests\\DarkwoodMultiplayerFramework.SelfTests.csproj' -c Release --no-build -p:MSBuildEnableWorkloadResolver=false
```

运行时 DLL、正式游戏目录、安装包或 ZIP 只有在当前任务明确要求时才覆盖；覆盖前确认 Darkwood 已退出并保留回滚备份。

## 当前优先级

- P0：真实双端物品主机权威事务（拿取、放置、拖放、堆叠、交换、并发最后一件）。
- P1：攻击请求到 Host 校验、伤害、死亡和掉落的闭环。
- P1：门窗、陷阱、发电机和剧情事件 Action 化。
- P2：RuntimeEntityId 的 Spawn/Destroy/Reconnect 闭环。

## 诚实报告

报告必须列出修改文件、协议版本、自动测试结果、实机测试结果、DLL/ZIP 是否更新以及未验证项目。不能把“状态镜像”描述成“完整权威同步”，也不能声称已经接入不存在的第三方运行时 SDK。
