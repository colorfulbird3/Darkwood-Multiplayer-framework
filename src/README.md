# Darkwood Multiplayer Framework 源码重建

这里是完全独立于 v0.7.0 运行时的 v0.8 可维护源码工程。旧发布包仅作为行为研究参考，v0.8 不加载旧 SaveTransfer、Avatar 桥或 Mirror NetworkManager。

## 模块

- `Core`：版本、连接状态和通用结果类型
- `Network`：传输和主机权威协议边界
- `Entities`：持久实体/运行时实体身份
- `Snapshots`：世界快照生命周期
- `Actions`：客户端意图、主机验证和结果广播
- `Inventory`：容器版本与事务
- `Rendering`：远端玩家视觉替身边界

当前 alpha.5 连接链路已经串联：Protocol Envelope → ClientHello/ServerHello → 实时存档强制刷新与分块收发 → 客户端隔离目录安装/加载 → Entity Registry 校验 → 独立 WorldSnapshot（实体与容器）→ 双向应用确认 → READY → 15 Hz 实体/玩家同步与 2 Hz 容器增量同步。

这仍是双端 Darkwood 实测前的 alpha：场景切换权威流程、客户端容器 Action Request/Host 原子事务和更多游戏行为还需继续实现。
