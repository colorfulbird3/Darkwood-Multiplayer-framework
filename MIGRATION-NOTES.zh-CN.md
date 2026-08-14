# Darkwood Multiplayer Framework：Hermes + DS V4 Pro 迁移方案

> 文档版本：0.2（基于本地 `0.8.7-alpha.9` 源码与只读风险审计）  
> 目标：把 Hermes Agent + DeepSeek V4 Pro 用作后续开发助手与模型，不把它们误当成游戏运行时网络库。  
> 当前游戏运行时仍是 BepInEx + Harmony + Unity/Mono + DMF 自有 Protocol + Telepathy。

## 0. 先明确迁移对象

从当前本地仓库和公开资料看，Hermes 是开发 Agent，DS V4 Pro 是可供 Agent 调用的模型；两者不是替代 Telepathy 的 Unity 网络 SDK，也没有可直接引用的 `Hermes.dll` 或 `DSV4Pro.dll`。因此本项目的“迁移”分为两层：

1. **开发工作流迁移（现在可以做）**：为 Hermes 配置项目上下文、模型、终端权限、测试规则和任务分层，让 Hermes + DS V4 Pro 能可靠维护本项目。
2. **游戏网络运行时迁移（暂不直接做）**：只有在提供真正的 Hermes/DS V4 Pro 游戏网络 SDK、协议文档和 Unity 适配器后，才评估替换 Telepathy 或 Protocol。没有这些资料时，直接改运行时会产生不可编译的假 API。

Hermes 官方配置位置是 `~/.hermes/`，包括 `config.yaml`、`.env`、`SOUL.md`、`memories/` 和 `skills/`；模型主槽和辅助槽在 `config.yaml` 中配置。API 密钥只放 `.env`，不要写入仓库。

## 1. 项目概览

Darkwood Multiplayer Framework 是一个运行在 Darkwood（Unity/Mono）上的 BepInEx 插件框架。它将主机作为世界状态权威源，客户端在完成握手、存档安装、实体注册和世界快照应用后进入 READY，再接收实体增量、玩家姿态和 Action 结果。

当前发布基线：`0.8.7-alpha.9`。

已实现的核心能力：

- F1 创建主机、F2 加入、F3 停止、F6 网络面板。
- Protocol Envelope、版本/游戏构建/存档 Schema/快照 Schema 握手校验。
- Telepathy TCP 传输，适配局域网和 Radmin VPN。
- 分块存档传输和独立客户端存档目录。
- Persistent EntityId、实体注册表和 digest。
- WorldSnapshot、EntityDelta、PlayerPose。
- 主机权威拾取，以及共享柜子的取出、放入、拖放、堆叠和交换事务。
- 客户端远端角色渲染、怪物 AI 冻结和实体状态镜像。
- Release 安装包、Payload、ZIP、SHA256 manifest 和 SelfTests。

## 2. 原技术栈与边界

| 层 | 当前实现 |
|---|---|
| 游戏加载 | BepInEx 5 |
| 运行时补丁 | Harmony/0Harmony |
| 游戏适配 | `Assembly-CSharp.dll`、UnityEngine、Darkwood 反编译行为 |
| 网络传输 | Telepathy TCP（运行时反射加载） |
| 网络协议 | 自有 BinaryWriter/BinaryReader 编码 |
| 目标运行时 | .NET Framework 4.7.2 / Unity Mono |
| 纯逻辑模块 | netstandard 2.0/2.1 |
| 测试 | `DarkwoodMultiplayerFramework.SelfTests`，含 Telepathy loopback |
| 发布 | 本地 Release DLL → 游戏、源码 Payload、安装包、ZIP |

`Mirror.dll`、旧 0.7 代码和反编译参考目录仅用于历史行为比对或编译参考；alpha.9 的运行时路径不依赖 Mirror API。

## 3. 核心逻辑梳理

### 3.1 连接生命周期

```text
Disconnected
  → Connecting
  → VersionChecking
  → SaveTransfer
  → LoadingSave
  → BuildingRegistry
  → ApplyingSnapshot
  → Ready
```

客户端 READY 之前不能发送玩家姿态、Action 或应用实时增量。主机收到客户端 `Ready` 后生成 WorldSnapshot；客户端只有在所有共享容器快照成功绑定后才发送 `WorldSnapshotApplied`。

### 3.2 主机权威 Action

```text
Client ActionRequest
  → Host 校验 peer、距离、EntityId、Revision、容量和槽位
  → Host Apply
  → Revision/StateVersion 增加
  → ActionResult 或 ActionRejected
  → InventoryState / EntityDelta 广播
```

