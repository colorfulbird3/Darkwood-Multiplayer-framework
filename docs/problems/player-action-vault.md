# Player Action (Vault/Door) Client Authority（v0.9.2）

## 状态

Fixed in code — awaiting real-machine verification

## 计划

Phase 2 玩家动作 Client Authority：Client 原版执行翻窗/开门/交互动画 → PlayerActionEvent{playerId, actionType, targetEntityId, position, velocity, animationState, sequence} → Host Relay → 其他 Client 播放。

## P0-J WhereAmI NRE

远程 Player Proxy 上 WhereAmI 直接 disabled；本地 Player 对 Astar graph / location / zone / controller reference 全 null-safe。翻窗不再 NRE 卡死。

## 真机验收

TEST F：Vault 30 次（0 freeze / 0 WhereAmI NRE / 0 stuck animation）
