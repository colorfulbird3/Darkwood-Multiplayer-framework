# 参与贡献（CONTRIBUTING）

欢迎为本项目贡献代码、测试或文档！提交的 PR 被合并后，你的 GitHub 贡献记录会公开可见。

## 快速流程

1. **Fork** 本仓库（GitHub 页面右上角 Fork）。
2. **Clone 你的 fork 到本地**。构建需要引用 Darkwood 游戏程序集，请把仓库放在 **Darkwood 游戏安装目录内部**：
   ```
   <SteamLibrary>/steamapps/common/Darkwood/Darkwood Multiplayer framework
   ```
3. 创建分支：`git checkout -b feature/你的改动`
4. 修改代码或文档。
5. 构建与自测（必须全过）：
   ```powershell
   dotnet build '.\src\DarkwoodMultiplayerFramework.sln' -c Release -m:1 -p:MSBuildEnableWorkloadResolver=false
   dotnet run --project '.\src\DarkwoodMultiplayerFramework.SelfTests\DarkwoodMultiplayerFramework.SelfTests.csproj' -c Release --no-build -p:MSBuildEnableWorkloadResolver=false
   ```
   要求：**0 警告 / 0 错误，SelfTests 全部 PASS**。
6. 提交并推送到你的 fork，然后打开 Pull Request 到本仓库 `main` 分支。

## 提交信息规范

使用简洁的描述式标题（中英文均可），例如：

- `fix: 修复客户端容器状态上报丢失槽位数据的问题`
- `docs: 补充容器信任模式的行为说明`
- `test: 新增 InventoryState 转发回归测试`

## 入门任务清单（适合第一个 PR）

按难度排序，任选其一即可：

| 任务 | 说明 | 涉及文件 |
|---|---|---|
| 新增 SelfTests 单元测试 | 为现有纯逻辑补充边界用例（如 `SnapshotTolerance`、`EntityId`、编解码 roundtrip） | `src/DarkwoodMultiplayerFramework.SelfTests/Program.cs` |
| 完善文档 | 补充游戏内操作指南、常见问题（FAQ）、安装排错（防火墙/Radmin 等） | `README.md` 或新增文档 |
| 改进日志信息 | 让关键路径日志更清晰、统一格式、去除噪音 | `src/DarkwoodMultiplayerFramework.DarkwoodAdapter/` |
| 协议注释与说明 | 为 `ReplicationProtocol.cs` 的消息结构补充字段级注释 | `src/DarkwoodMultiplayerFramework.Protocol/` |
| 修复"已知欠账" | 见 `ARCHITECTURE-ROADMAP.zh-CN.md` 的"0.8.7 已知欠账"章节，选取小型条目 | 按条目 |

想做大功能前，建议先开 Issue 与维护者确认方向。

## PR 注意事项

- 一个 PR 只做一件事。
- 不要改动协议消息字段顺序（框架无向下兼容，wire 版本绑定框架版本）。
- 不要提交游戏程序集（`Darkwood_Data/` 下文件）、日志文件、IDE 配置。
- 中文注释与提交信息完全 OK。
- PR 描述请说明：改了什么、为什么、如何验证（构建与自测结果）。

## 联系方式

- Issue：https://github.com/colorfulbird3/Darkwood-Multiplayer-framework/issues
- 论坛招募帖见项目文档。
