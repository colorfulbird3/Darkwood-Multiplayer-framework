# 物品与共享容器权威模型

## 目标

同一个共享柜子在所有端只有一份状态。Client 的鼠标/手柄操作是意图，不是最终写入；Host 以 revision/CAS 方式验证并原子应用。

## 事务链路

```text
输入入口
  → Client 捕获 intent（目标容器、来源槽位、目标槽位、数量、ExpectedRevision）
  → Host 校验 peer/距离/目标 ID/槽位/数量/容量/revision
  → 原子更新 Host 容器 + 玩家库存 shadow
  → Revision++，缓存 RequestId 结果
  → ActionResult 或 ActionRejected（带 current revision 和完整状态）
  → 广播容器状态与相关玩家库存
  → Client 应用确认；拒绝/超时/断线回滚 cursor、源槽位和目标槽位
```

## 必须覆盖的入口

- 快捷转移。
- `grabItem`、`controllerPickUpItem`。
- `placeItem`、`controllerPlaceItem`。
- `addToItem(int)`、`addToItem(InvItemClass)`（同类堆叠）。
- `swapItems`（异类交换/指定目标槽位）。
- 空槽、满槽、数量不足、距离超限、旧 revision、重复 RequestId、同时抢最后一件。

## 当前已知缺口

- Host 的远端玩家库存 shadow 不能只从本机 `Player.Instance` 初始化；应由 join snapshot 携带客户端库存，或建立受 revision 保护的 inventory intent。
- 拒绝分支必须回传完整玩家库存和容器状态，不能只显示错误文本。
- Host 原版未覆盖入口仍可能先本地改变容器；新增 patch 前要确认 Prefix/Postfix 的阻断语义。
- `deathDrop` 等类型要明确是共享还是每人独立，不能依靠名称猜测。

## VERIFIED 条件

1. 同柜子最后一件物品并发操作只成功一次。
2. 拿取、放置、拖放、堆叠、交换两端最终槽位和数量一致。
3. 拒绝、断线、F3 停止、重复 Join 后没有残留 cursor item。
4. 日志可关联 session、request、peer、目标 ID、旧/新 revision 和结果。
