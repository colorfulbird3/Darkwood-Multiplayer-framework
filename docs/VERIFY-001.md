# VERIFY-001 — 核心玩法矩阵真机验证

> 状态：**通过（核心玩法）**。版本：0.8.7-alpha.23（封版为 0.8.7-beta.1）。

## 测试环境

| 项 | 值 |
|---|---|
| 测试版本 | 0.8.7-alpha.23（Host 与 Client 同版本） |
| Host | Darkwood Unity 2021.3.30，Windows 10/11 64 位，Steam 版 |
| Client | Darkwood 同构建，Windows 64 位 |
| 网络 | Radmin VPN（26.9.29.104），TCP 17777 |
| 场景 | chapter1（同一主机存档世界） |
| 验证方式 | 双方实机操作 + 两端 BepInEx 日志交叉核对 |

## 结果

### PASS（有实机日志证据）

| 项 | 证据 |
|---|---|
| Host → Client 加入 | `已接受玩家连接：1` / `握手成功。玩家 ID` |
| Save transfer | 存档 1.02–1.09 MB、8–9 块 100% 送达，`Peer 1 installed verified save` |
| Save load（客户端） | `存档加载完成回调已触发（用时 11.5 秒）`，导航图剥离生效（-64%） |
| Registry | 主机 3269–3274 实体；客户端注册表稳定化后摘要一致 |
| Snapshot | 快照 7 块 100% 送达、应用完成，`Peer 1 READY after applying snapshot` |
| 双方互相可见 | 两端 `远端模型已创建：玩家 N，渲染器 14，动画器 3` |
| Door（客户端操作→主机同步） | `主机已批准开关门 …：玩家 1，门 world:…` |
| Item Activate（柜子/物品开关） | `主机已应用物品开关 … isOn=False`（信任模式，无拒绝） |
| Container Take / Put（客户端本地执行→上报→转发） | `客户端已上报共享容器状态` / `主机已应用客户端容器状态并转发：ID=…，版本 3687/3688`；物品同步双方一致（玩家确认） |

### 未覆盖（待后续矩阵）

以下项在本次实机中**未产生可核对证据**，标记为未覆盖，不视为通过：

- Pickup（地面拾取）
- Window（封窗）
- Client Attack / Monster Damage（近战与怪物伤害结算）
- Downed / Rescue（倒地与营救）
- Disconnect / Reconnect（断线重连）
- Hot Join（游戏进行中热加入）
- 容器并发（双客户端同时操作同一容器——0.8.7-beta.2 Container Revision 后专项测）

## 已知偏差（按设计容忍）

- 快照绑定 753/755：主机运行时生成物（乌鸦群、动物尸体）客户端世界缺失，FIX-007 容忍跳过（≤5%），等待 0.8.8 Runtime Entity 生命周期补发。
- 注册表摘要两端短暂不一致（`registry digest differs`）后由权威快照收敛——设计内行为。

## 结论

加载链、注册表、快照、双方可见、门/物品/容器交互与物品同步在真机双端验证通过，核心玩法可玩。**封版 0.8.7-beta.1。**