物品事务特别要求：客户端不能先修改共享柜子。alpha.9 patch 了快捷转移、鼠标抓取、手柄抓取、放置、堆叠和交换入口；主机成功后回传完整玩家库存快照和容器状态。

### 3.3 实体身份

- 持久对象使用 `SaveableObject.uniqueId`、场景、组件类型、带同名兄弟序号的相对路径和组件序号生成稳定 hash。
- 动态实体未来应使用 Host 分配的 RuntimeEntityId；禁止 `GetInstanceID()`、名字或 Instantiate 顺序作为网络身份。
- 客户端收到容器 ID 不一致时，按名称、位置和库存类型重新绑定；仍无法绑定则阻止 READY。

### 3.4 状态复制

- Host 约 15 Hz 捕获 Character、Door、Window、Item 状态。
- Character：位置、旋转、生命、存活、活动、攻击/行走/奔跑、动画片段和帧。
- Door/Window：开启、路障、阻挡、摧毁、生命和路障字段。
- Item：摧毁、生命、数量、开关、搜索和活动状态。
- 客户端 Character 的 AIpath 被禁用，远端位置使用插值。

## 4. Hermes + DS V4 Pro 的开发工作流

### 4.1 Hermes 配置建议

建议使用独立 Hermes profile，避免与其他项目混用记忆和技能：

```powershell
$env:HERMES_HOME = 'C:\Users\bird\.hermes-darkwood'
hermes config set terminal.backend local
hermes config set terminal.cwd 'F:\SteamLibrary\steamapps\common\Darkwood\Darkwood Multiplayer framework'
hermes config set model.provider openrouter
hermes config set model.default 'deepseek/deepseek-v4-pro'
```

实际模型 slug 以你的 Provider/账户显示为准；不要把 API key 写进命令历史或仓库。推荐在 `config.yaml` 中把辅助任务分开：

- 主模型：DS V4 Pro，用于架构判断、协议设计和复杂调试。
- Compression/Web Extract/标题：成本更低的快速模型。
- Approval：便宜模型或手动审批，尤其是文件覆盖、启动游戏和打包操作。

### 4.2 Hermes 工作规则

把本目录的 `.hermes.md` 作为项目级上下文（如果你安装的 Hermes 版本使用其他项目上下文文件名，则把同样内容放入该版本支持的项目 context 入口）。它定义：

- 真实工作目录和源码目录。
- 允许修改的范围。
- 编译、自测和发布命令。
- 不得把 Hermes/DS V4 Pro 当成游戏运行时依赖。
- 物品同步优先级和主机权威不变量。
- 每次修改必须报告文件、测试和 DLL/ZIP 校验。

### 4.3 任务拆分方式

使用短、可验证的任务，而不是让 Agent 一次重写整个网络层：

1. `audit`：只读定位目标程序集方法、调用链和现有日志。
2. `protocol`：只改 Protocol DTO/codec，并添加 roundtrip/negative test。
3. `host-authority`：只改 Host handler、revision、idempotency 和广播。
4. `client-apply`：只改客户端 patch、快照应用和回滚。
5. `adapter`：只改 Darkwood 反射/Harmony 适配。
6. `package`：编译、覆盖、manifest、ZIP；不得夹带未测试源码改动。

每个任务结束必须满足：`dotnet build` 通过、SelfTests 通过、无协议版本未更新、无旧 DLL 混入。

## 5. Hermes/DS V4 Pro 代码实现建议

### 5.1 建立“模型不可直接改状态”的规则

Agent 的职责是提出和实现代码变更，不是替代 Host 权威逻辑。所有游戏状态修改必须落在 Host handler 或明确的本地单机分支：

```csharp
if (runtime.IsClient && runtime.State == ConnectionState.Ready)
    return SendActionRequest(intent);

if (runtime.IsHost || !runtime.IsNetworkConnected())
    return ApplyLocalOrAuthoritative(intent);
```

### 5.2 抽象运行时传输，而不是把 Hermes 塞进 Unity

未来如果确实有 DS V4 Pro 网络 SDK，应新增适配层，而不是让协议模块直接依赖 SDK：

```csharp
public interface IAuthoritativeTransport
{
    void StartHost(TransportOptions options);
    void ConnectClient(TransportOptions options);
    void Send(int peerId, ReadOnlyMemory<byte> envelope);
    event Action<int, ReadOnlyMemory<byte>> Message;
    event Action<int> Disconnected;
}
```

