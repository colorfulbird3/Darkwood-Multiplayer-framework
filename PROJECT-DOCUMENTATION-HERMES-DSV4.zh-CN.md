# Darkwood Multiplayer Framework
## Hermes + DS V4 Pro 项目文档

> 文档用途：作为 Hermes 的项目级上下文，说明项目背景、运行时架构、核心流程、数据契约、当前状态和后续开发边界。  
> 文档基线：`0.8.7-alpha.11`（2026-08-13）  
> 重要边界：Hermes 是开发 Agent，DS V4 Pro 是开发模型；二者目前不是 Darkwood 的游戏运行时网络 SDK。

---

## 1. 项目定位

Darkwood Multiplayer Framework（DMF）是一个运行在 Darkwood 上的多人联机插件框架。它通过 BepInEx 加载、Harmony 修改游戏行为，并使用自有协议和 Telepathy TCP 在局域网/Radmin VPN 中连接多个游戏实例。

项目的核心目标是：

1. 主机（Host）维护世界、实体和共享物品的唯一权威状态。
2. 客户端（Client）完成版本握手、存档接收、实体注册和世界快照应用后进入 `READY`。
3. 客户端只提交玩家意图/Action，主机验证并广播结果。
4. 新玩家加入一个已经运行的世界时，能够得到与主机一致的世界骨架、实体状态和共享容器状态。

本项目不是独立服务器，也不修改 Darkwood 原始存档格式的设计目标；它是游戏内运行时框架。

## 2. 目录和基线

### Canonical 本地源码

本地 canonical 源码树的 `src\` 目录（路径不公开，避免把本机信息提交到仓库）。

### 运行时和发布目录

```text
游戏目录：<Darkwood 安装目录>
插件目录：<游戏目录>\BepInEx\plugins
Payload：<canonical 树>\Payload
安装包：<游戏目录>\Darkwood联机框架-安装包-v0.8.7-alpha.11
ZIP：<游戏目录>\Darkwood联机框架-安装包-v0.8.7-alpha.11.zip
```

当前发布基线：

```text
Framework       0.8.7-alpha.11
Envelope        3（信封头常量）
握手门槛        FrameworkVersion + GameVersion（无向下兼容，PROTO-001 已定）
SaveBundle wire 3（实现细节，随框架版本绑定）
Snapshot wire   2（实现细节，随框架版本绑定）
```

alpha.11 ZIP 当前 SHA256：

```text
503C27E6DE135739FA27B529ECA7BD1AC2C96FC690D93F1C7731F66EFC4733B1
```

GitHub 工作副本（remote 为 https://github.com/colorfulbird3/Darkwood-Multiplayer-framework）只保留当前版本的源码与文档；canonical 树为开发主树，发布时把源码以普通提交推送到仓库 main（第三方二进制、Payload、安装包与旧版本不进仓库）。禁止在两棵树之间混改。

本地命令行若暂时找不到 `hermes`，先确认 Hermes 的实际安装 profile/PATH；这不影响使用本文档作为项目上下文。不要为了修复 PATH 而覆盖用户全局配置或在项目里保存 API key。

## 3. 实际技术栈

| 层 | 技术/版本 | 作用 |
|---|---|---|
| 游戏加载 | BepInEx 5.4.23.5 | 注入插件并启动框架 |
| 运行时补丁 | Harmony 2.9.0.0 | 拦截 Darkwood 输入、存档和实体行为 |
| 游戏适配 | Darkwood `Assembly-CSharp`、UnityEngine | 读取/修改游戏对象和库存 |
| 网络传输 | Telepathy TCP 1.0.341.0 | Host/Client 字节流传输 |
| 协议编码 | DMF BinaryWriter/BinaryReader codec | Envelope、握手、Action、快照和库存消息 |
| 游戏适配器目标 | .NET Framework 4.7.2 (`net472`) | Unity Mono 运行时 |
| 纯逻辑模块 | `netstandard2.0`/`netstandard2.1` | 不依赖 Unity 的协议和状态逻辑 |
| 自测项目 | `net7.0` | 协议、快照和 Telepathy loopback |

UnityEngine DLL 的文件版本不能用于推断 Unity Editor 版本；源码未声明具体 Unity Editor 版本。

## 4. 分层架构

```text
Darkwood / Unity Mono
        │
        ▼
