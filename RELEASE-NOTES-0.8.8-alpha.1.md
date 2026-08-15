# Darkwood Multiplayer Framework 0.8.8-alpha.1

**0.8.8 主开发线启动**：Runtime Entity 生命周期。本版只做协议与数据模型（alpha.1 计划项），游戏行为无变化。

## 新增：Runtime Entity 协议（wire 变更，版本随框架递增）

消息类型（ProtocolEnvelope）：

```text
RuntimeEntitySpawn   = 60
RuntimeEntityDespawn = 61
```

数据模型（`ReplicationProtocol.cs`）：

```csharp
RuntimeEntityKind : byte
    DroppedItem = 1   // 运行时生成的可拾取物品（alpha.3 首个验证目标）
    Enemy       = 2   // 运行时生成的敌人（alpha.4）
    Corpse      = 3   // 敌人死亡产生的尸体

RuntimeEntityDespawnReason : byte
    Collected = 1   // 被拾取
    Died      = 2   // 死亡
    Destroyed = 3   // 被摧毁
    Other     = 255 // 其他（场景清理等）

RuntimeEntitySpawnMessage
{
    RuntimeEntityId  ulong   // 只能由 Host 分配；会话内单调递增、绝不复用
    Kind             RuntimeEntityKind
    PrototypeId      string
    Scene            string
    X/Y/Z            float   // 位置
    Qx/Qy/Qz/Qw      float   // 旋转（四元数）
    InitialState     byte[]  // 预留给 alpha.3+ 的实体专属初始状态
    ServerTick       long
}

RuntimeEntityDespawnMessage
{
    RuntimeEntityId  ulong
    ServerTick       long
    Reason           RuntimeEntityDespawnReason
}
```

**wire 纪律**（按 Roadmap）：RuntimeEntityId 为 0 或未知 Kind/Reason 时解码直接抛异常（协议严格性）；ID 单调递增由 alpha.2 的 RuntimeRegistry 保证，晚到 Despawn 不会误杀新生实体。

## SelfTests

61 → **67**（新增 6 项）：

```
runtime entity spawn roundtrip
runtime entity despawn roundtrip
runtime entity spawn empty state
runtime entity unknown kind
runtime entity despawn unknown reason
runtime entity zero id
```

构建 0 警告 / 0 错误；SelfTests 67/67。

## 说明

- 本版为纯协议预备：主机/客户端尚未发送这些消息（alpha.2 注册表、alpha.3 物品闭环时接入）。
- 无向下兼容：与 0.8.7-beta.1 不互通（握手框架版本不同即拒绝），双方须同版本。
