# Darkwood Multiplayer Framework 0.8.7-alpha.8

- 柜子取出和放入统一进入 Host-authoritative action transaction。
- 主机按容器版本、距离、目标槽位、库存容量进行验证，成功后修改唯一共享库存。
- 客户端不再根据结果自行猜测加物品，改为应用主机回传的完整背包/快捷栏快照。
- 新增 `ContainerPut` 与玩家库存快照协议，协议版本升级到 2。
- 新增容器放入、玩家库存快照协议自测；完整自测通过。

版本：0.8.7-alpha.8。
