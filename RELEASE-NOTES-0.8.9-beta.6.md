# Darkwood Multiplayer Framework 0.8.9-beta.6

物品同步补全 + 实体身份/绑定重构（Host 权威 EntityId）。

## 实体身份 / 绑定（identity rework）

实机问题：Host/Client 加载同一存档后独立 hash 生成 EntityId，对象集合与 hierarchy 不完全一致导致大量 ID 不一致（registry 3240 vs 3236），实体/怪物/物品/门"各玩各的"，Action 请求 ITEM_NOT_FOUND。

1. **Host 唯一 EntityId authority**：主机 World Stable（实体计数连续 3 秒不变）后构建一次权威注册表，删除 90 秒延迟重建；注册表代际（generation）递增，客户端按代际清空旧映射重绑。
2. **EntityBindingManifest**：主机把权威描述符（ID/kind/组件类型/Saveable uid/相对路径/名称/位置）分块随快照发给客户端。
3. **客户端显式绑定**：本地候选扫描（不再生成网络 ID）→ 匹配优先级：uid+type+path → uid+type+position 容差 → kind+name+position；禁止无约束最近邻，ambiguous 显式报告不绑定。
4. **双向映射**：绑定后所有 Apply（EntityState/InventoryState/Despawn/Action target）走权威 ID；ActionRequest 经 localToAuthoritative 查主机 ID。
5. **ApplyStats**：Apply 不再静默跳过，返回 received/applied/missing/stale/ambiguous + missing 前 20 详情；快照日志报真实 applied 数。
6. **Ready Gate**：Character 关键类别 unmatched 禁止 Ready（详细诊断）；非关键类别走容差。
7. **未绑定 Character 冻结**：客户端绑定完成后未匹配的本地 Character 禁止静默本地模拟。
8. **[SYNC] 诊断**：主机/客户端每 10 秒输出 generation/实体数/delta 收发/绑定统计。

## 修复

1. **Drop 来源语义（物品互通根因）**：`DropItemPayload` 增加 `DropOrigin`——手上物品的来源是容器（共享容器/尸体/商人等）时，客户端发送容器实体 ID + 槽位，主机从权威容器扣减并广播容器状态；只有来自玩家自己背包/快捷栏的丢弃才按影子背包槽位解读。此前手上容器物品会被当成玩家背包槽位，导致丢错物品或直接拒绝。
2. **背包漂移收敛**：客户端操作被主机拒绝（槽位空/数量不足/槽位越界等）时，上报真实背包状态，主机重建该玩家的影子背包并自动重试一次——治愈客户端本地合成/搜尸体等未走意图路径的背包增益造成的影子漂移（此前表现为"东西扔不出去/捡不起来/拿取被拒"）。
3. wire 变更（DropItemPayload 扩展 + 新消息 EntityBindingManifest/Chunk）→ Framework 版本升至 0.8.9-beta.6（握手强制匹配，双端必须同版本）。

## 测试

- 单元测试 40 项通过（+11 绑定：sibling 顺序不同仍绑定 / Host 多对象 / Client 少对象 / 同名不误绑 / unknown missing / 权威 id / ambiguous 报告 / generation 换代 / 真实 applied 统计 / Ready gate）
- SelfTests 85 项通过
- 回环自测全链路通过

## 已知问题

- 客户端合成产物进背包仍走本地路径（漂移由"背包漂移收敛"兜底，首个槽位操作可能被拒后自动重试）。
- 合成溢出（spawnDroppedInvItemm）仍为本地生成，其他玩家不可见。
- ItemActivate / Window 交互仍是信任模型（客户端先执行后上报）。
- 客户端拖拽容器内重排会被主机权威广播覆盖（最终一致）。
- 真机双机测试尚未完成。
