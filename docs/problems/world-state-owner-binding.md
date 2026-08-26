# WorldState Owner-Binding（v0.9.2 框架）

## 状态

Fixed in code — awaiting real-machine verification

## 问题

0.8.9.1 Registry 是 "第一个 CanHandle adapter 赢"——复杂世界对象（Generator / BearTrap）永远不会被 typed adapter 匹配，因为 primary binding 是 Item。

## 修复

- WorldEntityBinding.ComponentMap：同一 EntityId 携带多个 typed state payload
- GeneratorAdapter / LightAdapter / BearTrapAdapter 在 binding.Root 上 GetComponentInChildren（不依赖 primary）
- EntityState.StatePayloads[] bundle：一次 EntityDelta 可附带 GenericItem + Generator + PowerConsumer + VisualLight

## 真机验收

TEST G/H/I：BearTrap / Generator / Reconnect
