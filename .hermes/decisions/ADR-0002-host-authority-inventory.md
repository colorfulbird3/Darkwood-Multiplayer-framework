# ADR-0002：共享物品使用 Host 权威事务

- 状态：Proposed for 0.9 work
- 日期：2026-08-13
- 回滚基线：`0.8.7-alpha.9`

## 背景

alpha.9 已在多个 Harmony 入口拦截物品操作，但真实双端测试仍需证明所有旁路写入、拖放回滚和远端玩家库存 shadow 都与 Host 一致。仅在 Postfix 广播本地变化会产生“双方各拿一份”的分叉。

## 决策

共享容器的拿取、放置、拖放、堆叠和交换统一走 `ActionRequest → Host validate/CAS → atomic apply → result/state broadcast`。客户端可以显示短暂输入反馈，但不能提交未经 Host 确认的最终容器状态。

## 必须满足

1. 幂等键为 `(SessionId, PeerId, RequestId)`。
2. 请求携带目标 EntityId、来源/目标槽、数量和 ExpectedRevision。
3. 成功时同时更新 Host 容器和玩家库存 shadow，revision 只增加一次。
4. 拒绝、超时、断线返回完整权威容器和玩家库存，并清除本地 cursor/pending drag。
5. 真实双端并发抢最后一件只允许一个成功。

## 后果

- 需要把客户端库存状态纳入 join snapshot 或建立版本保护的库存意图。
- 未覆盖的 Darkwood 原版入口必须阻断或显式标记为风险，不能默认放行。
- 在双端矩阵完成前，功能状态只能是 `IMPLEMENTED_UNVERIFIED`，不能写成“已完全修复”。
