# Darkwood Multiplayer Framework 0.8.9-beta.5

Authority correctness closeout：修复 beta.4 评审发现的 3 个 P0 + 2 个 P1。

## 修复

### P0

1. **ContainerPut 参数顺序错位**：客户端把"数量"塞进目标槽、"目标槽"塞进数量——已修正为 `(hotbar, slotIndex, destinationSlotIndex, amount)`。
2. **ContainerTake 复制物品风险**：请求 2 个实际给整槽（箱子 10→8、玩家 +10）——已按 `amount` 裁剪 `InvItemClass` 再入背包（`CanAdd` 校验也在裁剪后进行）。
3. **Take Prefix 未真正拦截**：`void Prefix` 发完 Intent 后原版照样本地执行（客户端拿一份 + Host 再拿一份 = 复制）——已改为 `bool Prefix`，客户端共享容器路径明确 `return false`。

### P1

4. **Drop 事务化**：先创建掉落物 + 分配 RuntimeEntityId，全部成功后才扣库存；创建/分配失败不丢物品，扣减失败销毁已创建的临时对象并广播 Despawn。
5. **Snapshot ACK 重试计数双增**（1→3→5）：`ShouldRetrySnapshotAck` 不再自增，仅实际发送时 `RecordSnapshotAckSent` 计数。
6. **FaultInjectingTransport 重连**：`Connect()` 同时清零 `sentCount`（`DisconnectAfterMessages` 重连后重新计数）。

### 表述修正

- RELEASE-NOTES 措辞收窄：**Drop / Pickup / Container 已收口 Host Authority；ItemActivate / Window 等部分交互仍保留信任模型**。

## 测试

- 单元测试 27 项通过
- SelfTests 81 项通过
- 回环自测全链路通过

## 已知问题

- ItemActivate / Window 交互仍是"客户端先执行后上报"（信任模型），尚未收口 Host Authority。
- 客户端拖拽容器内重排会被主机权威广播覆盖（最终一致）。
- 搜尸体（search）物品进背包路径尚未权威化。
- 真机双机测试尚未完成。