现有 `TelepathyServerTransport`/client transport 实现该接口；未来 `HermesDsV4Transport` 只负责把 Envelope 映射到新 SDK。`ProtocolEnvelope`、Handshake、Action DTO、快照 codec 不应知道传输供应商。

### 5.3 DS V4 Pro 若指模型而非服务器

DS V4 Pro 只参与：

- 生成协议/适配代码草案。
- 分析日志和反编译调用链。
- 生成测试用例、迁移 checklist 和 release notes。

它不参与运行时的每帧同步、玩家输入裁决或存档写入。所有模型生成代码必须经过编译、自测和双端游戏测试。

## 6. 代码迁移清单

### 阶段 A：开发上下文迁移（立即可做）

- [x] 建立 `.hermes.md` 项目规则。
- [ ] 在 Hermes profile 中设置主模型和辅助模型。
- [ ] 建立 `memories/`：协议不变量、测试命令、发布路径、已知问题。
- [ ] 建立专用 skill：`darkwood-protocol-review`、`darkwood-release-check`。
- [ ] 禁止自动上传 GitHub、启动游戏或覆盖 DLL，除非当前任务明确要求。

### 阶段 B：运行时解耦（建议 0.9.x）

- [ ] 把 `DarkwoodAdapterRuntime` 对传输的依赖收敛到 `ITransport`。
- [ ] 增加 `IAuthoritativeTransport` contract test。
- [ ] 将 Protocol codec 与 Unity/Darkwood 类型完全隔离。
- [ ] 统一 `InventoryRevision`、`EntityRevision`、`SnapshotServerTick` 为可比较的 `StateVersion`。
- [ ] 为 RuntimeEntityId 增加 Host 分配、Spawn、Destroy、Reconnect 测试。

### 阶段 C：权威玩法（建议 0.9.x/1.0）

- [ ] 攻击 Request → Host 距离/武器/冷却/目标校验 → 伤害结果。
- [ ] 怪物死亡、掉落生成和掉落物拾取全部由 Host 产生。
- [ ] 门窗开关、路障、陷阱、发电机和一次性事件 Action 化。
- [ ] 任务/剧情事件使用 Host EventId + 幂等缓存，客户端只播放结果。
- [ ] 场景切换期间暂停 Action，完成新快照后恢复。

### 阶段 D：可选的新传输适配

只有拿到真实 SDK 后才开始：

- [ ] 明确 Hermes/DS V4 Pro 的网络 API、线程模型、最大包、可靠性和断线语义。
- [ ] 写 `HermesDsV4Transport`，不修改上层协议。
- [ ] 运行 loopback、丢包、乱序、重连和 5–6 MB 存档压力测试。
- [ ] 保留 Telepathy fallback，至少跨一个 alpha 版本。
- [ ] 协议/传输兼容矩阵和握手错误码更新。

## 7. 关键文件映射

| 责任 | 文件 |
|---|---|
| 插件入口/版本 | `src/...DarkwoodAdapter/Plugin.cs` |
| Host/Client 生命周期 | `DarkwoodAdapterRuntime.cs`、`Network/HandshakeSessions.cs` |
| Envelope/握手/消息 | `Protocol/ProtocolEnvelope.cs`、`HandshakeProtocol.cs`、`ReplicationProtocol.cs` |
| 传输抽象 | `Network/Transport.cs`、`TelepathyServerTransport.cs` |
| EntityId/注册表 | `Core/EntityId.cs`、`Entities/EntityRegistry.cs`、`DarkwoodEntityScanner.cs` |
| 快照 | `Snapshots/*`、`DarkwoodWorldSnapshotCodec.cs` |
| 物品权威 | `DarkwoodContainerTakePatch.cs`、`DarkwoodPlayerInventoryShadow.cs`、`DarkwoodInventoryAdapter.cs` |
| 怪物/门窗镜像 | `DarkwoodEntityReplication.cs` |
| 远端模型 | `DarkwoodRemotePlayers.cs`、`Rendering/RemoteAvatar.cs` |
| 测试 | `SelfTests/Program.cs` |
| 发布 | `Darkwood联机框架-安装包-v0.8.7-alpha.9/`、本地打包脚本 |

## 8. 数据模型与 API 不变量

### ProtocolIdentity

```text
ProtocolVersion = 3
FrameworkVersion = 0.8.7-alpha.9
GameVersion = Application.version
SaveSchemaVersion = 1
SnapshotSchemaVersion = 3
```

### 主机权威不变量

