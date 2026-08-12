# Darkwood Multiplayer Framework 0.8.6-alpha.1

本版本收口 0.8.x 的 Action Core，只实现第一条主机权威行为：Pickup。

## 已实现

- 新增 `ActionRequest`、`ActionResult`、`ActionRejected` 协议消息。
- 客户端 READY 后拦截原生掉落物拾取，不再先修改本地权威世界。
- Host 验证连接状态、玩家身份、实体、revision、可拾取状态、玩家位置、4.5 米距离和背包容量。
- Host Apply once：更新远端背包影子、清空物品、权威 Despawn，并广播实体增量。
- Client 仅在收到成功结果后将物品加入本地背包。
- 有界 2048 条幂等缓存；重复 `RequestId` 重放完整原结果，不重复生成物品。
- 请求缓存绑定来源 peer，拒绝跨客户端 RequestId 碰撞。

## 验证

- Release 全解决方案编译：0 warning / 0 error。
- 31 项自测通过，其中 9 项覆盖 Action/Pickup 协议、幂等、revision 和距离边界。

## 仍未完成

- 尚未进行两台真实 Darkwood 客户端的 Pickup 实测。
- Drop、Door、Attack、Craft、Runtime Spawn/Despawn、Scene Transition 和 Reconnect 不属于本版本。