DarkwoodAdapter（Plugin、Harmony、存档、库存、实体扫描、渲染编排）
        │
        ├── Network（Session、Handshake、ChunkTransfer、Telepathy）
        ├── Protocol（Envelope、DTO、Encode/Decode）
        ├── Snapshots（WorldSnapshot、Assembler、Hash）
        ├── Entities（Persistent/Runtime EntityId、Registry、Digest）
        ├── Actions（RequestId、CAS Revision、幂等结果缓存）
        ├── Inventory（Host 容器事务和玩家库存 shadow）
        └── Rendering（远端玩家模型和插值）
```

核心原则：底层 `Core/Protocol/Network` 不得引用 Unity 或 Darkwood 类型；只有 `DarkwoodAdapter` 可以接触 `Assembly-CSharp`。

## 5. 模块职责和关键文件

| 模块 | 职责 | 关键文件 |
|---|---|---|
| Core | 连接状态、快照阶段、EntityId、StateVersion | `src/DarkwoodMultiplayerFramework.Core` |
| Protocol | Envelope、握手、Replication/Action/Inventory DTO | `src/DarkwoodMultiplayerFramework.Protocol/ProtocolEnvelope.cs`、`HandshakeProtocol.cs`、`ReplicationProtocol.cs` |
| Network | Telepathy、连接会话、握手状态机、分块传输 | `src/DarkwoodMultiplayerFramework.Network/Transport.cs`、`TelepathyServerTransport.cs`、`HandshakeSessions.cs`、`ChunkTransfer.cs` |
| Entities | 稳定实体注册表、digest、重绑定 | `src/DarkwoodMultiplayerFramework.Entities/EntityRegistry.cs`、`src/DarkwoodMultiplayerFramework.DarkwoodAdapter/DarkwoodEntityScanner.cs` |
| Snapshots | 快照模型、wire 编解码、分块组装 | `src/DarkwoodMultiplayerFramework.Snapshots`、`DarkwoodWorldSnapshotCodec.cs` |
| Actions | Action 请求、结果、拒绝、幂等缓存 | `src/DarkwoodMultiplayerFramework.Actions/NetworkAction.cs`、`ActionAuthority.cs` |
| Inventory | 共享容器状态抽象 | `src/DarkwoodMultiplayerFramework.Inventory/AuthoritativeContainer.cs` |
| DarkwoodAdapter | 正式插件入口和游戏行为桥接 | `Plugin.cs`、`DarkwoodAdapterRuntime.cs`、`DarkwoodContainerTakePatch.cs`、`DarkwoodInventoryAdapter.cs` |
| Rendering | 远端角色显示 | `src/DarkwoodMultiplayerFramework.Rendering/RemoteAvatar.cs`、`DarkwoodRemotePlayers.cs` |
| SelfTests | 自动回归测试 | `src/DarkwoodMultiplayerFramework.SelfTests/Program.cs` |

`Bootstrap`、`legacy-reference*` 和旧 Mirror 代码只作为历史/编译参考，不应被误认为当前正式入口或当前传输依赖。

正式游戏入口是 `DarkwoodMultiplayerFramework.DarkwoodAdapter/Plugin.cs`。`Bootstrap/Plugin.cs` 内仍有旧 alpha.1/0.8.6 文本，当前不能作为版本事实或正式构建入口。

## 6. 核心运行流程

### 6.1 Host 创建

```text
F1 / 面板 Create Host
  → 启动 Telepathy Server
  → 读取当前存档和场景
  → 扫描实体并生成 Registry/Digest
  → 等待 Client