- Client 不能直接提交最终世界状态，只能发送 intent/action。
- 每个 Action 有唯一 RequestId，重复请求必须返回缓存结果且不能重复 Apply。
- 每个实体/容器状态带 Revision；旧 Revision 直接丢弃。
- 容器事务必须同时更新 Host 容器和对应玩家 shadow，再发送结果。
- Snapshot 未完整校验、共享容器未全部应用时，客户端不能 READY。
- 远端输入和姿态必须绑定 peerId，不能信任客户端自报 ID。

## 9. 已知问题和注意事项

当前已确认但尚未完整实现：

- 客户端攻击尚未形成 Host 校验和伤害 Action 闭环。
- 门窗交互和剧情/任务/陷阱事件尚未全部 Action 化。
- Entity registry 全局 digest 在动态对象差异时仍可能不一致；共享容器有特征重绑定兜底。
- `Assembly-CSharp.dll`、BepInEx、Harmony、Telepathy 等第三方/反编译依赖不能未经许可上传公开仓库。
- alpha.8 与 alpha.9 协议不兼容，双端必须使用同一 alpha.9 DLL。
- 任何正式覆盖前先确认 Darkwood 进程已关闭，并保留可回滚备份。
- SelfTests 当前握手 fixture 仍是 `0.8.6-alpha.1 / protocol 1 / snapshot 1`；测试通过不代表已验证 alpha.9 的真实握手身份。
- 握手当前声明 Save Schema 1 / Snapshot Schema 3，但 SaveBundle wire 为 3、WorldSnapshot wire 为 2；迁移首个协议任务必须统一常量或明确拆分三种 wire 版本。
- 本地 alpha.9 源码目录不是 Git 根目录，而 GitHub 工作副本仍可能是旧 alpha.1；开始 Hermes 分支开发前必须锁定唯一 canonical 仓库。
- 公开 Release 中的第三方二进制必须与 `THIRD-PARTY-NOTICES` 一致，不能一边声明不分发、一边在 ZIP 中携带。

## 10. 验收标准

### 自动验收

```powershell
dotnet build '.\src\DarkwoodMultiplayerFramework.sln' -c Release --no-restore -m:1 -p:MSBuildEnableWorkloadResolver=false
dotnet run --project '.\src\DarkwoodMultiplayerFramework.SelfTests\DarkwoodMultiplayerFramework.SelfTests.csproj' -c Release --no-build -p:MSBuildEnableWorkloadResolver=false
```

### 双端验收

1. 双端 alpha.9 DLL SHA256 完全一致。
2. Radmin/局域网握手成功并完成存档、快照和 READY。
3. 同一柜子拿取、放置、拖放、堆叠和交换结果一致。
4. 两端同时操作最后一件物品时只有一个事务成功。
5. 客户端日志出现 `ActionResult` 或明确 `ActionRejected`，主机日志出现对应 request。
6. 断线、F3 停止、重复 Join 后不会遗留本地 cursor item 或错误 READY 状态。

## 11. 需要用户最终确认的两项资料

本方案可以直接指导 Hermes + DS V4 Pro 继续维护当前项目；若你说的“迁移到框架”是要替换游戏运行时网络层，还需要补充：

1. DS V4 Pro 的准确产品链接、SDK/协议文档和 Unity/Mono 支持方式。
2. Hermes 是否只是开发 Agent，还是你们另有一个名为 Hermes 的游戏网络 SDK。

在这两项明确前，不应生成假设性的 `HermesNetworkClient` 或 `DSV4Session` API。

## 12. 已生成的 Hermes 工作文件

本地源码树现在包含可直接复用的项目上下文包：

- `AGENTS.md`：通用 Agent 工作规则。
- `.hermes/project.yaml`：基线、路径、命令、安全边界和验证门。
- `.hermes/context/`：项目、协议、库存、实体快照、Darkwood 适配层、发布和 source-of-truth 说明。
- `.hermes/decisions/`：运行时分层 ADR。
- `.hermes/tasks/active/INV-001-authoritative-inventory-revalidation.yaml`：首个 P0 任务。
- `.hermes/prompts/`：architect、implementer、reviewer、test-release 角色提示词。
- `.hermes/hermes-config.example.yaml`：不含密钥的示例；实际 Provider 和 DS V4 Pro model slug 由用户账号决定。

当前开发机未检测到 `hermes` 命令，所以这些文件尚未绑定某个具体 Hermes 版本的 manifest schema。安装后应先查看该版本帮助/文档，再映射键名；不要反过来为迎合猜测的 schema 改运行时代码。
