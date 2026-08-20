# 0.8.9-beta.7 更新说明

## 核心

- 主机权威实体 ID + 绑定清单 + 真实世界稳定门（注册表指纹稳定后一次性提交）。
- 存档传输：Binding 分块 Data/Hash 修复；存档剥离 `graph:null`（修复 byte[] 反序列化崩、角色丢失）。
- Binding Matcher 三阶段按 ComponentType 区分（同 UID 多组件不再误判 ambiguous）。
- 跨会话强制重载 + 回主菜单重载；版本不匹配握手失败打印双方版本；残留世界 fail-fast 明确提示。

## 世界状态（World State Adapter 开端）

- 协议新增 typed 通道（StateSchema/StatePayload），不再把全部对象硬塞 `EntityStateWire` flags。
- 首批 adapter：Character（客户端纯视觉代理：关本地 AI/寻路/决策/Rigidbody kinematic）、GenericItem/BearTrap（幂等赋值）、Door/Window。
- `[WORLD-AUDIT]` 运行期输出未覆盖对象类型。

## 物品事务

- **HeldItem（鼠标手持物品）**：容器 grab 按原版吸附（保留 UI 图标/槽/整堆）；可放回指定背包槽或直接丢地；全 Host 权威。
- Drop 只解析一次；联机下客户端绝不本地生成掉落物。
- 掉落物走 Runtime 实体生命周期（Spawn/Despawn 统一清理镜像/Registry/ID，消灭幽灵包袱）。
- Host 销毁必广播 Despawn（不 silent purge）；Despawn 应用安全（Unity 对象已销毁不抛异常）。
- 容器 Grab 拿整堆。

## 稳定性 / 诊断

- CaptureDeltas / TickHost 子系统异常隔离；遍历改快照（消灭 Collection modified）。
- per-kind 统计、`[WORLD-LIFE]` / `[RUNTIME]` / `[HELD]` / `[RUNTIME-GHOST]` 诊断、F8 指向实体调试。
- 客户端 A* 剥离后防御（WhereAmI 空引用、无图 Request 静默；本地怪物 AI 保持关闭）。

## 安装

解压 → 运行 `安装.bat`。主机进世界按 F6 开主；客户端主菜单 F6 → 输主机 IP → 连接（自动下载存档加载）。客户端请从主菜单连接；提示重启游戏时请重启后再连。日志统一 `BepInEx/LogOutput.log`。

## 限制

- 发电机/灯光/事件类完整权威 transition 未完成（后续版本）。
- 捕兽夹 armed/triggered/occupied 细分同步后续补齐（拆除已同步）。
