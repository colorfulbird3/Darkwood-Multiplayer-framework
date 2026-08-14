# Darkwood Multiplayer Framework 0.8.7-alpha.19

本版修复 alpha.18 实机测试暴露的**客户端加载卡 92% 且永远不进入就绪**问题（FIX-006）。alpha.18 的三层图优化全部按预期生效（存档 5.24MB → 1.02MB、14 秒加载到 90%、joinPaths 成功跳过），但加载完成后客户端未能进入联机就绪——根因是**完成回调挂错了对象实例**。

## 根因（FIX-006）

`SaveManager` 是**场景内单例**（非 DontDestroyOnLoad）：客户端在**主菜单场景**把 `onFinishedLoading` 回调挂到当时的 SaveManager 实例上，`LoadScene("chapter1")` 后该实例随主菜单场景销毁；真正执行 `SaveManager.Load()` 的是 chapter1 场景里的**新实例**，其回调列表里只有游戏场景对象的订阅，我们的完成回调从未被触发。后果链：

```text
完成回调不触发
  → 客户端状态永远停在 LoadingSave（不会进入注册表稳定化/Ready）
  → 加载期间 timeScale=0 无人恢复
  → hideLoadingScreen 的延时调用（timeScaleDependent）永不执行
  → 界面永远卡在 92%
```

（92% = `WorldGenerator.onFinished()` 内的 `percentLoaded += 2f`，位于 joinPaths 跳过之后、完成回调触发之前。）

## 变更

- **回调改挂到执行 Load 的实例（FIX-006）**：新增 `SaveManager.Load` 入口 Harmony Prefix，把 `onFinishedLoading` 回调幂等挂到 `__instance`（当前场景的真实实例）；移除主菜单场景中的旧挂载点（挂载时即随场景销毁，无效且误导）。
- **Graph Strip 运行时保护**（评审建议落地）：`graph` 字段出现次数 ≠ 1（缺失/重复）或剥离后 JSON 结构验证失败时，回退为原样传输完整存档；剥离逻辑抽为 Core 纯函数并新增 4 项 SelfTests（54 → 58）。
- 版本号 0.8.7-alpha.19（双方须同版本）；协议 wire 无变更。

## 验证状态

- `dotnet build` 0 warning / 0 error；SelfTests 58/58。
- alpha.18 实机证据：图剥离生效（主机传输 1,024,242 字节）、客户端 14 秒加载至 90%、跳过 joinPaths 日志正常、卡点与根因分析完全吻合（详见上文）。
- 双端实机复验：待测（真机矩阵 VERIFY-001 第一关重跑）。

版本：0.8.7-alpha.19。
