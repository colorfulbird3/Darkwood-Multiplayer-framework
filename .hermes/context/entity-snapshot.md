# EntityRegistry 与 WorldSnapshot

## 身份规则

- 持久实体使用存档稳定信息生成 EntityId；禁止 `GetInstanceID()`、GameObject 名称或 Instantiate 顺序。
- 动态实体由 Host 分配 RuntimeEntityId，并在 Spawn/Destroy/Save/Load/Scene change/Reconnect 中保持可追踪。
- Registry 出现重复 ID、关键实体缺失或 digest 无法解释时，必须报告错误并阻止 Client READY；不能静默丢弃。

## 快照最小闭环

```text
Host manifest（scene/time/schema/registry digest/shared inventory digest）
  → Client 接收并校验
  → 建立/重绑实体注册表
  → 应用实体与共享库存状态
  → 统计 applied/rebound/missing/conflicts
  → 全部关键对象成功后发送 WorldSnapshotApplied
  → Host 标记 peer Ready
```

## 当前镜像范围

已有 Character、Door、Window、Item 的部分 Host→Client 镜像；客户端远端 Character 使用插值，AI 被冻结。这不等于完整游戏权威同步。

## 后续顺序

1. 修复 registry manifest、重复 ID 失败和 READY 门禁。
2. 完成 RuntimeEntityId Spawn/Destroy/Reconnect。
3. 把攻击、死亡、掉落、门窗和事件转成 Action/结果消息。
