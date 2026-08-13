# Darkwood Multiplayer Framework 0.8.7-alpha.16

本版继续修复客户端存档加载问题（FIX-003）：alpha.15 采用"置 `Core.loadingGame=true` 后直接加载章节场景"的正确语义，但实测客户端仍走了生成新世界分支（注册表仍为教学梦境的 8 个实体，摘要与 alpha.13/14 完全相同），且全程序集反编译确认没有任何游戏代码在场景加载期间重置该标志——时序上存在未知干扰。

## 变更

- **强制加载分支（FIX-003）**：新增 Harmony 补丁 `DarkwoodLoadPatch`，在 `WorldGenerator.Start` 入口（场景加载时最先执行的世界入口）强制将 `Core.loadingGame/loadedGame` 置为 true——只要客户端存档加载进行中（`ClientSaveLoadPending`），世界生成器必然走 `SaveManager.Load()` 恢复主机存档分支；同时记录强制前的标志原值用于诊断（若原值已为 true 则说明此前猜测有误，日志可证）。
- **加载看门狗放宽**：180 秒 → 300 秒（主机存档约 9MB 静态世界，弱机恢复可能较慢）。
- **加载进度可视化**：客户端面板"进度"在加载存档期间实时显示 `WorldGenerator.percentLoaded`（百分比），不再只有一个模糊的"正在加载存档"。
- 版本号 0.8.7-alpha.16（双方须同版本）；协议 wire 无变更。

## 验证状态

- `dotnet build` 0 warning / 0 error；SelfTests 54/54。
- 双端实机未验证：**IMPLEMENTED_UNVERIFIED**（等待实机复验）。

版本：0.8.7-alpha.16。
