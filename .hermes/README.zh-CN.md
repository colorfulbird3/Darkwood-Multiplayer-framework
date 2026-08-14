# Hermes 项目上下文包

这个目录把 Darkwood Multiplayer Framework 的开发记忆、约束和可执行任务集中保存，供 Hermes 或其他 Agent 读取。

## 目录

| 路径 | 用途 |
|---|---|
| `project.yaml` | 项目元数据、基线、命令和验证门 |
| `context/` | 稳定的项目事实和协议契约 |
| `decisions/` | 架构决策记录（ADR） |
| `tasks/active/` | 当前可执行任务（先处理协议版本门禁，再处理库存 P0） |
| `tasks/done/` | 已完成任务报告（手动归档） |
| `prompts/` | 可复用的审查/发布提示词 |

## 使用方式

1. 先读取 `AGENTS.md`、`.hermes.md` 和 `context/current-status.md`。
2. 只读取与任务相关的领域上下文；协议任务必须读取 `protocol-contract.md`，物品任务必须读取 `inventory-authority.md`。
3. 先执行任务 YAML 的 `preconditions`，再修改指定 `allowed_paths`。
4. 通过所有 `validation_gates` 后，更新任务状态和报告；未实机验证的功能标记为 `IMPLEMENTED_UNVERIFIED`。

## Hermes 安装状态

当前开发机未检测到 `hermes` 命令。因此这些文件先按通用 workspace/context 形式保存；安装 Hermes 后，将本目录映射到其支持的项目上下文入口即可。不要为了“接入”而修改全局 `~/.hermes/config.yaml`，也不要在这里保存密钥。

## 运行时边界

Hermes + DS V4 Pro 是开发辅助层，不是 Darkwood 的网络运行时。运行时传输仍是 Telepathy；只有在拿到真实 SDK 文档和许可证后，才新增供应商适配器。
