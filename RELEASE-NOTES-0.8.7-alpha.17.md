# Darkwood Multiplayer Framework 0.8.7-alpha.17

本版修复客户端加载存档**卡在 92%** 的问题（FIX-004）：alpha.16 强制加载分支生效后，客户端成功进入 `SaveManager.Load()` 恢复主机世界（加载进度到 92%），但 `Load()` 最后阶段的 `WorldGenerator.onLoadedSave()` 会调用 **`joinPaths()`**——对每个区块的 A* GridGraph 执行 `OnPostScan` 全图后处理，大世界在弱机上耗时数分钟以上，且它排在 `onFinishedLoading` 回调之前，导致客户端永远到不了 Ready（主机面板"握手完成=否"、双方看不到彼此）。

## 变更

- **客户端跳过 joinPaths()（FIX-004）**：新增 Harmony 补丁——客户端联机存档加载期间，`WorldGenerator.joinPaths()` 直接跳过并记录日志。客户端是视觉镜像：怪物 AI 冻结、玩家移动不依赖本地寻路，跳过导航图连接是安全的（代价：客户端本地寻路系统不可用，仅影响单机式 AI，联机不受影响）。
- **加载完成诊断**：`onFinishedLoading` 回调触发时记录总用时；若 `Time.timeScale` 仍为 0（加载期间被冻结），强制恢复为 1（防止加载完成后游戏假死）。
- 版本号 0.8.7-alpha.17（双方须同版本）；协议 wire 无变更。

## 验证状态

- `dotnet build` 0 warning / 0 error；SelfTests 54/54。
- 双端实机未验证：**IMPLEMENTED_UNVERIFIED**（等待实机复验）。

版本：0.8.7-alpha.17。
