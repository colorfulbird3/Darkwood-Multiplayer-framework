# Darkwood Multiplayer Framework 0.8.7-alpha.15

本版修复双端实机暴露的**客户端卡在存档加载界面**问题（FIX-002）：客户端此前使用 `initLoadGame()` 加载主机存档，但该方法内部先执行 `initNewGame()`（新游戏初始化），会把 `Core.loadingGame` 重置为 false——场景加载时 WorldGenerator 因此走"生成新世界"分支（只会生成教学梦境，约 8 个实体），而不是 `SaveManager.Load()` 恢复主机存档世界；同时 `onFinishedLoading` 回调不触发，客户端永远卡在加载界面（alpha.14 的 Ready 门控因此也等不到任何信号）。

## 变更

- **正确存档加载路径（FIX-002）**：复刻原版读取语义——设置 `Core.loadingGame=true` 后直接加载章节场景（按档案 chapter 选 chapter1/chapter2），WorldGenerator.Start 检测到该标志会走 `SaveManager.Load()` 恢复主机存档世界，完成后正常回调 `onFinishedLoading` → 注册表构建（此时注册表将包含主机世界的完整对象集）→ 稳定化 → Ready。
- **加载看门狗**：LoadingSave 状态超过 180 秒未完成 → 报 `SAVE_LOAD_TIMEOUT` 明确失败（不再无限卡加载界面）。
- **放宽客户端按键状态**：F2 加入不再要求必须位于主菜单（从主菜单或游戏内均可触发加载）。
- 版本号 0.8.7-alpha.15（双方须同版本）；协议 wire 无变更。

## 验证状态

- `dotnet build` 0 warning / 0 error；SelfTests 54/54。
- 双端实机未验证：**IMPLEMENTED_UNVERIFIED**（本版即修复"客户端卡加载界面"的版本，等待实机复验）。

版本：0.8.7-alpha.15。
