# Darkwood Multiplayer Framework 0.8.7-alpha.10

本版实现怪物与门窗/物品交互的主机权威同步闭环（任务 SYNC-001）。

## 新增

- `ActionKindWire` 新增 `Attack(4)`、`DoorInteract(5)`、`WindowInteract(6)`、`ItemActivate(7)`；新 payload：`AttackPayload`（近战种类、武器槽位、朝向、位置）、`InteractPayload`。
- **近战攻击权威闭环**：客户端拦截 `Player.attack(float)`，不再本地生成 MeleeSensor；改发 AttackRequest。Host 校验（影子武器必须是近战、姿态容差 2m、0.35s 限速）后按 MeleeSensor 近似弧（半径 1.6m、朝向半锥）解析命中目标，以游戏原生 `getHit` 结算 Character/Door/Window/Item。**伤害数值只由 Host 从影子库存武器推导，客户端不上报伤害。**
- 攻击成功后 Host 扣减影子武器耐久（打坏即移除）并回传完整玩家库存状态。
- **怪物死亡镜像**：客户端收到 `alive 1→0` 时以 `die2()` 本地生成尸体（不调用 `die()`，避免重复触发 `onDeath` 剧情事件）；尸体 `deathDrop` 容器内容以 Host 权威 `InventoryState` 广播为准。
- **门窗/物品交互**：开关门（`Door.openClose(Transform)`）、封窗（`Window.barricade(int,bool)`）、物品开关（`Item.activate()`）拦截为请求，Host 距离 + ExpectedRevision CAS 校验后重放并立即广播；旧版本请求拒绝并回传最新实体状态。
- **容器拒绝自校正**：`SLOT_NOT_FOUND`、`ITEM_EMPTY`、`PLAYER_SLOT_EMPTY`、`CONTAINER_FULL`、`DESTINATION_OCCUPIED` 现在同时回传权威容器状态。
- SelfTests 35 → 43 项（新增攻击/交互 payload roundtrip、未知近战种类、槽位越界、框架版本不一致拒绝等负例）；fixture 改用单一版本常量 `ProtocolVersions`，不再使用旧 0.8.6-alpha.1 fixture。
- 安装包清理：移除遗留 Mirror 与旧命名 DLL，Payload 插件目录现在只含 7 个框架 DLL + Telepathy。

## 版本与兼容性

- 框架版本 `0.8.7-alpha.10`，协议版本 3（不变），Save Schema 1，Snapshot Schema 3。
- alpha.9 与 alpha.10 **不兼容**（握手框架版本校验拒绝），双端必须同时使用 alpha.10。
- 已知的握手字段与内部 SaveBundle（wire 3）/WorldSnapshotWire（schema 2）版本漂移仍未合并（PROTO-001 范围）。

## 已知限制（未同步项）

- 火器/投掷物仍走原版本地逻辑（物理对象同步另需设计）。
- 剧情/任务事件、陷阱、发电机专属逻辑未 Action 化。
- 运行时生成实体（夜间怪物等）的 Spawn/Destroy 同步仍属 P2。
- 远端玩家攻击不计算技能加成（Strong/weak）；近战弧为近似值；封窗/物品开关拦截用 `selectedObject` 启发式——待双端实机调参。

## 验证状态

- `dotnet build` 0 warning / 0 error；SelfTests 43/43 通过（退出码 0）。
- 双端实机未验证：**IMPLEMENTED_UNVERIFIED**。测试重点见安装包 README。

版本：0.8.7-alpha.10。