```

### 6.2 Client 加入

```text
F2 / 面板 Join
  → Connecting
  → VersionChecking（ClientHello/ServerHello）
  → SaveTransfer（manifest + chunks + SHA256）
  → LoadingSave（写入独立客户端存档目录并加载）
  → BuildingRegistry
  → ApplyingSnapshot（实体 + 共享容器）
  → WorldSnapshotApplied
  → Host 确认 Ready
  → Ready
```

任何阶段失败都必须进入 `Failed/Disconnected`，清理临时存档、pending Action、分块组装器、远端模型和本地拖放 cursor。

### 6.3 实时同步

主机以约 15 Hz 捕获实体状态并发送增量：

- Character：位置、旋转、生命、存活、活动、移动/攻击状态和动画信息。
- Door/Window：开启、阻挡、路障、摧毁和生命字段。
- Item：数量、耐久、开关、搜索、活动和摧毁状态。
- PlayerPose：远端玩家位置、旋转、场景、动画片段和帧。

客户端远端 Character 使用插值，并冻结其 AI，避免客户端 AI 与主机状态竞争。

## 7. 主机权威物品模型

### 事务链路

```text
Client 输入
  → ActionRequest(SessionId, PeerId, RequestId, EntityId, sourceSlot, destinationSlot, amount, ExpectedRevision)
  → Host 校验距离、实体、槽位、容量、数量和 revision
  → 原子更新 Host 容器 + 玩家库存 shadow
  → revision++，缓存 RequestId 结果
  → ActionResult / ActionRejected + 完整容器/玩家库存状态
  → 所有端应用同一权威状态
```

必须覆盖：

- 快捷转移。
- `grabItem`、`controllerPickUpItem`。
- `placeItem`、`controllerPlaceItem`。
- `addToItem(int)`、`addToItem(InvItemClass)` 堆叠。
- `swapItems` 交换。
- 空槽、满槽、数量不足、距离超限、旧 revision、重复请求、并发抢最后一件。

客户端不能直接写共享柜子。拒绝、超时和断线必须恢复来源槽、目标槽和 cursor，并接收 Host 的完整库存状态。

## 8. EntityId 和快照规则

### EntityId

- 持久实体使用存档稳定信息、场景、组件类型、相对路径和同名兄弟序号生成。
- 动态实体由 Host 分配 RuntimeEntityId。
- 禁止使用 `GetInstanceID()`、GameObject 名称或 Instantiate 顺序作为网络身份。
- 重复 ID、关键实体缺失或无法重绑定时不能静默跳过，也不能让 Client 进入 READY。

### WorldSnapshot

快照至少包含：场景、服务器 tick、Registry digest、实体记录和共享容器记录。客户端必须统计 `applied/rebound/missing/conflicts`，所有关键共享容器成功绑定后才能发送 `WorldSnapshotApplied`。

## 9. Hermes + DS V4 Pro 的使用方式

Hermes 负责：

- 读取本项目文档和源码上下文。
- 拆解审计、协议、Host 权威、客户端应用、适配器和发布任务。
- 分析日志、生成测试、审查 diff 和编写发布说明。

DS V4 Pro 负责：

- 协议/架构推理。
- 反编译调用链和日志归因。
- 生成最小代码草案、测试用例和回归清单。

二者不负责：

- 游戏运行时每帧同步。
- 玩家输入最终裁决。
- 直接写 Darkwood 存档或共享容器。
- 替代 Telepathy，除非用户提供真实 Hermes/DS V4 网络 SDK 和完整文档。

给 Hermes 的首条任务建议：

```text
读取 PROJECT-DOCUMENTATION-HERMES-DSV4.zh-CN.md、AGENTS.md 和 .hermes/context/current-status.md。
先执行只读审计，不修改 DLL、安装包或 ZIP。优先处理 PROTO-001：核对握手、SaveBundle、WorldSnapshot 的实际 wire schema，
并把 SelfTests 的旧 alpha.1 fixture 更新计划写成证据表。没有真实双端证据的功能统一标记 IMPLEMENTED_UNVERIFIED。
```

## 10. 当前状态

### VERIFIED

- alpha.11 solution 构建：0 warning、0 error。
- SelfTests：43 项通过，包含 Envelope、握手、分块、Action、快照、Telepathy loopback 以及攻击/交互 payload 负例。
- 已记录 alpha.11 ZIP SHA256：`503C27E6DE135739FA27B529ECA7BD1AC2C96FC690D93F1C7731F66EFC4733B1`。
- 版本契约：无向下兼容，握手只比较 FrameworkVersion + GameVersion（PROTO-001 已定）。

### IMPLEMENTED_UNVERIFIED

- 物品主机权威事务（拿取/放置/拖放/堆叠/交换）、近战攻击权威闭环、怪物死亡镜像、门窗/物品交互 Action 化——代码与自动测试完成，真实双端矩阵未验证。

### BUG_OPEN / 待处理

1. 远端玩家库存 shadow 的初始来源和拒绝回滚需要真实验证与修复（INV-001）。
2. 火器/投掷物不同步；陷阱、发电机专属逻辑、剧情/任务事件尚未 Action 化。
3. 运行时生成实体（夜间怪物/掉落物）的 Spawn/Destroy/Reconnect 同步缺失（P2）。
4. 远端玩家攻击不计算技能加成；近战弧为近似值；封窗/物品开关拦截依赖启发式——需双端实机调参。
5. 公开仓库和 Release 的第三方二进制/许可证声明需要核对。
6. 部分 csproj 的 `HintPath` 依赖本机 `Darkwood_Data/Managed` 和 `Payload`；干净克隆环境需要由用户提供合法的 Darkwood 游戏引用或构建变量，不能把游戏程序集提交到仓库。

## 11. Hermes 开发任务顺序

```text
PROTO-001 版本契约统一
    ↓
