# Darkwood Multiplayer Framework 0.8.8-alpha.2

**0.8.8 第二刀**：RuntimeEntityRegistry 就位（alpha.2 计划项）。本版完成注册表与消息收发骨架，尚未实例化游戏对象（alpha.3 接入）。

## 新增：RuntimeEntityRegistry（Entities 层，与 Persistent Registry 分离）

```text
EntityRegistry
├── PersistentRegistry   ← 存档实体（原有 DarkwoodEntityReplication.entities）
└── RuntimeRegistry      ← 本版新增 RuntimeEntityRegistry
```

**ID 纪律（严格实现，含测试）**：

- `Allocate()` 会话内单调递增（从 1 开始），**绝不复用**——实体移除后其 ID 不再分配给新对象，晚到的 Despawn 包永远无法误杀新生实体。
- `Register` 重复 ID → 返回 false（duplicate spawn 容错，不抛异常）。
- `Remove` 未知 ID → 返回 false（despawn unknown id 容错）。
- `ClearAlive()` 仅清空存活集合（场景切换用），**计数器继续递增**。

## Adapter 接入

- **Host**：`BroadcastRuntimeEntitySpawn(kind, prototypeId, position, rotation, initialState)` → 分配 ID → 登记 → 广播给所有就绪玩家；`BroadcastRuntimeEntityDespawn(id, reason)` → 移除登记 → 广播。未登记的 ID 不广播。
- **Client**：收到 Spawn/Despawn → 维护本地 runtimeRegistry + 日志（`客户端已登记运行时实体` / `客户端已移除运行时实体`）；晚到/重复 Despawn 忽略并警告。**alpha.3 起在此登记点实例化/销毁游戏对象。**

## SelfTests

67 → **73**（新增 6 项）：

```
runtime id monotonic               runtime id never reused
runtime duplicate spawn rejected   runtime despawn unknown id
runtime lifecycle sequence         runtime clear keeps counter
```

构建 0 警告 / 0 错误；SelfTests 73/73。

## 说明

- 协议与 0.8.8-alpha.1 相同（wire 未再变更，仅框架版本递增）。
- 无向下兼容；双方须同版本。
- 下一步 alpha.3：Host 检测运行时生成物（乌鸦/动物尸体等）→ Spawn 广播 → Client 实例化 → 交互/Despawn 闭环（Roadmap 首个最小闭环：Dropped Item）。
