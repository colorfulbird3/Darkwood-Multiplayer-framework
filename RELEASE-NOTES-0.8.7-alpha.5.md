# Darkwood Multiplayer Framework 0.8.7-alpha.5

- 修复创建主机后每帧全场景扫描柜子，导致帧率降到个位数的问题。
- 每个场景的已有柜子扩容扫描现在只执行一次。
- 保留 alpha.4 的主机权威共享库存、人数倍率和持久化扩容账本。
- 双方必须使用 alpha.5；旧 alpha.1/alpha.4 会被严格握手拒绝。