INV-001 物品主机权威复验
    ↓
独立代码审查
    ↓
Build + SelfTests
    ↓
双端 Darkwood 实机矩阵
    ↓
EntityRegistry/WorldSnapshot 完整性
    ↓
攻击、门窗、掉落和事件 Action 化
    ↓
在取得真实 SDK 后评估新传输适配器
```

每个任务都必须输出：修改文件、协议/Schema 变化、构建结果、自测结果、实机证据、未验证风险和回滚步骤。

## 12. 构建和验收命令

在项目根目录执行：

```powershell
dotnet build '.\\src\\DarkwoodMultiplayerFramework.sln' -c Release --no-restore -m:1 -p:MSBuildEnableWorkloadResolver=false
dotnet run --project '.\\src\\DarkwoodMultiplayerFramework.SelfTests\\DarkwoodMultiplayerFramework.SelfTests.csproj' -c Release --no-build -p:MSBuildEnableWorkloadResolver=false
```

双端实机验收必须确认：

1. 两端 DLL SHA256 完全一致。
2. 握手、存档、Registry、Snapshot 和 READY 全部完成。
3. 同一柜子的拿、放、拖、叠、换结果一致。
4. 同时抢最后一件物品只有一个成功。
5. 日志出现对应 request、peer、target、旧/新 revision 和 `ActionResult/ActionRejected`。
6. 断线、F3 停止和重复 Join 后没有残留 cursor item 或错误 READY。

## 13. 安全和发布边界

- 只有用户明确要求时才覆盖正式游戏 DLL、生成安装包/ZIP、启动游戏、上传 GitHub 或创建 Release。
- 覆盖前确认 Darkwood 进程已关闭，并保留 alpha.11 回滚包。
- 不公开 `Assembly-CSharp.dll`、反编译源码、个人存档、API key 或许可证不明的第三方二进制。
- 发布 manifest 使用 UTF-8 读取，避免 PowerShell 编码造成假错误。

---

这份文档是项目事实和开发约束的单一阅读入口；细化任务时可再读取本地 canonical 树的 `.hermes/` 上下文文件（不随仓库分发）。
